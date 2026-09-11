/*
 *  Copyright 2016-2019 Netflix, Inc.
 *
 *     Licensed under the Apache License, Version 2.0 (the "License");
 *     you may not use this file except in compliance with the License.
 *     You may obtain a copy of the License at
 *
 *         http://www.apache.org/licenses/LICENSE-2.0
 *
 *     Unless required by applicable law or agreed to in writing, software
 *     distributed under the License is distributed on an "AS IS" BASIS,
 *     WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *     See the License for the specific language governing permissions and
 *     limitations under the License.
 *
 */

using System.Buffers;
using System.Buffers.Binary;
using Hollow.Core.Io;

namespace Hollow.Core.Write;

/// <summary>
/// Writes the primitives that make up a Hollow blob to an underlying <see cref="Stream"/>.
/// </summary>
/// <remarks>
/// The write counterpart of <see cref="Read.HollowBlobInput"/>, and the port's stand-in for Java's
/// <c>java.io.DataOutputStream</c>: all multi-byte integers are big-endian and strings are modified
/// UTF-8, so the bytes produced here are byte-identical to the Java implementation's.
/// </remarks>
public sealed class HollowBlobOutput : IDisposable
{
    private readonly bool _leaveOpen;
    private Stream? _stream;

    private HollowBlobOutput(Stream stream, bool leaveOpen)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
    }

    private Stream Stream => _stream ?? throw new ObjectDisposedException(nameof(HollowBlobOutput));

    /// <summary>
    /// Wraps <paramref name="stream"/> for blob output.
    /// </summary>
    /// <param name="stream">The stream to write the blob to.</param>
    /// <param name="leaveOpen">
    /// When <see langword="true"/>, disposing this output leaves <paramref name="stream"/> open.
    /// </param>
    public static HollowBlobOutput Serial(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new HollowBlobOutput(stream, leaveOpen);
    }

    /// <summary>Writes a single byte.</summary>
    public void WriteByte(byte value) => Stream.WriteByte(value);

    /// <summary>Writes the low 8 bits of <paramref name="value"/>.</summary>
    /// <remarks>
    /// Mirrors <c>OutputStream.write(int)</c>, which Hollow calls with values that have already been
    /// masked down to a byte.
    /// </remarks>
    public void WriteByte(int value) => Stream.WriteByte((byte)value);

    /// <summary>Writes a span of bytes.</summary>
    public void Write(ReadOnlySpan<byte> buffer) => Stream.Write(buffer);

    /// <summary>Writes a big-endian signed 16-bit integer.</summary>
    public void WriteInt16(short value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(short)];
        BinaryPrimitives.WriteInt16BigEndian(buffer, value);
        Stream.Write(buffer);
    }

    /// <summary>Writes a big-endian signed 32-bit integer.</summary>
    public void WriteInt32(int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        Stream.Write(buffer);
    }

    /// <summary>Writes a big-endian signed 64-bit integer.</summary>
    public void WriteInt64(long value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(buffer, value);
        Stream.Write(buffer);
    }

    /// <summary>
    /// Writes a length-prefixed modified UTF-8 string, matching <c>java.io.DataOutput.writeUTF</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The encoded form exceeds 65535 bytes.</exception>
    public void WriteUtf(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        int byteCount = ModifiedUtf8.GetByteCount(value);
        if (byteCount > ushort.MaxValue)
        {
            throw new ArgumentException(
                $"encoded string too long: {byteCount} bytes", nameof(value));
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount + sizeof(ushort));
        try
        {
            BinaryPrimitives.WriteUInt16BigEndian(rented, (ushort)byteCount);
            ModifiedUtf8.GetBytes(value, rented.AsSpan(sizeof(ushort)));
            Stream.Write(rented, 0, byteCount + sizeof(ushort));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Flushes the underlying stream.</summary>
    public void Flush() => Stream.Flush();

    /// <inheritdoc />
    public void Dispose()
    {
        Stream? stream = _stream;
        _stream = null;

        if (stream is not null && !_leaveOpen)
        {
            stream.Dispose();
        }
    }
}
