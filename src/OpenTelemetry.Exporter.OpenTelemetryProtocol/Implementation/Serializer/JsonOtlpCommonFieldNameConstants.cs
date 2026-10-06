// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

#if NET8_0_OR_GREATER

using System.Text.Json;

namespace OpenTelemetry.Exporter.OpenTelemetryProtocol.Implementation.Serializer;

/// <summary>
/// Common field name constants for OTLP JSON encoding.
/// </summary>
internal static class JsonOtlpCommonFieldNameConstants
{
    internal static readonly JsonEncodedText Key = JsonEncodedText.Encode("key");
    internal static readonly JsonEncodedText Value = JsonEncodedText.Encode("value");
    internal static readonly JsonEncodedText StringValue = JsonEncodedText.Encode("stringValue");
    internal static readonly JsonEncodedText BoolValue = JsonEncodedText.Encode("boolValue");
    internal static readonly JsonEncodedText IntValue = JsonEncodedText.Encode("intValue");
    internal static readonly JsonEncodedText DoubleValue = JsonEncodedText.Encode("doubleValue");
    internal static readonly JsonEncodedText BytesValue = JsonEncodedText.Encode("bytesValue");
    internal static readonly JsonEncodedText ArrayValue = JsonEncodedText.Encode("arrayValue");
    internal static readonly JsonEncodedText KvlistValue = JsonEncodedText.Encode("kvlistValue");
    internal static readonly JsonEncodedText Values = JsonEncodedText.Encode("values");
    internal static readonly JsonEncodedText Attributes = JsonEncodedText.Encode("attributes");
    internal static readonly JsonEncodedText DroppedAttributesCount = JsonEncodedText.Encode("droppedAttributesCount");
    internal static readonly JsonEncodedText Name = JsonEncodedText.Encode("name");
    internal static readonly JsonEncodedText Version = JsonEncodedText.Encode("version");
}

#endif
