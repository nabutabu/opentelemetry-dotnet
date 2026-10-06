// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

#if NET8_0_OR_GREATER

using System.Diagnostics;
using System.Text.Json;
using OpenTelemetry.Trace;

namespace OpenTelemetry.Exporter.OpenTelemetryProtocol.Implementation.Serializer;

/// <summary>
/// Serializes a batch of activities under OTLP's <c>application/json</c> mapping.
/// </summary>
/// <remarks>
/// Field order mirrors <see cref="ProtobufOtlpTraceSerializer"/> exactly, including its two
/// departures from field-number order (<c>flags</c> immediately after <c>parentSpanId</c>,
/// and <c>name</c> before <c>timeUnixNano</c> in events). Being able to diff the two
/// serializers line for line is worth more than field-number tidiness.
/// <para/>
/// Keys are lowerCamelCase, enums are integers, 64-bit integers are decimal strings, and
/// the <c>AnyValue</c> oneof collapses so only the set field appears.
/// </remarks>
internal static class JsonOtlpTraceSerializer
{
    private const string UnsetStatusCodeTagValue = "UNSET";
    private const string OkStatusCodeTagValue = "OK";
    private const string ErrorStatusCodeTagValue = "ERROR";

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

            var bufferWriter = new JsonBufferWriter(buffer, writePosition);
            var writer = GetOrCreateWriter(bufferWriter);
            var complete = false;

            try
            {
                WriteResourceSpans(writer, sdkLimitOptions, resource);
                writer.Flush();
                complete = true;
                return bufferWriter.BytesWritten;
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException)
            {
                // Retry with larger buffer.
                writePosition = entryWritePosition;

                if (!ProtobufSerializer.IncreaseBufferSize(ref buffer, OtlpSignalType.Traces, maxBufferSize))
                {
                    throw;
                }

                // Continue the loop to retry serialization with the larger buffer. The loop
                // is bounded by IncreaseBufferSize, which refuses to grow beyond
                // ProtobufSerializer.MaxBufferSize, so this cannot become an infinite loop.
            }
            finally
            {
                // Dropping the writer is what makes a retry possible at all:
                // Utf8JsonWriter.Reset requires that no object or array is open, so on the
                // growth path - where the document is always mid-write - Reset replaces
                // the ArgumentException that triggered the retry with an
                // InvalidOperationException that this filter does not catch. Discarding it
                // costs one allocation on the growth path only; the next attempt allocates a
                // replacement. It is never disposed, because that nulls the output and would
                // make the thread-static instance permanently unusable.
                //
                // complete is false for *any* exception, not just the two filtered types:
                // an unsupported attribute escaping mid-document would otherwise leave a
                // writer that poisons the next export on this thread. The invariant this
                // maintains is that a stored writer is always post-Flush and complete,
                // which is what GetOrCreateWriter relies on when it calls Reset.
                if (!complete)
                {
                    jsonWriter = null;
                }
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
            jsonWriter = new Utf8JsonWriter(bufferWriter, JsonOtlpHelper.WriterOptions);
        }
        else
        {
            jsonWriter.Reset(bufferWriter);
        }

