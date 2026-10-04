// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

#if NET8_0_OR_GREATER

using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace OpenTelemetry.Exporter.OpenTelemetryProtocol.Implementation.Serializer;

/// <summary>
/// Exposes the unused tail of a caller-owned buffer as an <see cref="IBufferWriter{T}"/>,
/// and never grows it. Growth is owned by the serializer's retry loop so that this
/// path grows the buffer exactly as <see cref="ProtobufSerializer"/> does, which is
/// what makes the two serializers comparable in a benchmark.
/// </summary>
internal sealed class JsonBufferWriter : IBufferWriter<byte>
{
    private readonly byte[] buffer;
    private int position;

    internal JsonBufferWriter(byte[] buffer, int position)
    {
        this.buffer = buffer;
        this.position = position;
    }

    /// <summary>
    /// Gets the number of bytes written since this instance was created, which is the
    /// final write position of a successful serialization attempt.
    /// </summary>
    internal int BytesWritten => this.position;

    /// <summary>
    /// Returns the tail of the buffer.
    /// </summary>
    /// <param name="sizeHint">The number of bytes the caller intends to write.</param>
    /// <returns>The writable tail of the buffer.</returns>
    /// <remarks>
    /// Throwing on a short tail here, rather than returning one, is deliberate.
    /// <see cref="System.Text.Json.Utf8JsonWriter"/> asks for 256 bytes on its first
    /// call and 4096 on every refill, and responds to a smaller tail by throwing
    /// <see cref="InvalidOperationException"/>. That exception is unsafe here:
    /// <c>TagWriter.TryWriteKvListTagWithinDepthLimit</c> catches
    /// <see cref="IndexOutOfRangeException"/> and <see cref="ArgumentException"/> to
    /// reset its recursion depth, and treats any other exception as an unsupported
    /// attribute type, silently dropping the attribute while the export still reports
    /// success. <see cref="ArgumentException"/> is the out-of-space signal
    /// <see cref="ProtobufSerializer"/> already throws from its slow path, and every
    /// handler in the pipeline already treats it as "grow and retry".
    /// <para/>
    /// Note that honoring <paramref name="sizeHint"/> exactly makes this slightly more
    /// conservative than protobuf near the end of the buffer, since a refill asks for
    /// 4096 bytes even when far fewer are needed. That only costs an extra growth.
    /// </remarks>
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);

        if (this.buffer.Length - this.position < sizeHint)
        {
            ThrowBufferTooSmallException();
        }

        return this.buffer.AsMemory(this.position);
    }

    /// <summary>
    /// Returns the tail of the buffer.
    /// </summary>
    /// <param name="sizeHint">The number of bytes the caller intends to write.</param>
    /// <returns>The writable tail of the buffer.</returns>
    public Span<byte> GetSpan(int sizeHint = 0) => this.GetMemory(sizeHint).Span;

    /// <summary>
    /// Advances the write position.
    /// </summary>
    /// <param name="count">The number of bytes written.</param>
    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        if (this.buffer.Length - this.position < count)
        {
            ThrowBufferTooSmallException();
        }

        this.position += count;
    }

    [DoesNotReturn]
    private static void ThrowBufferTooSmallException()
        => throw new ArgumentException("The buffer is too small to hold the data being written.");
}

#endif
