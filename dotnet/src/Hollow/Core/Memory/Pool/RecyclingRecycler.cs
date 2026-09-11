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

namespace Hollow.Core.Memory.Pool;

/// <summary>
/// An <see cref="IArraySegmentRecycler"/> which actually pools arrays, in contrast with
/// <see cref="WastefulRecycler"/>.
/// </summary>
public sealed class RecyclingRecycler : IArraySegmentRecycler
{
    private readonly SegmentPool<long> _longSegments;
    private readonly SegmentPool<byte> _byteSegments;

    /// <summary>
    /// Initialises a recycler handing out default-sized segments.
    /// </summary>
    public RecyclingRecycler()
        : this(IArraySegmentRecycler.DefaultLog2ByteArraySize, IArraySegmentRecycler.DefaultLog2LongArraySize)
    {
    }

    /// <summary>
    /// Initialises a recycler handing out segments of the given base-2 logarithm lengths.
    /// </summary>
    public RecyclingRecycler(int log2OfByteSegmentSize, int log2OfLongSegmentSize)
    {
        Log2OfByteSegmentSize = log2OfByteSegmentSize;
        Log2OfLongSegmentSize = log2OfLongSegmentSize;

        _byteSegments = new SegmentPool<byte>(1 << log2OfByteSegmentSize);
        _longSegments = new SegmentPool<long>(1 << log2OfLongSegmentSize);
    }

    /// <inheritdoc />
    public int Log2OfByteSegmentSize { get; }

    /// <inheritdoc />
    public int Log2OfLongSegmentSize { get; }

    /// <inheritdoc />
    public long[] GetLongArray()
    {
        long[] array = _longSegments.Get();
        Array.Clear(array);
        return array;
    }

    /// <inheritdoc />
    public void RecycleLongArray(long[] array) => _longSegments.Recycle(array);

    /// <inheritdoc />
    public byte[] GetByteArray() => _byteSegments.Get();

    /// <inheritdoc />
    public void RecycleByteArray(byte[] array) => _byteSegments.Recycle(array);

    /// <inheritdoc />
    public void Swap()
    {
        _longSegments.Swap();
        _byteSegments.Swap();
    }

    /// <summary>
    /// A double-buffered free list: segments recycled during the current cycle only become available
    /// for reuse once <see cref="Swap"/> is called, so a segment can never be handed out while the
    /// state that recycled it is still being read.
    /// </summary>
    /// <remarks>
    /// Java uses <c>ArrayDeque</c> plus a <c>Creator</c> functional interface; <see cref="Queue{T}"/>
    /// and a captured array length cover both here.
    /// </remarks>
    private sealed class SegmentPool<T>(int segmentLength)
    {
        private Queue<T[]> _current = new();
        private Queue<T[]> _next = new();

        internal T[] Get() => _current.TryDequeue(out T[]? segment) ? segment : new T[segmentLength];

        internal void Recycle(T[] segment) => _next.Enqueue(segment);

        internal void Swap()
        {
            // Swap the queue references to reduce addition and clearing cost.
            if (_next.Count > _current.Count)
            {
                (_current, _next) = (_next, _current);
            }

            while (_next.TryDequeue(out T[]? segment))
            {
                _current.Enqueue(segment);
            }
        }
    }
}
