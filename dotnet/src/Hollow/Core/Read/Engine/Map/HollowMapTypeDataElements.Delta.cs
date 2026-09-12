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

namespace Hollow.Core.Read.Engine.Map;

/// <summary>
/// Delta support for a map type's record storage.
/// </summary>
public sealed partial class HollowMapTypeDataElements
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

        BitsPerMapPointer = VarInt.ReadVInt(input);
        BitsPerMapSizeValue = VarInt.ReadVInt(input);
        BitsPerKeyElement = VarInt.ReadVInt(input);
        BitsPerValueElement = VarInt.ReadVInt(input);
        BitsPerFixedLengthMapPortion = BitsPerMapPointer + BitsPerMapSizeValue;
        BitsPerMapEntry = BitsPerKeyElement + BitsPerValueElement;
        EmptyBucketKeyValue = (1 << BitsPerKeyElement) - 1;
        TotalNumberOfBuckets = VarInt.ReadVLong(input);

        MapPointerAndSizeData = FixedLengthElementArray.NewFrom(input, MemoryRecycler);
        EntryData = FixedLengthElementArray.NewFrom(input, MemoryRecycler);
    }

    /// <summary>
    /// Builds the successor to <paramref name="from"/> by taking each ordinal's hash table from
    /// <paramref name="delta"/> where the delta adds it, and from <paramref name="from"/> otherwise.
    /// </summary>
    internal static HollowMapTypeDataElements ApplyDelta(
        HollowMapTypeDataElements from, HollowMapTypeDataElements delta)
    {
        HollowMapTypeDataElements target = new(from.MemoryRecycler)
        {
            MaxOrdinal = delta.MaxOrdinal,
            EncodedRemovals = delta.EncodedRemovals,
            BitsPerMapPointer = delta.BitsPerMapPointer,
            BitsPerMapSizeValue = delta.BitsPerMapSizeValue,
            BitsPerKeyElement = delta.BitsPerKeyElement,
            BitsPerValueElement = delta.BitsPerValueElement,
            BitsPerFixedLengthMapPortion = delta.BitsPerMapPointer + delta.BitsPerMapSizeValue,
            BitsPerMapEntry = delta.BitsPerKeyElement + delta.BitsPerValueElement,
            EmptyBucketKeyValue = (1 << delta.BitsPerKeyElement) - 1,
            TotalNumberOfBuckets = delta.TotalNumberOfBuckets,
        };

        target.MapPointerAndSizeData = new FixedLengthElementArray(
            from.MemoryRecycler, ((long)target.MaxOrdinal + 1) * target.BitsPerFixedLengthMapPortion);
        target.EntryData = new FixedLengthElementArray(
            from.MemoryRecycler, target.TotalNumberOfBuckets * target.BitsPerMapEntry);

        GapEncodedVariableLengthIntegerReader additions =
            delta.EncodedAdditions ?? GapEncodedVariableLengthIntegerReader.EmptyReader;
        additions.Reset();

        GapEncodedVariableLengthIntegerReader removals =
            from.EncodedRemovals ?? GapEncodedVariableLengthIntegerReader.EmptyReader;
        removals.Reset();

        long writeBucket = 0;
        int deltaOrdinal = 0;

        // The empty-bucket sentinel is all-ones at the key width, and an entry packs a key against a
        // value, so buckets can only be copied when neither width moved. See CopyUnchangedRun.
        bool widthsUnchanged = target.BitsPerKeyElement == from.BitsPerKeyElement
            && target.BitsPerValueElement == from.BitsPerValueElement
            && target.BitsPerFixedLengthMapPortion == from.BitsPerFixedLengthMapPortion
            && target.BitsPerMapPointer == from.BitsPerMapPointer;

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
    /// <para>
    /// Both the entries and the pointer-and-size portions move in one copy each, and only the pointer
    /// half of a portion needs correcting afterwards — by the amount the run as a whole moved.
    /// </para>
    /// <para>
    /// The record-at-a-time path leaves an empty bucket's value bits zero where this carries whatever
    /// the previous state held there. Nothing reads them: a lookup stops at the sentinel key, and so
    /// does the checksum. Were that not so, the two paths would not agree.
    /// </para>
    /// </remarks>
    private static long CopyUnchangedRun(
        HollowMapTypeDataElements target,
        HollowMapTypeDataElements from,
        int firstOrdinal,
        int lastOrdinal,
        long writeBucket)
    {
        int recordCount = lastOrdinal - firstOrdinal + 1;
        long sourceStart = from.GetStartBucket(firstOrdinal);
        long bucketCount = from.GetEndBucket(lastOrdinal) - sourceStart;

        target.EntryData!.CopyBits(
            from.EntryData!,
            sourceStart * from.BitsPerMapEntry,
            writeBucket * target.BitsPerMapEntry,
            bucketCount * target.BitsPerMapEntry);

        long portionStartBit = (long)firstOrdinal * target.BitsPerFixedLengthMapPortion;

        target.MapPointerAndSizeData!.CopyBits(
            from.MapPointerAndSizeData!,
            portionStartBit,
            portionStartBit,
            (long)recordCount * target.BitsPerFixedLengthMapPortion);

        if (writeBucket != sourceStart)
        {
            // The pointer is the low field of each portion, so it is the one this lands on.
            target.MapPointerAndSizeData.IncrementMany(
                portionStartBit,
                writeBucket - sourceStart,
                target.BitsPerFixedLengthMapPortion,
                recordCount);
        }

        RecordCopyDiagnostics.BulkCopiedMaps += recordCount;

        return writeBucket + bucketCount;
    }

    private static (long WriteBucket, int Size) CopyBuckets(
        HollowMapTypeDataElements target, HollowMapTypeDataElements source, int sourceOrdinal, long writeBucket) =>
        target.CopyBucketsFrom(writeBucket, source, sourceOrdinal);

    /// <summary>
    /// Copies one map's hash table into this shard at <paramref name="writeBucket"/>, returning where
    /// the next one starts and how many entries were in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The empty-bucket sentinel is all-ones at the key width, and an entry packs a key against a
    /// value, so where the two shards agree on both widths the whole table is one copy and where they
    /// do not every bucket has to be rewritten. The delta merge, the resharding splitter and the joiner
    /// all come through here.
    /// </para>
    /// <para>
    /// Copying carries an empty bucket's value bits where rewriting leaves them zero. Nothing reads
    /// them: a lookup stops at the sentinel key, and so does the checksum.
    /// </para>
    /// </remarks>
    internal (long WriteBucket, int Size) CopyBucketsFrom(
        long writeBucket, HollowMapTypeDataElements source, int sourceOrdinal)
    {
        long start = source.GetStartBucket(sourceOrdinal);
        long end = source.GetEndBucket(sourceOrdinal);

        if (BitsPerKeyElement == source.BitsPerKeyElement
            && BitsPerValueElement == source.BitsPerValueElement)
        {
            EntryData!.CopyBits(
                source.EntryData!,
                start * BitsPerMapEntry,
                writeBucket * BitsPerMapEntry,
                (end - start) * BitsPerMapEntry);

            return (writeBucket + (end - start), source.GetSize(sourceOrdinal));
        }

        for (long bucket = start; bucket < end; bucket++)
        {
            int key = source.GetBucketKeyValue(bucket);
            bool isEmpty = key == source.EmptyBucketKeyValue;

            long entryBitOffset = writeBucket * BitsPerMapEntry;

            EntryData!.SetElementValue(
                entryBitOffset, BitsPerKeyElement, isEmpty ? EmptyBucketKeyValue : key);

            if (!isEmpty)
            {
                EntryData.SetElementValue(
                    entryBitOffset + BitsPerKeyElement,
                    BitsPerValueElement,
                    source.GetBucketValueValue(bucket));
            }

            writeBucket++;
        }

        return (writeBucket, source.GetSize(sourceOrdinal));
    }
}
