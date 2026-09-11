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

using Hollow.Core.Memory.Pool;
using Hollow.Core.Read;

namespace Hollow.Core.Memory.Encoding;

/// <summary>
/// A bit string of fixed-width elements, backed by a <see cref="SegmentedLongArray"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Port note.</strong> The Java implementation reads elements with <c>sun.misc.Unsafe</c>,
/// loading eight bytes at an unaligned byte offset inside a <c>long[]</c>. That is fast but it is
/// little-endian-only, can read past the end of a segment (hence Java's extra "fencepost" slot per
/// segment), and constrains element widths to 58 bits.
/// </para>
/// <para>
/// This port instead composes each element from the one or two 64-bit words that actually contain it.
/// The result is safe, endian-independent, needs no fencepost, and imposes no width limit, at the cost
/// of an extra shift and or on reads that straddle a word boundary.
/// <see cref="GetElementValue(long, int)"/> and <see cref="GetLargeElementValue(long, int)"/> are
/// therefore equivalent here; both are retained so that ported call sites keep reading like the
/// original.
/// </para>
/// </remarks>
public sealed class FixedLengthElementArray : SegmentedLongArray, IFixedLengthData
{
    private readonly long _sizeBits;

    /// <summary>
    /// Initialises a bit string of <paramref name="numBits"/> bits, drawing its segments from
    /// <paramref name="memoryRecycler"/>.
    /// </summary>
    public FixedLengthElementArray(IArraySegmentRecycler memoryRecycler, long numBits)
        : base(memoryRecycler, numBits <= 0 ? 0 : ((numBits - 1) >> 6) + 1)
    {
        _sizeBits = numBits;
    }

    /// <summary>An approximation of the memory this bit string occupies, in bytes.</summary>
    public long ApproxHeapFootprintInBytes => _sizeBits / 8;

    /// <inheritdoc />
    public void ClearElementValue(long index, int bitsPerElement)
    {
        long whichLong = (long)((ulong)index >> 6);
        int whichBit = (int)(index & 0x3F);

        long mask = bitsPerElement == 64 ? -1L : (1L << bitsPerElement) - 1;

        Set(whichLong, Get(whichLong) & ~(mask << whichBit));

        int bitsRemaining = 64 - whichBit;

        if (bitsRemaining < bitsPerElement)
        {
            Set(whichLong + 1, Get(whichLong + 1) & ~(long)((ulong)mask >> bitsRemaining));
        }
    }

    /// <inheritdoc />
    public void SetElementValue(long index, int bitsPerElement, long value)
    {
        long whichLong = (long)((ulong)index >> 6);
        int whichBit = (int)(index & 0x3F);

        Set(whichLong, Get(whichLong) | (value << whichBit));

        int bitsRemaining = 64 - whichBit;

        if (bitsRemaining < bitsPerElement)
        {
            Set(whichLong + 1, Get(whichLong + 1) | (long)((ulong)value >> bitsRemaining));
        }
    }

    /// <inheritdoc />
    public long GetElementValue(long index, int bitsPerElement) =>
        GetLargeElementValue(index, bitsPerElement);

    /// <inheritdoc />
    public long GetElementValue(long index, int bitsPerElement, long mask) =>
        GetLargeElementValue(index, bitsPerElement, mask);

    /// <inheritdoc />
    public long GetLargeElementValue(long index, int bitsPerElement)
    {
        long mask = bitsPerElement == 64 ? -1L : (1L << bitsPerElement) - 1;
        return GetLargeElementValue(index, bitsPerElement, mask);
    }

    /// <inheritdoc />
    public long GetLargeElementValue(long index, int bitsPerElement, long mask)
    {
        long whichLong = (long)((ulong)index >> 6);
        int whichBit = (int)(index & 0x3F);

        long value = (long)((ulong)Get(whichLong) >> whichBit);

        int bitsRemaining = 64 - whichBit;

        if (bitsRemaining < bitsPerElement)
        {
            value |= Get(whichLong + 1) << bitsRemaining;
        }

        return value & mask;
    }

