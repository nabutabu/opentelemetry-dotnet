// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

#if NET8_0_OR_GREATER

using System.Text.Json;
using OpenTelemetry.Resources;

namespace OpenTelemetry.Exporter.OpenTelemetryProtocol.Implementation.Serializer;

/// <summary>
/// Serializes a <see cref="Resource"/> under OTLP's JSON mapping.
/// </summary>
internal static class JsonOtlpResourceSerializer
{
    /// <summary>
    /// Writes <c>"resource":{...}</c>.
    /// </summary>
    /// <param name="writer">The writer to write to.</param>
    /// <param name="resource">The resource, if any.</param>
    /// <remarks>
    /// A null or <see cref="Resource.Empty"/> resource produces <c>{"resource":{}}</c>,
    /// which is what the protobuf serializer emits as an empty message, so that the two
    /// payloads stay comparable. There is deliberately no dropped-attribute accounting
    /// here, matching <see cref="ProtobufOtlpResourceSerializer"/>.
    /// <para/>
    /// The <c>ConditionalWeakTable</c> cache the protobuf serializer uses to serialize
    /// each distinct resource exactly once is deferred: it would have to cache spliced
    /// JSON rather than bytes, and a resource is written once per batch next to the
    /// spans, so there is nothing to amortize yet.
    /// </remarks>
    internal static void WriteResource(Utf8JsonWriter writer, Resource? resource)
    {
        writer.WriteStartObject();
        writer.WritePropertyName(JsonOtlpTraceFieldNameConstants.Resource);
        writer.WriteStartObject();

        if (resource != null && resource != Resource.Empty)
        {
            var attributes = resource.Attributes;

            if (attributes is IReadOnlyList<KeyValuePair<string, object>> resourceAttributesList)
            {
                if (resourceAttributesList.Count > 0)
                {
                    writer.WritePropertyName(JsonOtlpCommonFieldNameConstants.Attributes);
                    writer.WriteStartArray();

                    for (var i = 0; i < resourceAttributesList.Count; i++)
                    {
                        WriteResourceAttribute(writer, resourceAttributesList[i]);
                    }

                    writer.WriteEndArray();
                }
            }
            else
            {
                writer.WritePropertyName(JsonOtlpCommonFieldNameConstants.Attributes);
                writer.WriteStartArray();

                foreach (var attribute in attributes)
                {
                    WriteResourceAttribute(writer, attribute);
                }

                writer.WriteEndArray();
            }
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteResourceAttribute(Utf8JsonWriter writer, KeyValuePair<string, object> attribute)
    {
        JsonOtlpTagWriter.OtlpTagWriterState state = new(writer);
        _ = JsonOtlpTagWriter.WriteKeyValue(ref state, attribute.Key, attribute.Value);
    }
}

#endif