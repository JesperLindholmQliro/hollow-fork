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
    /// <summary>The ordinals this state's successor removes, populated when a delta is read.</summary>
    public GapEncodedVariableLengthIntegerReader? EncodedRemovals { get; internal set; }

    /// <summary>The ordinals this delta adds.</summary>
    public GapEncodedVariableLengthIntegerReader? EncodedAdditions { get; internal set; }

    /// <summary>
    /// Reads one shard's delta records from <paramref name="input"/>.
    /// </summary>
    public void ReadDelta(HollowBlobInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        MaxOrdinal = VarInt.ReadVInt(input);

        EncodedRemovals = GapEncodedVariableLengthIntegerReader.ReadEncodedDeltaOrdinals(input, _memoryRecycler);
        EncodedAdditions = GapEncodedVariableLengthIntegerReader.ReadEncodedDeltaOrdinals(input, _memoryRecycler);

        BitsPerMapPointer = VarInt.ReadVInt(input);
        BitsPerMapSizeValue = VarInt.ReadVInt(input);
        BitsPerKeyElement = VarInt.ReadVInt(input);
        BitsPerValueElement = VarInt.ReadVInt(input);
        BitsPerFixedLengthMapPortion = BitsPerMapPointer + BitsPerMapSizeValue;
        BitsPerMapEntry = BitsPerKeyElement + BitsPerValueElement;
        EmptyBucketKeyValue = (1 << BitsPerKeyElement) - 1;
        TotalNumberOfBuckets = VarInt.ReadVLong(input);

        MapPointerAndSizeData = FixedLengthElementArray.NewFrom(input, _memoryRecycler);
        EntryData = FixedLengthElementArray.NewFrom(input, _memoryRecycler);
    }

    /// <summary>
    /// Builds the successor to <paramref name="from"/> by taking each ordinal's hash table from
    /// <paramref name="delta"/> where the delta adds it, and from <paramref name="from"/> otherwise.
    /// </summary>
    internal static HollowMapTypeDataElements ApplyDelta(
        HollowMapTypeDataElements from, HollowMapTypeDataElements delta)
    {
        HollowMapTypeDataElements target = new(from._memoryRecycler)
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
            from._memoryRecycler, ((long)target.MaxOrdinal + 1) * target.BitsPerFixedLengthMapPortion);
        target.EntryData = new FixedLengthElementArray(
            from._memoryRecycler, target.TotalNumberOfBuckets * target.BitsPerMapEntry);

        GapEncodedVariableLengthIntegerReader additions =
            delta.EncodedAdditions ?? GapEncodedVariableLengthIntegerReader.EmptyReader;
        additions.Reset();

        GapEncodedVariableLengthIntegerReader removals =
            from.EncodedRemovals ?? GapEncodedVariableLengthIntegerReader.EmptyReader;
        removals.Reset();

        long writeBucket = 0;
        int deltaOrdinal = 0;

        for (int ordinal = 0; ordinal <= target.MaxOrdinal; ordinal++)
        {
            bool addedByDelta = additions.NextElement() == ordinal;
            bool removed = removals.NextElement() == ordinal;

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

            long fixedLengthOffset = (long)ordinal * target.BitsPerFixedLengthMapPortion;
            target.MapPointerAndSizeData.SetElementValue(
                fixedLengthOffset, target.BitsPerMapPointer, writeBucket);
            target.MapPointerAndSizeData.SetElementValue(
                fixedLengthOffset + target.BitsPerMapPointer, target.BitsPerMapSizeValue, size);
        }

        return target;
    }

    private static (long WriteBucket, int Size) CopyBuckets(
        HollowMapTypeDataElements target, HollowMapTypeDataElements source, int sourceOrdinal, long writeBucket)
    {
        long start = source.GetStartBucket(sourceOrdinal);
        long end = source.GetEndBucket(sourceOrdinal);

        for (long bucket = start; bucket < end; bucket++)
        {
            int key = source.GetBucketKeyValue(bucket);
            bool isEmpty = key == source.EmptyBucketKeyValue;

            long entryBitOffset = writeBucket * target.BitsPerMapEntry;

            // The empty-bucket sentinel is width-dependent, so an empty bucket is rewritten at the
            // target's width rather than copied.
            target.EntryData!.SetElementValue(
                entryBitOffset, target.BitsPerKeyElement, isEmpty ? target.EmptyBucketKeyValue : key);

            if (!isEmpty)
            {
                target.EntryData.SetElementValue(
                    entryBitOffset + target.BitsPerKeyElement,
                    target.BitsPerValueElement,
                    source.GetBucketValueValue(bucket));
            }

            writeBucket++;
        }

        return (writeBucket, source.GetSize(sourceOrdinal));
    }
}
