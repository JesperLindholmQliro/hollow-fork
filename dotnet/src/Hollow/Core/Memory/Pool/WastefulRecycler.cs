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
/// An <see cref="IArraySegmentRecycler"/> which doesn't <em>really</em> pool arrays; it allocates them
/// on demand, in contrast with <see cref="RecyclingRecycler"/>.
/// </summary>
public sealed class WastefulRecycler : IArraySegmentRecycler
{
    /// <summary>The shared recycler used wherever no pool has been configured.</summary>
    public static readonly WastefulRecycler DefaultInstance = new(
        IArraySegmentRecycler.DefaultLog2ByteArraySize,
        IArraySegmentRecycler.DefaultLog2LongArraySize);

    /// <summary>A recycler handing out small segments, useful for tiny or short-lived structures.</summary>
    public static readonly WastefulRecycler SmallArrayRecycler = new(5, 2);

    /// <summary>
    /// Initialises a recycler handing out segments of the given base-2 logarithm lengths.
    /// </summary>
    public WastefulRecycler(int log2OfByteSegmentSize, int log2OfLongSegmentSize)
    {
        Log2OfByteSegmentSize = log2OfByteSegmentSize;
        Log2OfLongSegmentSize = log2OfLongSegmentSize;
    }

    /// <inheritdoc />
    public int Log2OfByteSegmentSize { get; }

    /// <inheritdoc />
    public int Log2OfLongSegmentSize { get; }

    /// <inheritdoc />
    public long[] GetLongArray() => new long[1 << Log2OfLongSegmentSize];

    /// <inheritdoc />
    public byte[] GetByteArray() => new byte[1 << Log2OfByteSegmentSize];

    /// <inheritdoc />
    public void RecycleLongArray(long[] array)
    {
        // Nothing to do; the GC reclaims the array.
    }

    /// <inheritdoc />
    public void RecycleByteArray(byte[] array)
    {
        // Nothing to do; the GC reclaims the array.
    }

    /// <inheritdoc />
    public void Swap()
    {
        // Nothing to do; this recycler holds no state.
    }
}
