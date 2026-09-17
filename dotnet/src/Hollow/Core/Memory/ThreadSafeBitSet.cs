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

using Hollow.Core.Util;
using System.Collections;
using System.Numerics;
using Hollow.Core.Write;

namespace Hollow.Core.Memory;

/// <summary>
/// A lock-free, thread-safe bit set.
/// </summary>
/// <remarks>
/// <para>
/// Bits live in segments of <c>long</c>s that are published atomically and never mutated in place
/// except by compare-and-swap, so readers always observe a consistent segment array.
/// </para>
/// <para>
/// Java uses <c>AtomicLongArray</c>; .NET has no such type, so this port uses plain <c>long[]</c>
/// segments with <see cref="Interlocked"/> and <see cref="Volatile"/> operations on their elements,
/// which is the equivalent guarantee.
/// </para>
/// </remarks>
public sealed class ThreadSafeBitSet : IEquatable<ThreadSafeBitSet>
{
    /// <summary>The default segment size: 16384 bits, 2048 bytes, 256 longs.</summary>
    public const int DefaultLog2SegmentSizeInBits = 14;

    private readonly int _numLongsPerSegment;
    private readonly int _log2SegmentSize;
    private readonly int _segmentMask;
    private Segments _segments;

    /// <summary>
    /// Initialises a bit set with default-sized segments.
    /// </summary>
    public ThreadSafeBitSet()
        : this(DefaultLog2SegmentSizeInBits)
    {
    }

    /// <summary>
    /// Initialises a bit set whose segments each hold <c>2^log2SegmentSizeInBits</c> bits.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="log2SegmentSizeInBits"/> is less than 6, which would put fewer than 64 bits in
    /// each segment.
    /// </exception>
    public ThreadSafeBitSet(int log2SegmentSizeInBits, int numBitsToPreallocate = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(log2SegmentSizeInBits, 6);

        _log2SegmentSize = log2SegmentSizeInBits;
        _numLongsPerSegment = 1 << (log2SegmentSizeInBits - 6);
        _segmentMask = _numLongsPerSegment - 1;

        long numBitsPerSegment = (long)_numLongsPerSegment * 64;
        int numSegmentsToPreallocate = numBitsToPreallocate == 0
            ? 1
            : (int)(((numBitsToPreallocate - 1) / numBitsPerSegment) + 1);

        _segments = new Segments(numSegmentsToPreallocate, _numLongsPerSegment);
    }

    /// <summary>Sets the bit at <paramref name="position"/>.</summary>
    public void Set(int position)
    {
        long[] segment = GetSegment(position >>> _log2SegmentSize);
        int longPosition = (position >>> 6) & _segmentMask;
        long mask = 1L << (position & 0x3F);

        // Loop until we win the race to publish the new long value.
        while (true)
        {
            long current = Volatile.Read(ref segment[longPosition]);
            if (Interlocked.CompareExchange(ref segment[longPosition], current | mask, current) == current)
            {
                return;
            }
        }
    }

    /// <summary>Clears the bit at <paramref name="position"/>.</summary>
    public void Clear(int position)
    {
        long[] segment = GetSegment(position >>> _log2SegmentSize);
        int longPosition = (position >>> 6) & _segmentMask;
        long mask = ~(1L << (position & 0x3F));

        while (true)
        {
            long current = Volatile.Read(ref segment[longPosition]);
            if (Interlocked.CompareExchange(ref segment[longPosition], current & mask, current) == current)
            {
                return;
            }
        }
    }

    /// <summary>Gets the bit at <paramref name="position"/>.</summary>
    public bool Get(int position)
    {
        long[] segment = GetSegment(position >>> _log2SegmentSize);
        int longPosition = (position >>> 6) & _segmentMask;
        long mask = 1L << (position & 0x3F);

        return (Volatile.Read(ref segment[longPosition]) & mask) != 0;
    }

