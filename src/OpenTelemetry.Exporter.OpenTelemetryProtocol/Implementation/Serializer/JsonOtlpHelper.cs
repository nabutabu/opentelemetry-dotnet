// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

#if NET8_0_OR_GREATER

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace OpenTelemetry.Exporter.OpenTelemetryProtocol.Implementation.Serializer;

/// <summary>
/// Helpers shared by the OTLP JSON serializers.
/// </summary>
internal static class JsonOtlpHelper
{
    /// <summary>
    /// The <see cref="JsonWriterOptions"/> used by every <see cref="Utf8JsonWriter"/> in
    /// this serializer, including the nested ones that back array- and kvlist-valued
    /// attributes. They have to agree, or spliced bytes are escaped differently from
    /// main-writer bytes and the payload is only accidentally self-consistent.
    /// </summary>
    internal static readonly JsonWriterOptions WriterOptions = new()
    {
        // SkipValidation is safe with Indented = false: neither WriteEndSlow's
        // ValidateEnd nor UpdateBitStackOnStart touches the bit stack, so the two
        // settings are self-consistent. Depth is still checked against MaxDepth, and
        // the deepest document OTLP can produce here is far below the default of 1000.
        SkipValidation = true,
        Indented = false,
    };

    internal static readonly char[] HexChars = "0123456789abcdef".ToCharArray();

    private const int TraceIdSize = 16;
    private const int SpanIdSize = 8;

    /// <summary>
    /// Writes an identifier as a lowercase hex string, which is what OTLP's JSON mapping
    /// requires for <c>trace_id</c>, <c>span_id</c> and <c>parent_span_id</c>. Protobuf
    /// gets the bytes as-is; JSON has to spell them out.
    /// </summary>
    /// <param name="writer">The writer to write to.</param>
    /// <param name="propertyName">The property name.</param>
    /// <param name="id">The raw identifier bytes.</param>
    internal static void WriteHexId(Utf8JsonWriter writer, JsonEncodedText propertyName, ReadOnlySpan<byte> id)
    {
        Span<char> hex = stackalloc char[id.Length * 2];

        for (var i = 0; i < id.Length; i++)
        {
            hex[i * 2] = HexChars[id[i] >> 4];
            hex[(i * 2) + 1] = HexChars[id[i] & 0x0F];
        }

        writer.WriteString(propertyName, hex);
    }

    /// <summary>
    /// Writes a trace id as a lowercase hex string.
    /// </summary>
    /// <param name="writer">The writer to write to.</param>
    /// <param name="propertyName">The property name.</param>
    /// <param name="traceId">The trace id.</param>
    internal static void WriteTraceId(Utf8JsonWriter writer, JsonEncodedText propertyName, ActivityTraceId traceId)
    {
        Span<byte> traceIdBytes = stackalloc byte[TraceIdSize];
        traceId.CopyTo(traceIdBytes);
        WriteHexId(writer, propertyName, traceIdBytes);
    }

    /// <summary>
    /// Writes a span id as a lowercase hex string.
    /// </summary>
    /// <param name="writer">The writer to write to.</param>
    /// <param name="propertyName">The property name.</param>
    /// <param name="spanId">The span id.</param>
    internal static void WriteSpanId(Utf8JsonWriter writer, JsonEncodedText propertyName, ActivitySpanId spanId)
    {
        Span<byte> spanIdBytes = stackalloc byte[SpanIdSize];
        spanId.CopyTo(spanIdBytes);
        WriteHexId(writer, propertyName, spanIdBytes);
    }

    /// <summary>
    /// Writes a 64-bit integer as a decimal string, which is OTLP's JSON mapping for
    /// <c>intValue</c>, <c>startTimeUnixNano</c>, <c>endTimeUnixNano</c> and
    /// <c>timeUnixNano</c>.
    /// </summary>
    /// <param name="writer">The writer to write to.</param>
    /// <param name="propertyName">The property name.</param>
    /// <param name="value">The value.</param>
    internal static void WriteDecimalString(Utf8JsonWriter writer, JsonEncodedText propertyName, long value)
    {
        Span<char> digits = stackalloc char[20];
        value.TryFormat(digits, out var written, default, CultureInfo.InvariantCulture);
        writer.WriteString(propertyName, digits[..written]);
    }

    /// <summary>
    /// Writes a 64-bit integer as a decimal string value, for callers that have already
    /// written the property name.
    /// </summary>
    /// <param name="writer">The writer to write to.</param>
    /// <param name="value">The value.</param>
    internal static void WriteDecimalValue(Utf8JsonWriter writer, long value)
    {
        Span<char> digits = stackalloc char[20];

        // Note: the value is formatted signed rather than through (ulong). A
        // DateTimeOffset.ToUnixTimeNanoseconds() is negative before 1970, and casting
        // that to ulong renders long.MinValue as 18446744073709551616. Protobuf escapes
        // this by writing the raw two's-complement fixed64, which round-trips to the
        // same signed value; JSON has no analogous escape hatch. 20 chars still covers
        // both long.MinValue (-9223372036854775808) and ulong.MaxValue.
        value.TryFormat(digits, out var written, default, CultureInfo.InvariantCulture);
        writer.WriteStringValue(digits[..written]);
    }
}

#endif