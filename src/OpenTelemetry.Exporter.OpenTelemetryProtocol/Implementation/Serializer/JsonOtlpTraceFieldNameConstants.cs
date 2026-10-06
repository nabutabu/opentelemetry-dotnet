// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

#if NET8_0_OR_GREATER

using System.Text.Json;

namespace OpenTelemetry.Exporter.OpenTelemetryProtocol.Implementation.Serializer;

/// <summary>
/// Trace field name constants for OTLP JSON encoding.
/// </summary>
internal static class JsonOtlpTraceFieldNameConstants
{
    internal static readonly JsonEncodedText ResourceSpans = JsonEncodedText.Encode("resourceSpans");
    internal static readonly JsonEncodedText Resource = JsonEncodedText.Encode("resource");
    internal static readonly JsonEncodedText ScopeSpans = JsonEncodedText.Encode("scopeSpans");
    internal static readonly JsonEncodedText Scope = JsonEncodedText.Encode("scope");
    internal static readonly JsonEncodedText SchemaUrl = JsonEncodedText.Encode("schemaUrl");
    internal static readonly JsonEncodedText TraceId = JsonEncodedText.Encode("traceId");
    internal static readonly JsonEncodedText SpanId = JsonEncodedText.Encode("spanId");
    internal static readonly JsonEncodedText TraceState = JsonEncodedText.Encode("traceState");
    internal static readonly JsonEncodedText ParentSpanId = JsonEncodedText.Encode("parentSpanId");
    internal static readonly JsonEncodedText Kind = JsonEncodedText.Encode("kind");
    internal static readonly JsonEncodedText StartTimeUnixNano = JsonEncodedText.Encode("startTimeUnixNano");
    internal static readonly JsonEncodedText EndTimeUnixNano = JsonEncodedText.Encode("endTimeUnixNano");
    internal static readonly JsonEncodedText Events = JsonEncodedText.Encode("events");
    internal static readonly JsonEncodedText DroppedEventsCount = JsonEncodedText.Encode("droppedEventsCount");
    internal static readonly JsonEncodedText Links = JsonEncodedText.Encode("links");
    internal static readonly JsonEncodedText DroppedLinksCount = JsonEncodedText.Encode("droppedLinksCount");
    internal static readonly JsonEncodedText Status = JsonEncodedText.Encode("status");
    internal static readonly JsonEncodedText Flags = JsonEncodedText.Encode("flags");
    internal static readonly JsonEncodedText TimeUnixNano = JsonEncodedText.Encode("timeUnixNano");
    internal static readonly JsonEncodedText Message = JsonEncodedText.Encode("message");
    internal static readonly JsonEncodedText Code = JsonEncodedText.Encode("code");
    internal static readonly JsonEncodedText Spans = JsonEncodedText.Encode("spans");
}

#endif
