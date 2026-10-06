// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

#if NET8_0_OR_GREATER

using System.Buffers;
using System.Globalization;
using System.Text.Json;
using OpenTelemetry.Internal;

namespace OpenTelemetry.Exporter.OpenTelemetryProtocol.Implementation.Serializer;

/// <summary>
/// Writes a <c>KeyValue</c> under OTLP's JSON encoding of
/// <c>opentelemetry.proto.common.v1.AnyValue</c>.
/// </summary>
/// <remarks>
/// The main writer cannot be rewound, so array- and kvlist-valued attributes are built
/// in a nested <see cref="Utf8JsonWriter"/> over a rented scratch buffer and spliced in
/// with <see cref="Utf8JsonWriter.WriteRawValue(ReadOnlySpan{byte}, bool)"/> once they are
/// known good. Protobuf gets the same guarantee for free by restoring
/// <c>WritePosition</c>; see <see cref="JsonOtlpTagWriter.WriteArrayTag"/>.
/// <para/>
/// Note that the OTLP/JSON <c>AnyValue</c> oneof is collapsed, so only the field that is
/// actually set appears inside <c>"value"</c>, unwrapped.
/// </remarks>
internal sealed class JsonOtlpTagWriter : TagWriter<JsonOtlpTagWriter.OtlpTagWriterState, JsonOtlpTagWriter.OtlpTagWriterArrayState>
{
    private readonly OtlpArrayTagWriter arrayTagWriter;

    internal JsonOtlpTagWriter(OtlpArrayTagWriter arrayTagWriter)
        : base(arrayTagWriter)
    {
        this.arrayTagWriter = arrayTagWriter;
    }

    private JsonOtlpTagWriter()
        : this(new OtlpArrayTagWriter())
    {
    }

    /// <summary>
    /// Gets the shared writer. <see cref="TagWriter{TTagState, TArrayState}"/> keeps its
    /// recursion depth in a <c>[ThreadStatic]</c> field, so all writes for a batch must go
    /// through the same instance.
    /// </summary>
    public static JsonOtlpTagWriter Instance { get; } = new();

    /// <summary>
    /// Writes a tag as a <c>KeyValue</c> object.
    /// </summary>
    /// <param name="state">The writer state.</param>
    /// <param name="key">The attribute key.</param>
    /// <param name="value">The attribute value.</param>
    /// <param name="tagValueMaxLength">The maximum length to write a string value as, if any.</param>
    /// <returns><see langword="true"/> if the key-value pair was successfully written; otherwise <see langword="false"/>.</returns>
    public static bool WriteKeyValue(
        ref OtlpTagWriterState state,
        string key,
        object? value,
        int? tagValueMaxLength = null)
        => Instance.TryWriteTag(ref state, key, value, tagValueMaxLength);

