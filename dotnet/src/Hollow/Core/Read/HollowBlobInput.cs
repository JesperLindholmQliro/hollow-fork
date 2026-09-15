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
using Hollow.Core.Memory;
using Hollow.Core.Memory.Encoding;

namespace Hollow.Core.Read;

/// <summary>
/// Reads the primitives that make up a Hollow blob from an underlying <see cref="Stream"/>.
/// </summary>
/// <remarks>
/// <para>
/// The Java original abstracts over <c>DataInputStream</c> and <c>RandomAccessFile</c> to serve its two
/// memory modes. .NET has a single <see cref="Stream"/> abstraction that covers both, so this port wraps
/// a stream directly and reports seekability via <see cref="CanSeek"/>.
/// </para>
/// <para>
/// All multi-byte integers are big-endian and strings are modified UTF-8, matching
/// <c>java.io.DataInput</c>, so blobs written by the Java implementation read here unchanged.
/// </para>
/// </remarks>
public sealed class HollowBlobInput : IDisposable
{
    private readonly bool _leaveOpen;
    private readonly MemoryMappedBlob? _blob;
    private Stream? _stream;

    private HollowBlobInput(MemoryMode memoryMode, Stream stream, bool leaveOpen, MemoryMappedBlob? blob = null)
    {
        MemoryMode = memoryMode;
        _stream = stream;
        _leaveOpen = leaveOpen;
        _blob = blob;
    }

    /// <summary>The memory mode this input was opened for.</summary>
    public MemoryMode MemoryMode { get; }

    /// <summary>Whether <see cref="Seek"/> and <see cref="Position"/> are supported.</summary>
    public bool CanSeek => Stream.CanSeek;

    /// <summary>
    /// The current offset in the input at which the next read would occur.
    /// </summary>
    /// <remarks>Replaces the Java <c>getFilePointer()</c>, whose name presumes a file.</remarks>
    public long Position => Stream.Position;

    private Stream Stream => _stream ?? throw new ObjectDisposedException(nameof(HollowBlobInput));

    /// <summary>
    /// Opens a serial-access input over an in-memory blob.
    /// </summary>
    public static HollowBlobInput Serial(byte[] bytes) => Serial(new MemoryStream(bytes, writable: false));

    /// <summary>
    /// Opens a serial-access input over a stream.
    /// </summary>
    /// <param name="stream">The stream to read the blob from.</param>
    /// <param name="leaveOpen">
    /// When <see langword="true"/>, disposing this input leaves <paramref name="stream"/> open.
    /// </param>
    public static HollowBlobInput Serial(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new HollowBlobInput(MemoryMode.OnHeap, stream, leaveOpen);
    }

