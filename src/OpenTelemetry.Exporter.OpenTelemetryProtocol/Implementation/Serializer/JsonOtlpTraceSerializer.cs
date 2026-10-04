// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

#if NET8_0_OR_GREATER

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace OpenTelemetry.Exporter.OpenTelemetryProtocol.Implementation.Serializer;

internal static class JsonOtlpTraceSerializer
{
    private const int TraceIdSize = 16;
    private const int SpanIdSize = 8;

    private static readonly char[] HexChars = "0123456789abcdef".ToCharArray();

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        // SkipValidation is safe with Indented = false: neither WriteEndSlow's
        // ValidateEnd nor UpdateBitStackOnStart touches the bit stack, so the two
        // settings are self-consistent. Depth is still checked against MaxDepth, and
        // the deepest document OTLP can produce here is far below the default of 1000.
        SkipValidation = true,
        Indented = false,
    };

    [ThreadStatic]
    private static Stack<List<Activity>>? activityListPool;
    [ThreadStatic]
    private static Dictionary<string, List<Activity>>? scopeTracesList;

    [ThreadStatic]
    private static Utf8JsonWriter? jsonWriter;

    internal static int WriteTraceData(
        ref byte[] buffer,
        int writePosition,
        SdkLimitOptions sdkLimitOptions,
        Resources.Resource? resource,
        in Batch<Activity> batch,
        int maxBufferSize = ProtobufSerializer.MaxBufferSize)
    {
        activityListPool ??= [];
        scopeTracesList ??= [];

        // Note: The grouped batch is held in thread-static state, so it has to be
        // released even when serialization fails. TryWriteResourceSpans rethrows
        // once the buffer cannot be grown any further; leaving the batch behind
        // would merge it into the next export on this thread.
        try
        {
            foreach (var activity in batch)
            {
                var sourceName = activity.Source.Name;
                if (!scopeTracesList.TryGetValue(sourceName, out var activities))
                {
                    activities = activityListPool.Count > 0 ? activityListPool.Pop() : [];
                    scopeTracesList[sourceName] = activities;
                }

                activities.Add(activity);
            }

            writePosition = TryWriteResourceSpans(ref buffer, writePosition, sdkLimitOptions, resource, maxBufferSize);
        }
        finally
        {
            ReturnActivityListToPool();
        }

        return writePosition;
    }

    internal static int TryWriteResourceSpans(
        ref byte[] buffer,
        int writePosition,
        SdkLimitOptions sdkLimitOptions,
        Resources.Resource? resource,
        int maxBufferSize = ProtobufSerializer.MaxBufferSize)
    {
        while (true)
        {
            var entryWritePosition = writePosition;

            try
            {
                // A new JsonBufferWriter per attempt, because a retry hands us a
                // different (larger) array and the previous one is still bound to the
                // old one. JSON has no length prefixes to back-fill, so unlike protobuf
                // there is nothing to correct between attempts.
                var bufferWriter = new JsonBufferWriter(buffer, writePosition);
                var writer = GetOrCreateWriter(bufferWriter);

                WriteResourceSpans(writer, sdkLimitOptions, resource);
                writer.Flush();

                // Serialization succeeded, return the final write position
                return bufferWriter.BytesWritten;
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException)
            {
                // FATAL STATE: The writer failed mid-document (Depth > 0). 
                // It cannot be Reset. Null it out so the next iteration creates a new one.
                // on startup we expect to see this line of code slow down the exporter as it needs to re-allocate memory again and again until the size is enough to handle most traces
                // on hot path we expect the size of the writer to already be big enough to be able to handle most traces and therefore not hit this code path at all
                jsonWriter = null;

                // Reset write position and attempt to increase the buffer size
                writePosition = entryWritePosition;

                if (!ProtobufSerializer.IncreaseBufferSize(ref buffer, OtlpSignalType.Traces, maxBufferSize))
                {
                    throw;
                }

                // Continue the loop to retry serialization with the larger buffer. The loop
                // is bounded by IncreaseBufferSize, which refuses to grow beyond beyond
                // ProtobufSerializer.MaxBufferSize, so this cannot become an infinite loop.
            }
        }
    }

    internal static void ReturnActivityListToPool()
    {
        if (scopeTracesList is { Count: > 0 })
        {
            foreach (var entry in scopeTracesList)
            {
                entry.Value.Clear();
                activityListPool?.Push(entry.Value);
            }

            scopeTracesList.Clear();
        }
    }

    private static Utf8JsonWriter GetOrCreateWriter(JsonBufferWriter bufferWriter)
    {
        if (jsonWriter == null)
        {
            jsonWriter = new Utf8JsonWriter(bufferWriter, WriterOptions);
        }
        else
        {
            jsonWriter.Reset(bufferWriter);
        }

        return jsonWriter;
    }

    // Note: Resource attributes, scope attributes, span attributes, events, links and
    // status are not written yet. They need JsonOtlpTagWriter, which is the next step.
    // Everything below emits a valid OTLP/JSON document without them.
    private static void WriteResourceSpans(Utf8JsonWriter writer, SdkLimitOptions sdkLimitOptions, Resources.Resource? resource)
    {
        writer.WriteStartObject();

        writer.WritePropertyName("resourceSpans");
        writer.WriteStartArray();

        writer.WriteStartObject();

        writer.WritePropertyName("resource");
        writer.WriteStartObject();
        writer.WriteEndObject();

        writer.WritePropertyName("scopeSpans");
        writer.WriteStartArray();

        if (scopeTracesList != null)
        {
            foreach (var entry in scopeTracesList)
            {
                WriteScopeSpan(writer, entry.Value[0].Source, entry.Value);
            }
        }

        writer.WriteEndArray();

        writer.WriteEndObject();

        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    private static void WriteScopeSpan(Utf8JsonWriter writer, ActivitySource activitySource, List<Activity> activities)
    {
        writer.WriteStartObject();

        writer.WritePropertyName("scope");
        writer.WriteStartObject();
        writer.WriteString("name", activitySource.Name);
        if (activitySource.Version != null)
        {
            writer.WriteString("version", activitySource.Version);
        }

        writer.WriteEndObject();

        writer.WritePropertyName("spans");
        writer.WriteStartArray();

        for (var i = 0; i < activities.Count; i++)
        {
            WriteSpan(writer, activities[i]);
        }

        writer.WriteEndArray();

        if (!string.IsNullOrEmpty(activitySource.TelemetrySchemaUrl))
        {
            writer.WriteString("schemaUrl", activitySource.TelemetrySchemaUrl);
        }

        writer.WriteEndObject();
    }

    private static void WriteSpan(Utf8JsonWriter writer, Activity activity)
    {
        writer.WriteStartObject();

        // Ids are hex strings, not base64, per the OTLP JSON encoding rules.
        WriteTraceId(writer, "traceId", activity.TraceId);
        WriteSpanId(writer, "spanId", activity.SpanId);
        if (activity.ParentSpanId != default)
        {
            WriteSpanId(writer, "parentSpanId", activity.ParentSpanId);
        }

        writer.WriteString("name", activity.DisplayName);

        // Enums are emitted as integers, so this keeps protobuf's +1 that reserves 0
        // for SPAN_KIND_UNSPECIFIED.
        writer.WriteNumber("kind", (int)activity.Kind + 1);

        // 64-bit integers are emitted as decimal strings.
        var startTimeUnixNano = activity.StartTimeUtc.ToUnixTimeNanoseconds();
        WriteUnixNanoAsString(writer, "startTimeUnixNano", startTimeUnixNano);
        WriteUnixNanoAsString(writer, "endTimeUnixNano", startTimeUnixNano + activity.Duration.ToNanoseconds());

        writer.WriteEndObject();
    }

    private static void WriteTraceId(Utf8JsonWriter writer, string propertyName, ActivityTraceId traceId)
    {
        Span<byte> traceIdBytes = stackalloc byte[TraceIdSize];
        traceId.CopyTo(traceIdBytes);
        WriteHexId(writer, propertyName, traceIdBytes);
    }

    private static void WriteSpanId(Utf8JsonWriter writer, string propertyName, ActivitySpanId spanId)
    {
        Span<byte> spanIdBytes = stackalloc byte[SpanIdSize];
        spanId.CopyTo(spanIdBytes);
        WriteHexId(writer, propertyName, spanIdBytes);
    }

    // Note: Convert.ToHexString always uppercases, and on net8.0 it has no casing
    // overload at all. This keeps the ids lowercase, matching both the protobuf
    // serializer's raw bytes and ActivityTraceId.ToHexString, without allocating.
    private static void WriteHexId(Utf8JsonWriter writer, string propertyName, ReadOnlySpan<byte> id)
    {
        Span<char> hex = stackalloc char[id.Length * 2];

        for (var i = 0; i < id.Length; i++)
        {
            hex[i * 2] = HexChars[id[i] >> 4];
            hex[(i * 2) + 1] = HexChars[id[i] & 0x0F];
        }

        writer.WriteString(propertyName, hex);
    }

    private static void WriteUnixNanoAsString(Utf8JsonWriter writer, string propertyName, long unixNano)
    {
        Span<char> digits = stackalloc char[20];
        ((ulong)unixNano).TryFormat(digits, out var written, default, CultureInfo.InvariantCulture);
        writer.WriteString(propertyName, digits[..written]);
    }
}

#endif
