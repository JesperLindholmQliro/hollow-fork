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

using System.Numerics;
using Hollow.Core.Memory.Pool;

namespace Hollow.Core.Index;

/// <summary>
/// A long array of unbounded length, assembled from pooled segments as it is written.
/// </summary>
/// <remarks>
/// Unlike <see cref="Memory.SegmentedLongArray"/>, which is sized up front, this one grows on demand
/// and reads an unwritten index as zero. The hash index builder needs that: it does not know how many
/// matches a dataset will produce until it has traversed it.
/// </remarks>
public sealed class GrowingSegmentedLongArray
{
    private readonly IArraySegmentRecycler _memoryRecycler;
    private readonly int _log2OfSegmentSize;
    private readonly int _bitmask;

    private long[]?[] _segments;

    /// <summary>
    /// Initialises an empty array drawing its segments from <paramref name="memoryRecycler"/>.
    /// </summary>
    public GrowingSegmentedLongArray(IArraySegmentRecycler memoryRecycler)
    {
        ArgumentNullException.ThrowIfNull(memoryRecycler);

        _memoryRecycler = memoryRecycler;
        _log2OfSegmentSize = memoryRecycler.Log2OfLongSegmentSize;
        _bitmask = (1 << _log2OfSegmentSize) - 1;
        _segments = new long[64][];
    }

    /// <summary>
    /// Sets the value at <paramref name="index"/>, allocating a segment for it if necessary.
    /// </summary>
    public void Set(long index, long value)
    {
        int segmentIndex = (int)(index >> _log2OfSegmentSize);

        if (segmentIndex >= _segments.Length)
        {
            int nextPowerOfTwo = 1 << (32 - BitOperations.LeadingZeroCount((uint)segmentIndex));
            Array.Resize(ref _segments, nextPowerOfTwo);
        }

        _segments[segmentIndex] ??= _memoryRecycler.GetLongArray();
        _segments[segmentIndex]![(int)(index & _bitmask)] = value;
    }

    /// <summary>
    /// Gets the value at <paramref name="index"/>, which is zero if nothing was ever written there.
    /// </summary>
    public long Get(long index)
    {
        int segmentIndex = (int)(index >> _log2OfSegmentSize);

        return segmentIndex >= _segments.Length || _segments[segmentIndex] is not { } segment
            ? 0
            : segment[(int)(index & _bitmask)];
    }

    /// <summary>Returns every allocated segment to the recycler.</summary>
    public void Destroy()
    {
        for (int i = 0; i < _segments.Length; i++)
        {
            if (_segments[i] is { } segment)
            {
                _memoryRecycler.RecycleLongArray(segment);
                _segments[i] = null;
            }
        }
    }
}
