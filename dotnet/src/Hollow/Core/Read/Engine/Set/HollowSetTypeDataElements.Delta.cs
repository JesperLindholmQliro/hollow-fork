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

namespace Hollow.Core.Read.Engine.Set;

/// <summary>
/// Delta support for a set type's record storage.
/// </summary>
public sealed partial class HollowSetTypeDataElements
{


    /// <summary>
    /// Reads one shard's delta records from <paramref name="input"/>.
    /// </summary>
    public void ReadDelta(HollowBlobInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        MaxOrdinal = VarInt.ReadVInt(input);

        EncodedRemovals = GapEncodedVariableLengthIntegerReader.ReadEncodedDeltaOrdinals(input, MemoryRecycler);
        EncodedAdditions = GapEncodedVariableLengthIntegerReader.ReadEncodedDeltaOrdinals(input, MemoryRecycler);

        BitsPerSetPointer = VarInt.ReadVInt(input);
        BitsPerSetSizeValue = VarInt.ReadVInt(input);
        BitsPerElement = VarInt.ReadVInt(input);
        BitsPerFixedLengthSetPortion = BitsPerSetPointer + BitsPerSetSizeValue;
        EmptyBucketValue = (1 << BitsPerElement) - 1;
        TotalNumberOfBuckets = VarInt.ReadVLong(input);

        SetPointerAndSizeData = FixedLengthElementArray.NewFrom(input, MemoryRecycler);
        ElementData = FixedLengthElementArray.NewFrom(input, MemoryRecycler);
    }

    /// <summary>
    /// Builds the successor to <paramref name="from"/> by taking each ordinal's hash table from
    /// <paramref name="delta"/> where the delta adds it, and from <paramref name="from"/> otherwise.
    /// </summary>
    internal static HollowSetTypeDataElements ApplyDelta(
        HollowSetTypeDataElements from, HollowSetTypeDataElements delta)
    {
        HollowSetTypeDataElements target = new(from.MemoryRecycler)
        {
            MaxOrdinal = delta.MaxOrdinal,
            EncodedRemovals = delta.EncodedRemovals,
            BitsPerSetPointer = delta.BitsPerSetPointer,
            BitsPerSetSizeValue = delta.BitsPerSetSizeValue,
            BitsPerElement = delta.BitsPerElement,
            BitsPerFixedLengthSetPortion = delta.BitsPerSetPointer + delta.BitsPerSetSizeValue,
            EmptyBucketValue = (1 << delta.BitsPerElement) - 1,
            TotalNumberOfBuckets = delta.TotalNumberOfBuckets,
        };

        target.SetPointerAndSizeData = new FixedLengthElementArray(
            from.MemoryRecycler, ((long)target.MaxOrdinal + 1) * target.BitsPerFixedLengthSetPortion);
        target.ElementData = new FixedLengthElementArray(
            from.MemoryRecycler, target.TotalNumberOfBuckets * target.BitsPerElement);

        GapEncodedVariableLengthIntegerReader additions =
            delta.EncodedAdditions ?? GapEncodedVariableLengthIntegerReader.EmptyReader;
        additions.Reset();

        GapEncodedVariableLengthIntegerReader removals =
            from.EncodedRemovals ?? GapEncodedVariableLengthIntegerReader.EmptyReader;
        removals.Reset();

        long writeBucket = 0;
        int deltaOrdinal = 0;

        // The empty-bucket sentinel is all-ones at the element width, so buckets can only be copied
        // rather than rewritten when that width is unchanged. See CopyUnchangedRun.
        bool widthsUnchanged = target.BitsPerElement == from.BitsPerElement
            && target.BitsPerFixedLengthSetPortion == from.BitsPerFixedLengthSetPortion
            && target.BitsPerSetPointer == from.BitsPerSetPointer;

        int lastCarryable = Math.Min(from.MaxOrdinal, target.MaxOrdinal);

        for (int ordinal = 0; ordinal <= target.MaxOrdinal; ordinal++)
        {
            bool addedByDelta = additions.NextElement() == ordinal;
            bool removed = removals.NextElement() == ordinal;

            if (widthsUnchanged && !addedByDelta && !removed && ordinal <= lastCarryable)
            {
                // A removal inside the run would drop its buckets and shift everything after it by a
                // different amount, so the run stops at the next one of either kind.
                int runEnd = Math.Min(
                    lastCarryable, Math.Min(additions.NextElement(), removals.NextElement()) - 1);

                writeBucket = CopyUnchangedRun(target, from, ordinal, runEnd, writeBucket);
                ordinal = runEnd;
                continue;
            }

            int size = 0;

            if (addedByDelta)
            {
                (writeBucket, size) = CopyBuckets(target, delta, deltaOrdinal++, writeBucket);
                additions.Advance();
            }
            else if (ordinal <= from.MaxOrdinal && !removed)
            {
                (writeBucket, size) = CopyBuckets(target, from, ordinal, writeBucket);
            }

            if (removed)
            {
                removals.Advance();
            }

            target.WritePointerAndSize(ordinal, writeBucket, size);
        }

        return target;
    }