    /// <summary>
    /// Opens a random-access input over a blob file.
    /// </summary>
    public static HollowBlobInput RandomAccess(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new HollowBlobInput(
            MemoryMode.OnHeap,
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read),
            leaveOpen: false);
    }

    /// <summary>
    /// Opens a shared-memory input over a blob file, mapping it into the address space.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The header and the schemas are read through the stream exactly as they are on-heap; what changes
    /// is what happens to the record data behind them, which is left in the file and reached through the
    /// mapping. The stream and the mapping are two views of the same file, so a position in one is a
    /// position in the other.
    /// </para>
    /// <para>
    /// The mapping outlives this input: the data elements built from it read through it for as long as
    /// the state engine is alive, so disposing the input closes the stream and leaves the mapping be.
    /// It is released when nothing refers to it any more.
    /// </para>
    /// </remarks>
    public static HollowBlobInput Mapped(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        MemoryMappedBlob blob = MemoryMappedBlob.Map(path);

        return new HollowBlobInput(
            MemoryMode.SharedMemoryLazy,
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read),
            leaveOpen: false,
            blob);
    }

    /// <summary>
    /// The mapping this input reads through.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The input was not opened for shared-memory mode, so there is no mapping.
    /// </exception>
    public MemoryMappedBlob RequireMappedBlob() =>
        _blob ?? throw new InvalidOperationException(
            $"this input was opened in {MemoryMode} mode, which reads record data onto the heap rather "
            + "than through a mapping");

    /// <summary>
    /// Reads the next byte as an integer in the range 0 to 255, or -1 at end of input.
    /// </summary>
    public int Read() => Stream.ReadByte();

    /// <summary>
    /// Reads up to <paramref name="destination"/>.Length bytes, returning the number actually read.
    /// </summary>
    public int Read(Span<byte> destination) => Stream.Read(destination);

    /// <summary>
    /// Reads exactly <paramref name="destination"/>.Length bytes.
    /// </summary>
    /// <exception cref="EndOfStreamException">The input ended before the buffer was filled.</exception>
    public void ReadExactly(Span<byte> destination) => Stream.ReadExactly(destination);

    /// <summary>
    /// Reads a single byte.
    /// </summary>
    /// <exception cref="EndOfStreamException">The input has ended.</exception>
    public byte ReadByte()
    {
        int b = Stream.ReadByte();
        if (b == -1)
        {
            throw new EndOfStreamException();
        }

        return (byte)b;
    }

    /// <summary>Reads a big-endian signed 16-bit integer.</summary>
    public short ReadInt16()
    {
        Span<byte> buffer = stackalloc byte[sizeof(short)];
        Stream.ReadExactly(buffer);
        return BinaryPrimitives.ReadInt16BigEndian(buffer);
    }

    /// <summary>Reads a big-endian signed 32-bit integer.</summary>
    public int ReadInt32()
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        Stream.ReadExactly(buffer);
        return BinaryPrimitives.ReadInt32BigEndian(buffer);
    }

    /// <summary>Reads a big-endian signed 64-bit integer.</summary>
    public long ReadInt64()
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        Stream.ReadExactly(buffer);
        return BinaryPrimitives.ReadInt64BigEndian(buffer);
    }

    /// <summary>
    /// Reads a length-prefixed modified UTF-8 string, matching <c>java.io.DataInput.readUTF</c>.
    /// </summary>
    public string ReadUtf()
    {
        Span<byte> lengthBuffer = stackalloc byte[sizeof(ushort)];
        Stream.ReadExactly(lengthBuffer);
        int length = BinaryPrimitives.ReadUInt16BigEndian(lengthBuffer);

        byte[] rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            Span<byte> bytes = rented.AsSpan(0, length);
            Stream.ReadExactly(bytes);
            return ModifiedUtf8.GetString(bytes);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Sets the read position to <paramref name="position"/>, measured from the start of the input.
    /// </summary>
    /// <exception cref="NotSupportedException">The underlying stream is not seekable.</exception>
    public void Seek(long position) => Stream.Position = position;

    /// <summary>
    /// Skips <paramref name="count"/> bytes, which must all be there.
    /// </summary>
    /// <remarks>
    /// This is how shared-memory mode passes over record data: the bytes are left where they are and
    /// only their position is kept, so a short skip is a truncated blob rather than a benign end of
    /// input.
    /// </remarks>
    /// <exception cref="EndOfStreamException">The input ended first.</exception>
    public void Skip(long count)
    {
        long skipped = SkipBytes(count);

        if (skipped != count)
        {
            throw new EndOfStreamException(
                $"the blob ended {count - skipped} bytes into a {count}-byte run of record data");
        }
    }

    /// <summary>
    /// Attempts to skip <paramref name="count"/> bytes, returning the number actually skipped.
    /// </summary>
    public long SkipBytes(long count)
    {
        if (count <= 0)
        {
            return 0;
        }

        Stream stream = Stream;
        if (stream.CanSeek)
        {
            long start = stream.Position;
            long end = Math.Min(start + count, stream.Length);
            stream.Position = end;
            return end - start;
        }

        long skipped = 0;
        byte[] rented = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (skipped < count)
            {
                int toRead = (int)Math.Min(rented.Length, count - skipped);
                int read = stream.Read(rented, 0, toRead);
                if (read == 0)
                {
                    break;
                }

                skipped += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        return skipped;
    }

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
