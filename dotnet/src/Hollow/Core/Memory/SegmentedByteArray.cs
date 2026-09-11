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
using System.Threading;
using Hollow.Core.Memory.Pool;
using Hollow.Core.Read;

namespace Hollow.Core.Memory;

/// <summary>
/// Backs <see cref="IByteData"/> with array segments, which potentially come from a pool of reusable
/// memory.
/// </summary>
/// <remarks>
/// <para>
/// This data can grow without allocating successively larger blocks and copying memory. Segment length
/// is always a power of two so that the location of a given index can be found with mask and shift
/// operations.
/// </para>
/// <para>
/// Conceptually this can be thought of as a single byte array of undefined length. The currently
/// allocated buffer is always a multiple of the segment size, and grows automatically when a byte is
/// written to an index beyond it.
/// </para>
/// </remarks>
/// <seealso cref="IArraySegmentRecycler" />
public sealed class SegmentedByteArray : IVariableLengthData
{
    private readonly int _log2OfSegmentSize;
    private readonly int _bitmask;
    private readonly IArraySegmentRecycler _memoryRecycler;
    private byte[]?[] _segments;

    /// <summary>
    /// Initialises an empty array drawing its segments from <paramref name="memoryRecycler"/>.
    /// </summary>
    public SegmentedByteArray(IArraySegmentRecycler memoryRecycler)
    {
        ArgumentNullException.ThrowIfNull(memoryRecycler);

        _segments = new byte[2][];
        _log2OfSegmentSize = memoryRecycler.Log2OfByteSegmentSize;
        _bitmask = (1 << _log2OfSegmentSize) - 1;
        _memoryRecycler = memoryRecycler;
    }

    /// <inheritdoc />
    public long Size
    {
        get
        {
            long size = 0;
            foreach (byte[]? segment in _segments)
            {
                if (segment is not null)
                {
                    size += segment.Length;
                }
            }

            return size;
        }
    }

    /// <summary>
    /// Sets the byte at <paramref name="index"/>, growing the array if required.
    /// </summary>
    public void Set(long index, byte value)
    {
        int segmentIndex = (int)(index >> _log2OfSegmentSize);
        EnsureCapacity(segmentIndex);
        _segments[segmentIndex]![(int)(index & _bitmask)] = value;
    }

    /// <inheritdoc />
    public byte Get(long index) => _segments[(int)((ulong)index >> _log2OfSegmentSize)]![(int)(index & _bitmask)];

