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

using Hollow.Core.Memory.Encoding;
using Hollow.Core.Memory.Pool;
using Hollow.Core.Read;
using Hollow.Core.Write;

namespace Hollow.Core.Memory;

/// <summary>
/// A long array that can grow without allocating successively larger blocks and copying memory.
/// </summary>
/// <remarks>
/// <para>
/// Segment length is always a power of two so that the location of a given index can be found with
/// mask and shift operations. Conceptually this can be thought of as a single long array of undefined
/// length; the allocated buffer is always a multiple of the segment size.
/// </para>
/// <para>
/// <strong>Port note.</strong> The Java implementation allocates each segment one element longer than
/// its nominal size and duplicates the first word of each segment into that trailing slot of the
/// previous segment. That "fencepost" exists solely so that <c>sun.misc.Unsafe</c> can perform an
/// unaligned 8-byte read that runs off the end of a segment without faulting. This port composes
/// element values from the two words that actually hold them (see
/// <see cref="Encoding.FixedLengthElementArray"/>), so no fencepost is allocated or maintained. The
/// serialised form is unaffected: it is, and always was, just a count followed by that many big-endian
/// 64-bit words.
/// </para>
/// </remarks>
public class SegmentedLongArray
{
    /// <summary>The base-2 logarithm of the number of longs in each segment.</summary>
    protected readonly int Log2OfSegmentSize;

    /// <summary>Masks a long index down to its offset within a segment.</summary>
    protected readonly int Bitmask;

    private readonly long[][] _segments;

    /// <summary>
    /// Initialises an array able to hold at least <paramref name="numLongs"/> longs, drawing its
    /// segments from <paramref name="memoryRecycler"/>.
    /// </summary>
    public SegmentedLongArray(IArraySegmentRecycler memoryRecycler, long numLongs)
    {
        ArgumentNullException.ThrowIfNull(memoryRecycler);

        Log2OfSegmentSize = memoryRecycler.Log2OfLongSegmentSize;
        Bitmask = (1 << Log2OfSegmentSize) - 1;

        // Java computes this as ((numLongs - 1) >>> log2) + 1, which underflows into a nonsensical
        // segment count for an empty array. Always allocating at least one segment keeps zero-length
        // types (a type present in the schema with no records) working.
        int numSegments = numLongs <= 0
            ? 1
            : (int)((ulong)(numLongs - 1) >> Log2OfSegmentSize) + 1;
        long[][] segments = new long[numSegments][];
        for (int i = 0; i < segments.Length; i++)
        {
            segments[i] = memoryRecycler.GetLongArray();
        }

        Capacity = (long)numSegments << Log2OfSegmentSize;

        // The assignment is purposefully placed after the population of all segments: publishing the
        // fully built array last guarantees no thread can observe a null segment.
        _segments = segments;
    }

    /// <summary>The segments backing this array.</summary>
    protected long[][] Segments => _segments;

    /// <summary>
    /// The number of longs actually allocated, which is the requested length rounded up to a whole
    /// number of segments.
    /// </summary>
    public long Capacity { get; }

    /// <summary>
    /// Sets the long at <paramref name="index"/> to <paramref name="value"/>.
    /// </summary>
    /// <param name="index">
    /// The index in long units: index 0 occupies bytes 0-7, index 1 occupies bytes 8-15, and so on.
    /// </param>
    /// <param name="value">The value to store.</param>
    public void Set(long index, long value) =>
        _segments[(int)(index >> Log2OfSegmentSize)][(int)(index & Bitmask)] = value;

    /// <summary>
    /// Gets the long at <paramref name="index"/>.
    /// </summary>
    /// <param name="index">
    /// The index in long units: index 0 occupies bytes 0-7, index 1 occupies bytes 8-15, and so on.
    /// </param>
    public long Get(long index) =>
        _segments[(int)((ulong)index >> Log2OfSegmentSize)][(int)(index & Bitmask)];

    /// <summary>
    /// Sets every long in every allocated segment to <paramref name="value"/>.
    /// </summary>
    public void Fill(long value)
    {
        foreach (long[] segment in _segments)
        {
            Array.Fill(segment, value);
        }
    }

    /// <summary>
    /// Writes <paramref name="numLongs"/> longs to <paramref name="output"/>, preceded by the count.
    /// </summary>
    public void WriteTo(HollowBlobOutput output, long numLongs)
    {
        ArgumentNullException.ThrowIfNull(output);

        VarInt.WriteVLong(output, numLongs);

        for (long i = 0; i < numLongs; i++)
        {
            output.WriteInt64(Get(i));
        }
    }

    /// <summary>
    /// Returns every segment to <paramref name="memoryRecycler"/>.
    /// </summary>
    public void Destroy(IArraySegmentRecycler memoryRecycler)
    {
        ArgumentNullException.ThrowIfNull(memoryRecycler);

        foreach (long[] segment in _segments)
        {
            memoryRecycler.RecycleLongArray(segment);
        }
    }

    /// <summary>
    /// Reads <paramref name="numLongs"/> big-endian longs from <paramref name="input"/> into this array.
    /// </summary>
    protected void ReadFrom(HollowBlobInput input, long numLongs)
    {
        ArgumentNullException.ThrowIfNull(input);

        for (long i = 0; i < numLongs; i++)
        {
            Set(i, input.ReadInt64());
        }
    }
}