    /// <summary>
    /// Carries ordinals <paramref name="firstOrdinal"/> through <paramref name="lastOrdinal"/> across
    /// unchanged, returning where the next record's buckets start.
    /// </summary>
    /// <remarks>
    /// Both the buckets and the pointer-and-size entries move in one copy each. Only the pointer half
    /// of an entry is wrong afterwards, and only by the amount the run as a whole moved; the size half
    /// sits above it and is left alone, which holds because a pointer corrected into its own width
    /// cannot carry out of it.
    /// </remarks>
    private static long CopyUnchangedRun(
        HollowSetTypeDataElements target,
        HollowSetTypeDataElements from,
        int firstOrdinal,
        int lastOrdinal,
        long writeBucket)
    {
        int recordCount = lastOrdinal - firstOrdinal + 1;
        long sourceStart = from.GetStartBucket(firstOrdinal);
        long bucketCount = from.GetEndBucket(lastOrdinal) - sourceStart;

        target.ElementData!.CopyBits(
            from.ElementData!,
            sourceStart * from.BitsPerElement,
            writeBucket * target.BitsPerElement,
            bucketCount * target.BitsPerElement);

        long portionStartBit = (long)firstOrdinal * target.BitsPerFixedLengthSetPortion;

        target.SetPointerAndSizeData!.CopyBits(
            from.SetPointerAndSizeData!,
            portionStartBit,
            portionStartBit,
            (long)recordCount * target.BitsPerFixedLengthSetPortion);

        if (writeBucket != sourceStart)
        {
            // The pointer is the low field of each entry, so it is the one this lands on.
            target.SetPointerAndSizeData.IncrementMany(
                portionStartBit,
                writeBucket - sourceStart,
                target.BitsPerFixedLengthSetPortion,
                recordCount);
        }

        DeltaDiagnostics.BulkCopiedSets += recordCount;

        return writeBucket + bucketCount;
    }

    private static (long WriteBucket, int Size) CopyBuckets(
        HollowSetTypeDataElements target, HollowSetTypeDataElements source, int sourceOrdinal, long writeBucket) =>
        target.CopyBucketsFrom(writeBucket, source, sourceOrdinal);

    /// <summary>
    /// Copies one set's hash table into this shard at <paramref name="writeBucket"/>, returning where
    /// the next one starts and how many elements were in it.
    /// </summary>
    /// <remarks>
    /// The empty-bucket sentinel is all-ones at the element width, so an empty bucket has to be
    /// rewritten at this shard's width rather than copied.
    /// </remarks>
    internal (long WriteBucket, int Size) CopyBucketsFrom(
        long writeBucket, HollowSetTypeDataElements source, int sourceOrdinal)
    {
        long start = source.GetStartBucket(sourceOrdinal);
        long end = source.GetEndBucket(sourceOrdinal);

        for (long bucket = start; bucket < end; bucket++)
        {
            int value = source.GetBucketValue(bucket);
            long targetValue = value == source.EmptyBucketValue ? EmptyBucketValue : value;

            ElementData!.SetElementValue(writeBucket * BitsPerElement, BitsPerElement, targetValue);
            writeBucket++;
        }

        return (writeBucket, source.GetSize(sourceOrdinal));
    }
}
