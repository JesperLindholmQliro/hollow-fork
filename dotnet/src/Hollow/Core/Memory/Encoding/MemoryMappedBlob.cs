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

using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

namespace Hollow.Core.Memory.Encoding;

/// <summary>
/// A blob file mapped into the address space, read from without being copied onto the heap.
/// </summary>
/// <remarks>
/// <para>
/// This is what shared-memory mode reads through. Nothing is loaded up front: the operating system
/// pages the file in as records are actually touched, and pages it back out under pressure. Two
/// processes mapping the same file share those pages, which is where the mode gets its name.
/// </para>
/// <para>
/// Named <c>BlobByteBuffer</c> in Java, which has to carry a <em>spine</em> of
/// <c>MappedByteBuffer</c>s — one per gigabyte — because a Java buffer is indexed by <c>int</c>. A
/// .NET view accessor takes a <c>long</c> offset, so one view covers the whole file and the spine,
/// its shift and mask arithmetic, and its 2-exabyte ceiling all go away.
/// </para>
/// <para>
/// A reader is free-threaded: reads take no locks and hold no position.
/// </para>
/// </remarks>
public sealed class MemoryMappedBlob : IDisposable
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;

    private bool _disposed;

    private MemoryMappedBlob(MemoryMappedFile file, MemoryMappedViewAccessor view, long capacity)
    {
        _file = file;
        _view = view;
        Capacity = capacity;
    }

    /// <summary>The size of the mapped file, in bytes.</summary>
    public long Capacity { get; }

    /// <summary>
    /// Maps <paramref name="path"/> for reading.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file is empty, so there is nothing to map.</exception>
    public static MemoryMappedBlob Map(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        long size = new FileInfo(path).Length;

        if (size == 0)
        {
            throw new InvalidOperationException($"the blob at {path} is empty, so there is nothing to map");
        }

        MemoryMappedFile file = MemoryMappedFile.CreateFromFile(
            path, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);

        try
        {
            return new MemoryMappedBlob(
                file, file.CreateViewAccessor(0, size, MemoryMappedFileAccess.Read), size);
        }
        catch
        {
            file.Dispose();

            throw;
        }
    }

    /// <summary>
    /// The byte at <paramref name="index"/>, counted from the start of the file.
    /// </summary>
    /// <remarks>
    /// A read that runs up to eight bytes past the end answers zero rather than failing. That happens
    /// where the last few bits of a bit string are reached through a 64-bit window: the bits past the
    /// end are shifted away by the caller, so what they held never mattered.
    /// </remarks>
    public byte GetByte(long index)
    {
        if (index >= Capacity)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Capacity + sizeof(long), nameof(index));

            return 0;
        }

        return _view.ReadByte(index);
    }

    /// <summary>
    /// The 64-bit word at <paramref name="wordIndex"/> of a bit string starting at
    /// <paramref name="byteOffset"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A bit string is written to a blob as a run of 64-bit words, each big-endian, because that is
    /// what <c>java.io.DataOutput</c> writes. The bit string's own numbering is the other way round —
    /// bit <c>i</c> is bit <c>i % 64</c> of word <c>i / 64</c>, counting from the least significant —
    /// so a word read out of the file has to be byte-swapped to be used.
    /// </para>
    /// <para>
    /// Java does this a byte at a time, mapping each logical byte to its position inside the stored
    /// word. Reading the whole word and swapping it is the same answer in one instruction.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetWord(long byteOffset, long wordIndex)
    {
        long at = byteOffset + (wordIndex * sizeof(long));

        // Past the end: a bit string's last word may be reached through a window that runs off the end,
        // and the bits that would come from beyond it are shifted away by the caller.
        if (at + sizeof(long) > Capacity)
        {
            return TrailingWord(at);
        }

        long stored = _view.ReadInt64(at);

        return BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(stored) : stored;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _view.Dispose();
        _file.Dispose();
    }

    /// <summary>
    /// Assembles a word that runs past the end of the file out of the bytes that are there.
    /// </summary>
    private long TrailingWord(long at)
    {
        long value = 0;

        for (int i = 0; i < sizeof(long); i++)
        {
            // Byte i of the logical word is the byte at the other end of the stored word, which is
            // what makes the whole-word read above a byte swap.
            value |= (long)GetByte(at + (sizeof(long) - 1 - i)) << (8 * i);
        }

        return value;
    }
}