    /// <inheritdoc />
    public void Copy(IByteData source, long sourcePosition, long destinationPosition, long length)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source is SegmentedByteArray segmented)
        {
            Copy(segmented, sourcePosition, destinationPosition, length);
            return;
        }

        for (long i = 0; i < length; i++)
        {
            Set(destinationPosition++, source.Get(sourcePosition++));
        }
    }

    /// <summary>
    /// A faster <see cref="Copy(IByteData, long, long, long)"/> for another segmented array, which can
    /// be copied a segment at a time rather than a byte at a time.
    /// </summary>
    /// <param name="source">The source data.</param>
    /// <param name="sourcePosition">Position in the source data to begin copying from.</param>
    /// <param name="destinationPosition">Position in this array to begin writing at.</param>
    /// <param name="length">Length of the data to copy, in bytes.</param>
    public void Copy(SegmentedByteArray source, long sourcePosition, long destinationPosition, long length)
    {
        ArgumentNullException.ThrowIfNull(source);

        int segmentLength = 1 << _log2OfSegmentSize;
        int currentSegment = (int)((ulong)destinationPosition >> _log2OfSegmentSize);
        int segmentStartPos = (int)(destinationPosition & _bitmask);
        int remainingBytesInSegment = segmentLength - segmentStartPos;

        while (length > 0)
        {
            int bytesToCopyFromSegment = (int)Math.Min(remainingBytesInSegment, length);
            EnsureCapacity(currentSegment);
            int copiedBytes = source.CopyTo(sourcePosition, _segments[currentSegment]!, segmentStartPos, bytesToCopyFromSegment);

            sourcePosition += copiedBytes;
            length -= copiedBytes;
            segmentStartPos = 0;
            remainingBytesInSegment = segmentLength;
            currentSegment++;
        }
    }

    /// <summary>
    /// Copies exactly <paramref name="length"/> bytes out of this array into
    /// <paramref name="destination"/>, returning the number of bytes copied.
    /// </summary>
    /// <remarks>
    /// Java overloads <c>copy</c> for both directions; this direction is named <c>CopyTo</c> here
    /// because C# cannot distinguish the overloads by parameter order alone as clearly, and the
    /// direction is worth stating at the call site.
    /// </remarks>
    /// <param name="sourcePosition">Position in this array to begin copying from.</param>
    /// <param name="destination">The destination array.</param>
    /// <param name="destinationPosition">Position in the destination to begin writing at.</param>
    /// <param name="length">Length of the data to copy, in bytes.</param>
    public int CopyTo(long sourcePosition, byte[] destination, int destinationPosition, int length)
    {
        ArgumentNullException.ThrowIfNull(destination);

        int segmentSize = 1 << _log2OfSegmentSize;
        int remainingBytesInSegment = (int)(segmentSize - (sourcePosition & _bitmask));
        int dataPosition = destinationPosition;

        while (length > 0)
        {
            byte[] segment = _segments[(int)((ulong)sourcePosition >> _log2OfSegmentSize)]!;
            int bytesToCopyFromSegment = Math.Min(remainingBytesInSegment, length);

            Array.Copy(segment, (int)(sourcePosition & _bitmask), destination, dataPosition, bytesToCopyFromSegment);

            dataPosition += bytesToCopyFromSegment;
            sourcePosition += bytesToCopyFromSegment;
            remainingBytesInSegment = segmentSize - (int)(sourcePosition & _bitmask);
            length -= bytesToCopyFromSegment;
        }

        return dataPosition - destinationPosition;
    }

    /// <summary>
    /// Checks equality for a specified range of bytes in two arrays.
    /// </summary>
    /// <param name="rangeStart">The start position of the comparison range in this array.</param>
    /// <param name="compareTo">The other array to compare.</param>
    /// <param name="compareStart">The start position of the comparison range in the other array.</param>
    /// <param name="length">The length of the comparison range.</param>
    public bool RangeEquals(long rangeStart, SegmentedByteArray compareTo, long compareStart, int length)
    {
        ArgumentNullException.ThrowIfNull(compareTo);

        for (int i = 0; i < length; i++)
        {
            if (Get(rangeStart + i) != compareTo.Get(compareStart + i))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public void OrderedCopy(IVariableLengthData source, long sourcePosition, long destinationPosition, long length)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source is not SegmentedByteArray segmentedSource)
        {
            throw new ArgumentException(
                $"ordered copy requires a {nameof(SegmentedByteArray)} source", nameof(source));
        }

        int segmentLength = 1 << _log2OfSegmentSize;
        int currentSegment = (int)((ulong)destinationPosition >> _log2OfSegmentSize);
        int segmentStartPos = (int)(destinationPosition & _bitmask);
        int remainingBytesInSegment = segmentLength - segmentStartPos;

        while (length > 0)
        {
            int bytesToCopyFromSegment = (int)Math.Min(remainingBytesInSegment, length);
            EnsureCapacity(currentSegment);
            int copiedBytes = segmentedSource.OrderedCopyTo(
                sourcePosition, _segments[currentSegment]!, segmentStartPos, bytesToCopyFromSegment);

            sourcePosition += copiedBytes;
            length -= copiedBytes;
            segmentStartPos = 0;
            remainingBytesInSegment = segmentLength;
            currentSegment++;
        }
    }

    /// <inheritdoc />
    public void LoadFrom(HollowBlobInput input, long length)
    {
        ArgumentNullException.ThrowIfNull(input);

        int segmentSize = 1 << _log2OfSegmentSize;
        int segment = 0;

        byte[] scratch = ArrayPool<byte>.Shared.Rent(segmentSize);
        try
        {
            while (length > 0)
            {
                EnsureCapacity(segment);
                int bytesToCopy = (int)Math.Min(segmentSize, length);
                input.ReadExactly(scratch.AsSpan(0, bytesToCopy));
                OrderedCopy(scratch, 0, _segments[segment++]!, 0, bytesToCopy);
                length -= bytesToCopy;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    /// <summary>
    /// Writes a portion of this data to a stream.
    /// </summary>
    /// <param name="destination">The stream to write to.</param>
    /// <param name="startPosition">The position to begin copying from this array.</param>
    /// <param name="length">The length of the data to copy.</param>
    public void WriteTo(Stream destination, long startPosition, long length)
    {
        ArgumentNullException.ThrowIfNull(destination);

        int segmentSize = 1 << _log2OfSegmentSize;
        int remainingBytesInSegment = segmentSize - (int)(startPosition & _bitmask);
        long remainingBytesInCopy = length;

        while (remainingBytesInCopy > 0)
        {
            int bytesToCopyFromSegment = (int)Math.Min(remainingBytesInSegment, remainingBytesInCopy);

            destination.Write(
                _segments[(int)((ulong)startPosition >> _log2OfSegmentSize)]!,
                (int)(startPosition & _bitmask),
                bytesToCopyFromSegment);

            startPosition += bytesToCopyFromSegment;
            remainingBytesInSegment = segmentSize - (int)(startPosition & _bitmask);
            remainingBytesInCopy -= bytesToCopyFromSegment;
        }
    }

    /// <summary>
    /// Returns every allocated segment to the recycler this array was created with.
    /// </summary>
    public void Destroy()
    {
        for (int i = 0; i < _segments.Length; i++)
        {
            if (_segments[i] is { } segment)
            {
                _memoryRecycler.RecycleByteArray(segment);
                _segments[i] = null;
            }
        }
    }

    /// <summary>
    /// Copies exactly <paramref name="length"/> bytes out of this array into
    /// <paramref name="destination"/>, guaranteeing that if the update is seen by another thread then
    /// all writes prior to this call are also visible to that thread.
    /// </summary>
    private int OrderedCopyTo(long sourcePosition, byte[] destination, int destinationPosition, int length)
    {
        int segmentSize = 1 << _log2OfSegmentSize;
        int remainingBytesInSegment = (int)(segmentSize - (sourcePosition & _bitmask));
        int dataPosition = destinationPosition;

        while (length > 0)
        {
            byte[] segment = _segments[(int)((ulong)sourcePosition >> _log2OfSegmentSize)]!;
            int bytesToCopyFromSegment = Math.Min(remainingBytesInSegment, length);

            OrderedCopy(segment, (int)(sourcePosition & _bitmask), destination, dataPosition, bytesToCopyFromSegment);

            dataPosition += bytesToCopyFromSegment;
            sourcePosition += bytesToCopyFromSegment;
            remainingBytesInSegment = segmentSize - (int)(sourcePosition & _bitmask);
            length -= bytesToCopyFromSegment;
        }

        return dataPosition - destinationPosition;
    }

    /// <summary>
    /// Copies bytes with release semantics, so that a reader observing any copied byte also observes
    /// every write that happened before this call.
    /// </summary>
    /// <remarks>
    /// Java uses <c>Unsafe.putByteVolatile</c> per byte. The .NET equivalent of a release store is
    /// <see cref="Volatile.Write(ref byte, byte)"/>, but issuing one release fence for the whole block
    /// is both cheaper and sufficient: the reader only ever publishes a record by writing the ordinal
    /// after the data, so a single fence before that publication orders all of it.
    /// </remarks>
    private static void OrderedCopy(byte[] source, int sourcePosition, byte[] destination, int destinationPosition, int length)
    {
        Array.Copy(source, sourcePosition, destination, destinationPosition, length);
        Interlocked.MemoryBarrier();
    }

    /// <summary>
    /// Ensures that the segment at <paramref name="segmentIndex"/> exists.
    /// </summary>
    private void EnsureCapacity(int segmentIndex)
    {
        if (segmentIndex >= _segments.Length)
        {
            int newLength = _segments.Length;
            while (segmentIndex >= newLength)
            {
                newLength = newLength * 3 / 2;
            }

            Array.Resize(ref _segments, newLength);
        }

        _segments[segmentIndex] ??= _memoryRecycler.GetByteArray();
    }
}