    /// <summary>
    /// The highest set bit, or -1 when no bit is set.
    /// </summary>
    public long MaxSetBit()
    {
        Segments segments = Volatile.Read(ref _segments);

        for (int segmentIndex = segments.Count - 1; segmentIndex >= 0; segmentIndex--)
        {
            long[] segment = segments[segmentIndex];
            for (int longIndex = segment.Length - 1; longIndex >= 0; longIndex--)
            {
                long value = Volatile.Read(ref segment[longIndex]);
                if (value != 0)
                {
                    return ((long)segmentIndex << _log2SegmentSize)
                        + ((long)longIndex * 64)
                        + (63 - BitOperations.LeadingZeroCount((ulong)value));
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// The first set bit at or after <paramref name="fromIndex"/>, or -1 when there is none.
    /// </summary>
    public int NextSetBit(int fromIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fromIndex);

        Segments segments = Volatile.Read(ref _segments);

        int segmentPosition = fromIndex >>> _log2SegmentSize;
        if (segmentPosition >= segments.Count)
        {
            return -1;
        }

        int longPosition = (fromIndex >>> 6) & _segmentMask;
        long[] segment = segments[segmentPosition];

        long word = Volatile.Read(ref segment[longPosition]) & (unchecked((long)0xFFFFFFFFFFFFFFFFUL) << (fromIndex & 0x3F));

        while (true)
        {
            if (word != 0)
            {
                return (segmentPosition << _log2SegmentSize)
                    + (longPosition << 6)
                    + BitOperations.TrailingZeroCount((ulong)word);
            }

            if (++longPosition > _segmentMask)
            {
                segmentPosition++;
                if (segmentPosition >= segments.Count)
                {
                    return -1;
                }

                segment = segments[segmentPosition];
                longPosition = 0;
            }

            word = Volatile.Read(ref segment[longPosition]);
        }
    }

    /// <summary>
    /// The number of bits which are set in this bit set.
    /// </summary>
    public int Cardinality()
    {
        Segments segments = Volatile.Read(ref _segments);

        int numSetBits = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            long[] segment = segments[i];
            for (int j = 0; j < segment.Length; j++)
            {
                numSetBits += BitOperations.PopCount((ulong)Volatile.Read(ref segment[j]));
            }
        }

        return numSetBits;
    }

    /// <summary>
    /// The number of bits currently addressable by this bit set, which is the upper bound for
    /// iteration.
    /// </summary>
    public int CurrentCapacity => Volatile.Read(ref _segments).Count * (1 << _log2SegmentSize);

    /// <summary>Clears every bit.</summary>
    public void ClearAll()
    {
        Segments segments = Volatile.Read(ref _segments);

        for (int i = 0; i < segments.Count; i++)
        {
            long[] segment = segments[i];
            for (int j = 0; j < segment.Length; j++)
            {
                Volatile.Write(ref segment[j], 0L);
            }
        }
    }

    /// <summary>
    /// Returns a new bit set containing the bits set in this bit set but not in
    /// <paramref name="other"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The two bit sets have different segment sizes.</exception>
    public ThreadSafeBitSet AndNot(ThreadSafeBitSet other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (other._log2SegmentSize != _log2SegmentSize)
        {
            throw new ArgumentException("Segment sizes must be the same", nameof(other));
        }

        Segments theseSegments = Volatile.Read(ref _segments);
        Segments otherSegments = Volatile.Read(ref other._segments);
        Segments newSegments = new(theseSegments.Count, _numLongsPerSegment);

        for (int i = 0; i < theseSegments.Count; i++)
        {
            long[] thisArray = theseSegments[i];
            long[]? otherArray = i < otherSegments.Count ? otherSegments[i] : null;
            long[] newArray = newSegments[i];

            for (int j = 0; j < thisArray.Length; j++)
            {
                long otherLong = otherArray is null ? 0 : Volatile.Read(ref otherArray[j]);
                newArray[j] = Volatile.Read(ref thisArray[j]) & ~otherLong;
            }
        }

        return new ThreadSafeBitSet(_log2SegmentSize) { _segments = newSegments };
    }

    /// <summary>
    /// Returns a new bit set containing every bit set in any of <paramref name="bitSets"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The bit sets do not all have the same segment size.</exception>
    public static ThreadSafeBitSet OrAll(params ThreadSafeBitSet[] bitSets)
    {
        ArgumentNullException.ThrowIfNull(bitSets);

        if (bitSets.Length == 0)
        {
            return new ThreadSafeBitSet();
        }

        int log2SegmentSize = bitSets[0]._log2SegmentSize;
        int numLongsPerSegment = bitSets[0]._numLongsPerSegment;

        Segments[] segments = new Segments[bitSets.Length];
        int maxNumSegments = 0;

        for (int i = 0; i < bitSets.Length; i++)
        {
            if (bitSets[i]._log2SegmentSize != log2SegmentSize)
            {
                throw new ArgumentException("Segment sizes must be the same", nameof(bitSets));
            }

            segments[i] = Volatile.Read(ref bitSets[i]._segments);
            maxNumSegments = Math.Max(maxNumSegments, segments[i].Count);
        }

        Segments newSegments = new(maxNumSegments, numLongsPerSegment);

        for (int i = 0; i < maxNumSegments; i++)
        {
            long[] newSegment = newSegments[i];

            for (int j = 0; j < numLongsPerSegment; j++)
            {
                long value = 0;
                foreach (Segments source in segments)
                {
                    if (i < source.Count)
                    {
                        long[] segment = source[i];
                        value |= Volatile.Read(ref segment[j]);
                    }
                }

                newSegment[j] = value;
            }
        }

        return new ThreadSafeBitSet(log2SegmentSize) { _segments = newSegments };
    }

    /// <summary>
    /// Writes the raw bits, preceded by the number of 64-bit words.
    /// </summary>
    public void SerializeBitsTo(HollowBlobOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);

        Segments segments = Volatile.Read(ref _segments);

        output.WriteInt32(segments.Count * _numLongsPerSegment);

        for (int i = 0; i < segments.Count; i++)
        {
            long[] segment = segments[i];
            for (int j = 0; j < segment.Length; j++)
            {
                output.WriteInt64(Volatile.Read(ref segment[j]));
            }
        }
    }

    /// <summary>
    /// Enumerates the positions of the set bits, in ascending order.
    /// </summary>
    /// <remarks>
    /// Replaces Java's <c>toBitSet()</c>, which materialises a <c>java.util.BitSet</c>; enumerating is
    /// both cheaper and more useful to a .NET caller, who can build a
    /// <see cref="BitArray"/> from it if one is wanted.
    /// </remarks>
    public IEnumerable<int> EnumerateSetBits()
    {
        for (int ordinal = NextSetBit(0); ordinal != -1; ordinal = NextSetBit(ordinal + 1))
        {
            yield return ordinal;
        }
    }

    /// <summary>
    /// Returns a <see cref="BitArray"/> with the same bits set.
    /// </summary>
    public BitArray ToBitArray()
    {
        long maxSetBit = MaxSetBit();
        BitArray result = new(maxSetBit < 0 ? 0 : (int)maxSetBit + 1);

        foreach (int ordinal in EnumerateSetBits())
        {
            result[ordinal] = true;
        }

        return result;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Two bit sets built with different segment sizes are unequal rather than incomparable. Java throws
    /// <c>IllegalArgumentException</c> here; .NET cannot, because <see cref="object.Equals(object)"/> is
    /// contractually forbidden from throwing, and a set that threw would take <c>Contains</c>,
    /// <c>Distinct</c> and every dictionary lookup down with it.
    /// </remarks>
    public bool Equals(ThreadSafeBitSet? other)
    {
        if (other is null)
        {
            return false;
        }

        if (other._log2SegmentSize != _log2SegmentSize)
        {
            return false;
        }

        Segments theseSegments = Volatile.Read(ref _segments);
        Segments otherSegments = Volatile.Read(ref other._segments);

        for (int i = 0; i < theseSegments.Count; i++)
        {
            long[] thisArray = theseSegments[i];
            long[]? otherArray = i < otherSegments.Count ? otherSegments[i] : null;

            for (int j = 0; j < thisArray.Length; j++)
            {
                long otherLong = otherArray is null ? 0 : Volatile.Read(ref otherArray[j]);
                if (Volatile.Read(ref thisArray[j]) != otherLong)
                {
                    return false;
                }
            }
        }

        for (int i = theseSegments.Count; i < otherSegments.Count; i++)
        {
            long[] otherArray = otherSegments[i];
            for (int j = 0; j < otherArray.Length; j++)
            {
                if (Volatile.Read(ref otherArray[j]) != 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ThreadSafeBitSet other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        // Only the set bits contribute, so that two equal bit sets with different segment counts agree.
        System.HashCode hash = default;
        hash.Add(_log2SegmentSize);
        foreach (int ordinal in EnumerateSetBits())
        {
            hash.Add(ordinal);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"{{{InvariantFormatting.JoinInvariant(", ", EnumerateSetBits())}}}";

    /// <summary>
    /// Gets the segment at <paramref name="segmentIndex"/>, growing the segment array if it does not
    /// yet exist.
    /// </summary>
    private long[] GetSegment(int segmentIndex)
    {
        Segments visibleSegments = Volatile.Read(ref _segments);

        while (visibleSegments.Count <= segmentIndex)
        {
            // newVisibleSegments shares every segment of the currently visible segments, which are
            // canonical and will not change, and adds more.
            Segments newVisibleSegments = new(visibleSegments, segmentIndex + 1, _numLongsPerSegment);

            // If this thread wins the race, the segments newly defined in newVisibleSegments become
            // canonical. If it loses, they are discarded and the next iteration picks up whichever
            // segments did become canonical.
            Segments witnessed = Interlocked.CompareExchange(ref _segments, newVisibleSegments, visibleSegments);
            visibleSegments = ReferenceEquals(witnessed, visibleSegments) ? newVisibleSegments : witnessed;
        }

        return visibleSegments[segmentIndex];
    }

    /// <summary>
    /// An immutable array of segments, published as a whole so that readers never see a partially
    /// built array.
    /// </summary>
    private sealed class Segments
    {
        private readonly long[][] _segments;

        internal Segments(int numSegments, int segmentLength)
        {
            long[][] segments = new long[numSegments][];
            for (int i = 0; i < numSegments; i++)
            {
                segments[i] = new long[segmentLength];
            }

            // Assigning to a readonly field last makes the preceding writes visible to any thread that
            // observes this instance.
            _segments = segments;
        }

        internal Segments(Segments copyFrom, int numSegments, int segmentLength)
        {
            long[][] segments = new long[numSegments][];
            for (int i = 0; i < numSegments; i++)
            {
                segments[i] = i < copyFrom.Count ? copyFrom[i] : new long[segmentLength];
            }

            _segments = segments;
        }

        internal int Count => _segments.Length;

        internal long[] this[int index] => _segments[index];
    }
}