        return jsonWriter;
    }

    private static void WriteResourceSpans(Utf8JsonWriter writer, SdkLimitOptions sdkLimitOptions, Resources.Resource? resource)
    {
        writer.WriteStartObject();

        writer.WritePropertyName(JsonOtlpTraceFieldNameConstants.ResourceSpans);
        writer.WriteStartArray();

        writer.WriteStartObject();

        JsonOtlpResourceSerializer.WriteResource(writer, resource);

        if (resource?.SchemaUrl is { Length: > 0 } schemaUrl)
        {
            writer.WriteString(JsonOtlpTraceFieldNameConstants.SchemaUrl, schemaUrl);
        }

        writer.WritePropertyName(JsonOtlpTraceFieldNameConstants.ScopeSpans);
        writer.WriteStartArray();

        if (scopeTracesList != null)
        {
            foreach (var entry in scopeTracesList)
            {
                WriteScopeSpan(writer, sdkLimitOptions, entry.Value[0].Source, entry.Value);
            }
        }

        writer.WriteEndArray();

        writer.WriteEndObject();

        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    private static void WriteScopeSpan(Utf8JsonWriter writer, SdkLimitOptions sdkLimitOptions, ActivitySource activitySource, List<Activity> activities)
    {
        writer.WriteStartObject();

        writer.WritePropertyName(JsonOtlpTraceFieldNameConstants.Scope);
        writer.WriteStartObject();

        writer.WriteString(JsonOtlpCommonFieldNameConstants.Name, activitySource.Name);
        if (activitySource.Version != null)
        {
            writer.WriteString(JsonOtlpCommonFieldNameConstants.Version, activitySource.Version);
        }

        // Note: the dropped count has to be written before the "scope" object is closed.
        // dropped_attributes_count is field 3 of InstrumentationScope, so it belongs
        // inside "scope", not alongside it in the scopeSpans entry.
        WriteActivitySourceTags(writer, sdkLimitOptions, activitySource);

        writer.WriteEndObject();

        writer.WritePropertyName(JsonOtlpTraceFieldNameConstants.Spans);
        writer.WriteStartArray();

        for (var i = 0; i < activities.Count; i++)
        {
            WriteSpan(writer, sdkLimitOptions, activities[i]);
        }

        writer.WriteEndArray();

        if (!string.IsNullOrEmpty(activitySource.TelemetrySchemaUrl))
        {
            writer.WriteString(JsonOtlpTraceFieldNameConstants.SchemaUrl, activitySource.TelemetrySchemaUrl);
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes the <c>ActivitySource</c> tags into the open <c>"scope"</c> object,
    /// followed by <c>droppedAttributesCount</c> when any were dropped.
    /// </summary>
    /// <param name="writer">The writer to write to.</param>
    /// <param name="sdkLimitOptions">The SDK limit options.</param>
    /// <param name="activitySource">The activity source whose tags to write.</param>
    private static void WriteActivitySourceTags(Utf8JsonWriter writer, SdkLimitOptions sdkLimitOptions, ActivitySource activitySource)
    {
        var tags = activitySource.Tags;
        if (tags == null)
        {
            return;
        }

        var maxAttributeCount = sdkLimitOptions.SpanAttributeCountLimit ?? int.MaxValue;
        var maxAttributeValueLength = sdkLimitOptions.AttributeValueLengthLimit ?? int.MaxValue;
        var tagCount = 0;
        var droppedTagCount = 0;
        var writeAttributes = false;

        if (tags is IReadOnlyList<KeyValuePair<string, object?>> activitySourceTagsList)
        {
            for (var i = 0; i < activitySourceTagsList.Count; i++)
            {
                TryWriteScopeAttribute(
                    writer,
                    ref writeAttributes,
                    ref tagCount,
                    ref droppedTagCount,
                    maxAttributeCount,
                    activitySourceTagsList[i],
                    maxAttributeValueLength);
            }
        }
        else
        {
            foreach (var tag in tags)
            {
                TryWriteScopeAttribute(
                    writer,
                    ref writeAttributes,
                    ref tagCount,
                    ref droppedTagCount,
                    maxAttributeCount,
                    tag,
                    maxAttributeValueLength);
            }
        }

        if (writeAttributes)
        {
            writer.WriteEndArray();
        }

        if (droppedTagCount > 0)
        {
            writer.WriteNumber(JsonOtlpCommonFieldNameConstants.DroppedAttributesCount, droppedTagCount);
        }
    }

    private static void WriteSpan(Utf8JsonWriter writer, SdkLimitOptions sdkLimitOptions, Activity activity)
    {
        writer.WriteStartObject();

        // Ids are hex strings, not base64, per the OTLP JSON encoding rules.
        JsonOtlpHelper.WriteTraceId(writer, JsonOtlpTraceFieldNameConstants.TraceId, activity.TraceId);
        JsonOtlpHelper.WriteSpanId(writer, JsonOtlpTraceFieldNameConstants.SpanId, activity.SpanId);

        if (activity.TraceStateString != null)
        {
            writer.WriteString(JsonOtlpTraceFieldNameConstants.TraceState, activity.TraceStateString);
        }

        if (activity.ParentSpanId != default)
        {
            JsonOtlpHelper.WriteSpanId(writer, JsonOtlpTraceFieldNameConstants.ParentSpanId, activity.ParentSpanId);
        }

        // Note: protobuf writes flags here, before name, even though its field number is
        // 16. Mirrored deliberately.
        WriteTraceFlags(writer, activity.ActivityTraceFlags, activity.HasRemoteParent, JsonOtlpTraceFieldNameConstants.Flags);

        writer.WriteString(JsonOtlpCommonFieldNameConstants.Name, activity.DisplayName);

        // Enums are emitted as integers, so this keeps protobuf's +1 that reserves 0
        // for SPAN_KIND_UNSPECIFIED.
        writer.WriteNumber(JsonOtlpTraceFieldNameConstants.Kind, (int)activity.Kind + 1);

        // 64-bit integers are emitted as decimal strings.
        var startTimeUnixNano = activity.StartTimeUtc.ToUnixTimeNanoseconds();
        JsonOtlpHelper.WriteDecimalString(writer, JsonOtlpTraceFieldNameConstants.StartTimeUnixNano, startTimeUnixNano);
        JsonOtlpHelper.WriteDecimalString(writer, JsonOtlpTraceFieldNameConstants.EndTimeUnixNano, startTimeUnixNano + activity.Duration.ToNanoseconds());

        var (statusCode, statusMessage) = WriteActivityTags(writer, sdkLimitOptions, activity);
        WriteSpanEvents(writer, sdkLimitOptions, activity);
        WriteSpanLinks(writer, sdkLimitOptions, activity);
        WriteSpanStatus(writer, activity, statusCode, statusMessage);

        writer.WriteEndObject();
    }

    private static void WriteTraceFlags(Utf8JsonWriter writer, ActivityTraceFlags activityTraceFlags, bool hasRemoteParent, JsonEncodedText propertyName)
    {
        var spanFlags = (uint)activityTraceFlags & 0x000000FF;

        spanFlags |= 0x00000100;
        if (hasRemoteParent)
        {
            spanFlags |= 0x00000200;
        }

        writer.WriteNumber(propertyName, spanFlags);
    }

    private static (StatusCode? StatusCode, string? StatusMessage) WriteActivityTags(Utf8JsonWriter writer, SdkLimitOptions sdkLimitOptions, Activity activity)
    {
        StatusCode? statusCode = null;
        string? statusMessage = null;
        var maxAttributeCount = sdkLimitOptions.SpanAttributeCountLimit ?? int.MaxValue;
        var maxAttributeValueLength = sdkLimitOptions.SpanAttributeValueLengthLimit ?? int.MaxValue;
        var tagCount = 0;
        var droppedTagCount = 0;
        var writeAttributes = false;

        foreach (ref readonly var tag in activity.EnumerateTagObjects())
        {
            switch (tag.Key)
            {
                case "otel.status_code":

                    statusCode = tag.Value switch
                    {
                        /*
                         * Note: Order here matters for performance. Unset
                         * is first because the assumption is most spans will
                         * be Unset, then Error. Ok is not set by the SDK.
                         */
                        not null when string.Equals(UnsetStatusCodeTagValue, tag.Value as string, StringComparison.OrdinalIgnoreCase) => StatusCode.Unset,
                        not null when string.Equals(ErrorStatusCodeTagValue, tag.Value as string, StringComparison.OrdinalIgnoreCase) => StatusCode.Error,
                        not null when string.Equals(OkStatusCodeTagValue, tag.Value as string, StringComparison.OrdinalIgnoreCase) => StatusCode.Ok,
                        _ => null,
                    };
                    continue;
                case "otel.status_description":
                    statusMessage = tag.Value as string;
                    continue;
                default:
                    break;
            }

            if (tagCount < maxAttributeCount)
            {
                if (!writeAttributes)
                {
                    writer.WritePropertyName(JsonOtlpCommonFieldNameConstants.Attributes);
                    writer.WriteStartArray();
                    writeAttributes = true;
                }

                JsonOtlpTagWriter.OtlpTagWriterState state = new(writer);
                if (JsonOtlpTagWriter.WriteKeyValue(ref state, tag.Key, tag.Value, maxAttributeValueLength))
                {
                    tagCount++;
                }
                else
                {
                    droppedTagCount++;
                }
            }
            else
            {
                droppedTagCount++;
            }
        }

        if (writeAttributes)
        {
            writer.WriteEndArray();
        }

        if (droppedTagCount > 0)
        {
            writer.WriteNumber(JsonOtlpCommonFieldNameConstants.DroppedAttributesCount, droppedTagCount);
        }

        return (statusCode, statusMessage);
    }

    private static void WriteSpanEvents(Utf8JsonWriter writer, SdkLimitOptions sdkLimitOptions, Activity activity)
    {
        var maxEventCountLimit = sdkLimitOptions.SpanEventCountLimit ?? int.MaxValue;
        var eventCount = 0;
        var droppedEventCount = 0;

        foreach (ref readonly var evnt in activity.EnumerateEvents())
        {
            if (eventCount < maxEventCountLimit)
            {
                writer.WritePropertyName(JsonOtlpTraceFieldNameConstants.Events);
                writer.WriteStartObject();

                writer.WriteString(JsonOtlpCommonFieldNameConstants.Name, evnt.Name);
                JsonOtlpHelper.WriteDecimalString(writer, JsonOtlpTraceFieldNameConstants.TimeUnixNano, evnt.Timestamp.ToUnixTimeNanoseconds());

                WriteEventAttributes(writer, sdkLimitOptions, evnt);

                writer.WriteEndObject();
                eventCount++;
            }
            else
            {
                droppedEventCount++;
            }
        }

        if (droppedEventCount > 0)
        {
            writer.WriteNumber(JsonOtlpTraceFieldNameConstants.DroppedEventsCount, droppedEventCount);
        }
    }

    private static void WriteEventAttributes(Utf8JsonWriter writer, SdkLimitOptions sdkLimitOptions, ActivityEvent evnt)
    {
        var maxAttributeCount = sdkLimitOptions.SpanEventAttributeCountLimit ?? int.MaxValue;
        var maxAttributeValueLength = sdkLimitOptions.SpanAttributeValueLengthLimit ?? int.MaxValue;
        var tagCount = 0;
        var droppedTagCount = 0;
        var writeAttributes = false;

        foreach (ref readonly var tag in evnt.EnumerateTagObjects())
        {
            if (tagCount < maxAttributeCount)
            {
                if (!writeAttributes)
                {
                    writer.WritePropertyName(JsonOtlpCommonFieldNameConstants.Attributes);
                    writer.WriteStartArray();
                    writeAttributes = true;
                }

                JsonOtlpTagWriter.OtlpTagWriterState state = new(writer);
                if (JsonOtlpTagWriter.WriteKeyValue(ref state, tag.Key, tag.Value, maxAttributeValueLength))
                {
                    tagCount++;
                }
                else
                {
                    droppedTagCount++;
                }
            }
            else
            {
                droppedTagCount++;
            }
        }

        if (writeAttributes)
        {
            writer.WriteEndArray();
        }

        if (droppedTagCount > 0)
        {
            writer.WriteNumber(JsonOtlpCommonFieldNameConstants.DroppedAttributesCount, droppedTagCount);
        }
    }

    private static void WriteSpanLinks(Utf8JsonWriter writer, SdkLimitOptions sdkLimitOptions, Activity activity)
    {
        var maxLinksCount = sdkLimitOptions.SpanLinkCountLimit ?? int.MaxValue;
        var linkCount = 0;
        var droppedLinkCount = 0;

        foreach (ref readonly var link in activity.EnumerateLinks())
        {
            if (linkCount < maxLinksCount)
            {
                writer.WritePropertyName(JsonOtlpTraceFieldNameConstants.Links);
                writer.WriteStartObject();

                JsonOtlpHelper.WriteTraceId(writer, JsonOtlpTraceFieldNameConstants.TraceId, link.Context.TraceId);
                JsonOtlpHelper.WriteSpanId(writer, JsonOtlpTraceFieldNameConstants.SpanId, link.Context.SpanId);

                if (link.Context.TraceState != null)
                {
                    writer.WriteString(JsonOtlpTraceFieldNameConstants.TraceState, link.Context.TraceState);
                }

                WriteLinkAttributes(writer, sdkLimitOptions, link);

                WriteTraceFlags(writer, link.Context.TraceFlags, link.Context.IsRemote, JsonOtlpTraceFieldNameConstants.Flags);

                writer.WriteEndObject();
                linkCount++;
            }
            else
            {
                droppedLinkCount++;
            }
        }

        if (droppedLinkCount > 0)
        {
            writer.WriteNumber(JsonOtlpTraceFieldNameConstants.DroppedLinksCount, droppedLinkCount);
        }
    }

    private static void WriteLinkAttributes(Utf8JsonWriter writer, SdkLimitOptions sdkLimitOptions, ActivityLink link)
    {
        var maxAttributeCount = sdkLimitOptions.SpanLinkAttributeCountLimit ?? int.MaxValue;
        var maxAttributeValueLength = sdkLimitOptions.SpanAttributeValueLengthLimit ?? int.MaxValue;
        var tagCount = 0;
        var droppedTagCount = 0;
        var writeAttributes = false;

        foreach (ref readonly var tag in link.EnumerateTagObjects())
        {
            if (tagCount < maxAttributeCount)
            {
                if (!writeAttributes)
                {
                    writer.WritePropertyName(JsonOtlpCommonFieldNameConstants.Attributes);
                    writer.WriteStartArray();
                    writeAttributes = true;
                }

                JsonOtlpTagWriter.OtlpTagWriterState state = new(writer);
                if (JsonOtlpTagWriter.WriteKeyValue(ref state, tag.Key, tag.Value, maxAttributeValueLength))
                {
                    tagCount++;
                }
                else
                {
                    droppedTagCount++;
                }
            }
            else
            {
                droppedTagCount++;
            }
        }

        if (writeAttributes)
        {
            writer.WriteEndArray();
        }

        if (droppedTagCount > 0)
        {
            writer.WriteNumber(JsonOtlpCommonFieldNameConstants.DroppedAttributesCount, droppedTagCount);
        }
    }

    private static void WriteSpanStatus(Utf8JsonWriter writer, Activity activity, StatusCode? statusCode, string? statusMessage)
    {
        if (activity.Status == ActivityStatusCode.Unset && statusCode == null)
        {
            return;
        }

        var useActivity = activity.Status != ActivityStatusCode.Unset;
        var isError = useActivity ? activity.Status == ActivityStatusCode.Error : statusCode == StatusCode.Error;
        var description = useActivity ? activity.StatusDescription : statusMessage;

        writer.WritePropertyName(JsonOtlpTraceFieldNameConstants.Status);
        writer.WriteStartObject();

        if (isError && description != null)
        {
            writer.WriteString(JsonOtlpTraceFieldNameConstants.Message, description);
        }

        var finalStatusCode = useActivity ? (int)activity.Status : (statusCode is not null and not StatusCode.Unset) ? (int)statusCode : (int)StatusCode.Unset;
        writer.WriteNumber(JsonOtlpTraceFieldNameConstants.Code, finalStatusCode);

        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes one instrumentation scope attribute, opening the <c>"attributes"</c> array
    /// on first use.
    /// </summary>
    private static void TryWriteScopeAttribute(
        Utf8JsonWriter writer,
        ref bool writeAttributes,
        ref int tagCount,
        ref int droppedTagCount,
        int maxAttributeCount,
        KeyValuePair<string, object?> tag,
        int maxAttributeValueLength)
    {
        if (tagCount >= maxAttributeCount)
        {
            droppedTagCount++;
            return;
        }

        if (!writeAttributes)
        {
            writer.WritePropertyName(JsonOtlpCommonFieldNameConstants.Attributes);
            writer.WriteStartArray();
            writeAttributes = true;
        }

        JsonOtlpTagWriter.OtlpTagWriterState state = new(writer);
        if (JsonOtlpTagWriter.WriteKeyValue(ref state, tag.Key, tag.Value, maxAttributeValueLength))
        {
            tagCount++;
        }
        else
        {
            droppedTagCount++;
        }
    }
}

#endif