    /// <inheritdoc/>
    protected override void WriteIntegralTag(ref OtlpTagWriterState state, string key, long value)
    {
        var writer = state.Writer;
        WriteAnyValueStart(writer, key);
        writer.WritePropertyName(JsonOtlpCommonFieldNameConstants.IntValue);

        // OTLP encodes 64-bit integers as JSON strings, because a JSON number cannot
        // hold every long without losing precision.
        JsonOtlpHelper.WriteDecimalValue(writer, value);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    protected override void WriteFloatingPointTag(ref OtlpTagWriterState state, string key, double value)
    {
        var writer = state.Writer;
        WriteAnyValueStart(writer, key);
        writer.WritePropertyName(JsonOtlpCommonFieldNameConstants.DoubleValue);

        // Utf8JsonWriter formats doubles through Utf8Formatter, which cannot represent
        // NaN or Infinity and fails with ArgumentException. That is the same exception
        // type JsonBufferWriter uses to signal "buffer too small", so a single
        // double.NaN attribute would otherwise grow the buffer all the way to
        // maxBufferSize and then fail with a buffer-size message for a payload of a few
        // hundred bytes. JsonNumberHandling.AllowNamedFloatingPointLiterals is not an
        // escape either: it emits a bare NaN token, which RFC 8259 does not allow, so a
        // receiver rejects the whole payload rather than just this attribute. Writing it
        // as a string is lossless and keeps the document valid.
        if (double.IsFinite(value))
        {
            writer.WriteNumberValue(value);
        }
        else
        {
            writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    protected override void WriteBooleanTag(ref OtlpTagWriterState state, string key, bool value)
    {
        var writer = state.Writer;
        WriteAnyValueStart(writer, key);
        writer.WriteBoolean(JsonOtlpCommonFieldNameConstants.BoolValue, value);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    protected override void WriteStringTag(ref OtlpTagWriterState state, string key, ReadOnlySpan<char> value)
    {
        var writer = state.Writer;
        WriteAnyValueStart(writer, key);
        writer.WriteString(JsonOtlpCommonFieldNameConstants.StringValue, value);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    protected override void WriteStringTag(ref OtlpTagWriterState state, string key, string value)
    {
        var writer = state.Writer;
        WriteAnyValueStart(writer, key);
        writer.WriteString(JsonOtlpCommonFieldNameConstants.StringValue, value);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    protected override void WriteArrayTag(ref OtlpTagWriterState state, string key, ref OtlpTagWriterArrayState value)
    {
        var writer = state.Writer;
        WriteAnyValueStart(writer, key);
        writer.WritePropertyName(JsonOtlpCommonFieldNameConstants.ArrayValue);
        writer.WriteStartObject();
        writer.WritePropertyName(JsonOtlpCommonFieldNameConstants.Values);

        // The scratch buffer holds a complete, valid JSON array by this point, so the
        // main writer is only touched now. TagWriter.WriteArrayTagInternal can still
        // fail while producing that array - an element whose ToString throws, for
        // instance - and it reports that by returning false, by which time the main
        // writer would already be mid-arrayValue with no way back. Splicing is what
        // restores the invariant protobuf gets from restoring its WritePosition.
        writer.WriteRawValue(value.BytesWritten, skipInputValidation: true);

        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();

        this.arrayTagWriter.ReleaseBuffer(ref value);
    }

    /// <inheritdoc/>
    protected override bool TryWriteEmptyTag(ref OtlpTagWriterState state, string key, object? value)
    {
        var writer = state.Writer;
        WriteAnyValueStart(writer, key);
        writer.WriteEndObject();
        writer.WriteEndObject();
        return true;
    }

    /// <inheritdoc/>
    protected override bool TryWriteByteArrayTag(ref OtlpTagWriterState state, string key, ReadOnlySpan<byte> value)
    {
        var writer = state.Writer;
        WriteAnyValueStart(writer, key);
        writer.WriteBase64String(JsonOtlpCommonFieldNameConstants.BytesValue, value);
        writer.WriteEndObject();
        writer.WriteEndObject();
        return true;
    }

    /// <inheritdoc/>
    protected override void WriteKvListTag(ref OtlpTagWriterState state, string key, IEnumerable<KeyValuePair<string, object?>> kvList, int? tagValueMaxLength)
    {
        // Same atomicity reasoning as WriteArrayTag: the whole list is serialized into a
        // scratch buffer first, because TryWriteKvListTagWithinDepthLimit turns an
        // exception thrown while enumerating into a plain `return false`.
        var arrayState = this.arrayTagWriter.BeginWriteArray();

        try
        {
            foreach (var kvp in kvList)
            {
                OtlpTagWriterState entryState = new(arrayState.Writer);
                if (!this.TryWriteTag(ref entryState, kvp.Key, kvp.Value, tagValueMaxLength))
                {
                    continue;
                }
            }

            this.arrayTagWriter.EndWriteArray(ref arrayState);
        }
        catch
        {
            this.arrayTagWriter.AbortWriteArray(ref arrayState);
            throw;
        }

        var writer = state.Writer;
        WriteAnyValueStart(writer, key);
        writer.WritePropertyName(JsonOtlpCommonFieldNameConstants.KvlistValue);
        writer.WriteStartObject();
        writer.WritePropertyName(JsonOtlpCommonFieldNameConstants.Values);
        writer.WriteRawValue(arrayState.BytesWritten, skipInputValidation: true);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();

        this.arrayTagWriter.ReleaseBuffer(ref arrayState);
    }

    /// <inheritdoc/>
    protected override void OnUnsupportedTagDropped(
        string tagKey,
        string tagValueTypeFullName) => OpenTelemetryProtocolExporterEventSource.Log.UnsupportedAttributeType(
            tagValueTypeFullName,
            tagKey);

    /// <summary>
    /// Opens <c>{"key":k,"value":{</c>, the shape shared by every <c>KeyValue</c>.
    /// </summary>
    /// <param name="writer">The writer to open the key-value pair on.</param>
    /// <param name="key">The attribute key.</param>
    private static void WriteAnyValueStart(Utf8JsonWriter writer, string key)
    {
        writer.WriteStartObject();
        writer.WriteString(JsonOtlpCommonFieldNameConstants.Key, key);
        writer.WritePropertyName(JsonOtlpCommonFieldNameConstants.Value);
        writer.WriteStartObject();
    }

    internal readonly struct OtlpTagWriterState
    {
        internal readonly Utf8JsonWriter Writer;

        internal OtlpTagWriterState(Utf8JsonWriter writer)
        {
            this.Writer = writer;
        }
    }

    internal struct OtlpTagWriterArrayState
    {
        internal byte[]? Buffer;
        internal Utf8JsonWriter Writer;
        internal JsonBufferWriter BufferWriter;

        /// <summary>
        /// Gets the completed JSON array held in <see cref="Buffer"/>.
        /// </summary>
        internal ReadOnlySpan<byte> BytesWritten
            => this.Buffer is { } buffer && this.BufferWriter.BytesWritten > 0
                ? buffer.AsSpan(0, this.BufferWriter.BytesWritten)
                : ReadOnlySpan<byte>.Empty;
    }

    internal sealed class OtlpArrayTagWriter : ArrayTagWriter<OtlpTagWriterArrayState>
    {
        private const int DefaultBufferSize = 2048;
        private const int MaxBufferSize = 2 * 1024 * 1024;

        private readonly ArrayPool<byte> pool;

        public OtlpArrayTagWriter()
            : this(ArrayPool<byte>.Shared)
        {
        }

        internal OtlpArrayTagWriter(ArrayPool<byte> pool)
        {
            Guard.ThrowIfNull(pool);
            this.pool = pool;
        }

        /// <summary>
        /// Rents a scratch buffer and opens a JSON array on it.
        /// </summary>
        /// <returns>The state holding the scratch buffer and its writer.</returns>
        /// <remarks>
        /// A fresh <see cref="Utf8JsonWriter"/> per array, rather than a
        /// <c>[ThreadStatic]</c> one, is what lets a nested write - an element whose
        /// <c>ToString()</c> serializes an array of its own - keep its own buffer. The
        /// cost is one allocation per array-valued attribute.
        /// </remarks>
        public override OtlpTagWriterArrayState BeginWriteArray()
        {
            var buffer = this.pool.Rent(DefaultBufferSize);

            // The nested writer must share JsonOtlpHelper.WriterOptions with the main
            // writer. Otherwise the spliced bytes are escaped differently from
            // main-writer bytes and the payload is only accidentally self-consistent.
            var bufferWriter = new JsonBufferWriter(buffer, 0);
            var writer = new Utf8JsonWriter(bufferWriter, JsonOtlpHelper.WriterOptions);
            writer.WriteStartArray();

            return new OtlpTagWriterArrayState
            {
                Buffer = buffer,
                Writer = writer,
                BufferWriter = bufferWriter,
            };
        }

        /// <inheritdoc/>
        public override void WriteNullValue(ref OtlpTagWriterArrayState state)
        {
            // A null inside an array is an unset AnyValue, which is an empty object.
            state.Writer.WriteStartObject();
            state.Writer.WriteEndObject();
        }

        /// <inheritdoc/>
        public override void WriteIntegralValue(ref OtlpTagWriterArrayState state, long value)
        {
            state.Writer.WriteStartObject();
            state.Writer.WritePropertyName(JsonOtlpCommonFieldNameConstants.IntValue);
            JsonOtlpHelper.WriteDecimalValue(state.Writer, value);
            state.Writer.WriteEndObject();
        }

        /// <inheritdoc/>
        public override void WriteFloatingPointValue(ref OtlpTagWriterArrayState state, double value)
        {
            state.Writer.WriteStartObject();
            state.Writer.WritePropertyName(JsonOtlpCommonFieldNameConstants.DoubleValue);

            // See WriteFloatingPointTag for why a non-finite value is written as a string.
            if (double.IsFinite(value))
            {
                state.Writer.WriteNumberValue(value);
            }
            else
            {
                state.Writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
            }

            state.Writer.WriteEndObject();
        }

        /// <inheritdoc/>
        public override void WriteBooleanValue(ref OtlpTagWriterArrayState state, bool value)
        {
            state.Writer.WriteStartObject();
            state.Writer.WriteBoolean(JsonOtlpCommonFieldNameConstants.BoolValue, value);
            state.Writer.WriteEndObject();
        }

        /// <inheritdoc/>
        public override void WriteStringValue(ref OtlpTagWriterArrayState state, ReadOnlySpan<char> value)
        {
            state.Writer.WriteStartObject();
            state.Writer.WriteString(JsonOtlpCommonFieldNameConstants.StringValue, value);
            state.Writer.WriteEndObject();
        }

        /// <inheritdoc/>
        public override void EndWriteArray(ref OtlpTagWriterArrayState state)
        {
            state.Writer.WriteEndArray();

            // The scratch bytes are spliced into the main writer by value, so the nested
            // writer has to hand them over before the scratch buffer is released.
            state.Writer.Flush();
        }

        /// <inheritdoc/>
        public override void AbortWriteArray(ref OtlpTagWriterArrayState state)
            => this.ReleaseBuffer(ref state);

        /// <inheritdoc/>
        public override bool TryResize(ref OtlpTagWriterArrayState state)
        {
            var smallerBuffer = state.Buffer!;

            if (smallerBuffer.Length >= MaxBufferSize)
            {
                OpenTelemetryProtocolExporterEventSource.Log.ArrayBufferExceededMaxSize();
                return false;
            }

            byte[] largerBuffer;
            try
            {
                largerBuffer = this.pool.Rent(smallerBuffer.Length * 2);
            }
            catch (OutOfMemoryException)
            {
                OpenTelemetryProtocolExporterEventSource.Log.BufferResizeFailedDueToMemory(nameof(OtlpArrayTagWriter));
                return false;
            }

            // Carry the bytes already written over to the larger buffer, because
            // TagWriter.WriteArrayTagInternal retries the whole array against it.
            Buffer.BlockCopy(smallerBuffer, 0, largerBuffer, 0, state.BufferWriter.BytesWritten);

            // Reset requires a complete document, and the array is still open here, so
            // the writer has to be replaced rather than re-pointed. Nothing reads it
            // again, and disposing it would null the output.
            var newBufferWriter = new JsonBufferWriter(largerBuffer, state.BufferWriter.BytesWritten);
            var newWriter = new Utf8JsonWriter(newBufferWriter, JsonOtlpHelper.WriterOptions);
            newWriter.WriteStartArray();

            state.Buffer = largerBuffer;
            state.BufferWriter = newBufferWriter;
            state.Writer = newWriter;

            ProtobufSerializer.ReturnBuffer(this.pool, smallerBuffer);

            return true;
        }

        /// <summary>
        /// Returns the scratch buffer to the pool.
        /// </summary>
        /// <param name="state">The state to clear.</param>
        internal void ReleaseBuffer(ref OtlpTagWriterArrayState state)
        {
            var buffer = state.Buffer;
            state = default;

            if (buffer != null)
            {
                ProtobufSerializer.ReturnBuffer(this.pool, buffer);
            }
        }
    }
}

#endif