    /// <inheritdoc />
    public void CopyBits(IFixedLengthData copyFrom, long sourceStartBit, long destinationStartBit, long numBits)
    {
        ArgumentNullException.ThrowIfNull(copyFrom);

        if (numBits == 0)
        {
            return;
        }

        if ((destinationStartBit & 63) != 0)
        {
            int fillBits = (int)Math.Min(64 - (destinationStartBit & 63), numBits);
            long fillValue = copyFrom.GetLargeElementValue(sourceStartBit, fillBits);
            SetElementValue(destinationStartBit, fillBits, fillValue);

            destinationStartBit += fillBits;
            sourceStartBit += fillBits;
            numBits -= fillBits;
        }

        long currentWriteLong = (long)((ulong)destinationStartBit >> 6);

        while (numBits >= 64)
        {
            long value = copyFrom.GetLargeElementValue(sourceStartBit, 64, -1L);
            Set(currentWriteLong, value);
            numBits -= 64;
            sourceStartBit += 64;
            currentWriteLong++;
        }

        if (numBits != 0)
        {
            destinationStartBit = currentWriteLong << 6;

            long fillValue = copyFrom.GetLargeElementValue(sourceStartBit, (int)numBits);
            SetElementValue(destinationStartBit, (int)numBits, fillValue);
        }
    }

    /// <inheritdoc />
    public void IncrementMany(long startBit, long increment, long bitsBetweenIncrements, int numIncrements)
    {
        long endBit = startBit + (bitsBetweenIncrements * numIncrements);
        for (; startBit < endBit; startBit += bitsBetweenIncrements)
        {
            Increment(startBit, increment);
        }
    }

    /// <summary>
    /// Adds <paramref name="increment"/> to the value at bit <paramref name="index"/>.
    /// </summary>
    /// <remarks>
    /// The addition happens inside the 64-bit window that begins at the byte containing
    /// <paramref name="index"/>, matching the Java implementation: a carry or borrow propagates upward
    /// through that window and is then discarded. Callers are expected to size elements so that
    /// carries stay within the element, in which case the window boundary never matters.
    /// </remarks>
    public void Increment(long index, long increment)
    {
        long whichByte = (long)((ulong)index >> 3);
        int bitInByte = (int)(index & 0x07);

        // The window starts at a byte boundary, so it straddles at most two 64-bit words.
        long whichLong = (long)((ulong)whichByte >> 3);
        int windowShift = (int)(whichByte & 0x07) * 8;

        bool hasUpperWord = windowShift != 0 && whichLong + 1 < Capacity;

        ulong low = (ulong)Get(whichLong);
        ulong high = hasUpperWord ? (ulong)Get(whichLong + 1) : 0UL;

        ulong window = windowShift == 0 ? low : (low >> windowShift) | (high << (64 - windowShift));
        window = unchecked(window + ((ulong)increment << bitInByte));

        if (windowShift == 0)
        {
            Set(whichLong, (long)window);
            return;
        }

        ulong lowMask = (1UL << windowShift) - 1;
        Set(whichLong, (long)((low & lowMask) | (window << windowShift)));

        if (hasUpperWord)
        {
            ulong highMask = ~((1UL << windowShift) - 1);
            Set(whichLong + 1, (long)((high & highMask) | (window >> (64 - windowShift))));
        }
    }

    /// <summary>
    /// Reads a bit string whose serialised form begins with its length in 64-bit words.
    /// </summary>
    public static FixedLengthElementArray NewFrom(HollowBlobInput input, IArraySegmentRecycler memoryRecycler)
    {
        ArgumentNullException.ThrowIfNull(input);

        long numLongs = VarInt.ReadVLong(input);
        return NewFrom(input, memoryRecycler, numLongs);
    }

    /// <summary>
    /// Reads a bit string of <paramref name="numLongs"/> 64-bit words.
    /// </summary>
    public static FixedLengthElementArray NewFrom(
        HollowBlobInput input, IArraySegmentRecycler memoryRecycler, long numLongs)
    {
        ArgumentNullException.ThrowIfNull(input);

        FixedLengthElementArray array = new(memoryRecycler, numLongs * 64);
        array.ReadFrom(input, numLongs);
        return array;
    }
}
