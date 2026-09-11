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
using Hollow.Core.Memory.Encoding;

namespace Hollow.Core.Read.Engine.Set;

/// <summary>
/// Divides one shard of a set type into several.
/// </summary>
public sealed class HollowSetTypeDataElementsSplitter(HollowSetTypeDataElements from, int numSplits)
    : HollowTypeDataElementsSplitter<HollowSetTypeDataElements>(from, numSplits)
{
    /// <inheritdoc />
    protected override HollowSetTypeDataElements[] CreateSplits() =>
        [.. Enumerable.Range(0, NumSplits).Select(_ => new HollowSetTypeDataElements(From.MemoryRecycler))];

    /// <inheritdoc />
    protected override void PopulateStats()
    {
        long[] totalBuckets = new long[NumSplits];
        int maxSize = 0;

        for (int ordinal = 0; ordinal <= From.MaxOrdinal; ordinal++)
        {
            int toIndex = ordinal & ToMask;
            To[toIndex].MaxOrdinal = ordinal >> ToOrdinalShift;

            totalBuckets[toIndex] += From.GetEndBucket(ordinal) - From.GetStartBucket(ordinal);
            maxSize = Math.Max(maxSize, From.GetSize(ordinal));
        }

        // Every split is given a pointer wide enough for the largest of them.
        long maxTotalBuckets = totalBuckets.Length == 0 ? 0 : totalBuckets.Max();

        foreach ((HollowSetTypeDataElements target, long buckets) in To.Zip(totalBuckets))
        {
            target.BitsPerElement = From.BitsPerElement;
            target.EmptyBucketValue = From.EmptyBucketValue;
            target.BitsPerSetPointer =
                maxTotalBuckets == 0 ? 1 : 64 - BitOperations.LeadingZeroCount((ulong)maxTotalBuckets);
            target.BitsPerSetSizeValue =
                maxSize == 0 ? 1 : 32 - BitOperations.LeadingZeroCount((uint)maxSize);
            target.BitsPerFixedLengthSetPortion = target.BitsPerSetPointer + target.BitsPerSetSizeValue;
            target.TotalNumberOfBuckets = buckets;
        }
    }

    /// <inheritdoc />
    protected override void CopyRecords()
    {
        long[] bucketCounter = new long[NumSplits];

        foreach (HollowSetTypeDataElements target in To)
        {
            target.SetPointerAndSizeData = new FixedLengthElementArray(
                target.MemoryRecycler, (long)target.BitsPerFixedLengthSetPortion * (target.MaxOrdinal + 1));
            target.ElementData = new FixedLengthElementArray(
                target.MemoryRecycler, target.BitsPerElement * target.TotalNumberOfBuckets);
        }

        for (int ordinal = 0; ordinal <= From.MaxOrdinal; ordinal++)
        {
            int toIndex = ordinal & ToMask;
            HollowSetTypeDataElements target = To[toIndex];

            (bucketCounter[toIndex], int size) = target.CopyBucketsFrom(bucketCounter[toIndex], From, ordinal);

            target.WritePointerAndSize(ordinal >> ToOrdinalShift, bucketCounter[toIndex], size);
        }
    }
}

/// <summary>
/// Merges several shards of a set type into one.
/// </summary>
public sealed class HollowSetTypeDataElementsJoiner(HollowSetTypeDataElements[] from)
    : HollowTypeDataElementsJoiner<HollowSetTypeDataElements>(from)
{
    /// <inheritdoc />
    protected override HollowSetTypeDataElements CreateJoined() => new(From[0].MemoryRecycler);

    /// <inheritdoc />
    protected override void PopulateStats()
    {
        To.MaxOrdinal = JoinedMaxOrdinal();
        To.TotalNumberOfBuckets = From.Sum(source => source.TotalNumberOfBuckets);

        // The element width need not agree across the sources, so the join takes the widest — and with
        // it the matching empty-bucket sentinel.
        To.BitsPerElement = From.Max(source => source.BitsPerElement);
        To.EmptyBucketValue = (1 << To.BitsPerElement) - 1;

        To.BitsPerSetPointer = To.TotalNumberOfBuckets == 0
            ? 1
            : 64 - BitOperations.LeadingZeroCount((ulong)To.TotalNumberOfBuckets);
        To.BitsPerSetSizeValue = From.Max(source => source.BitsPerSetSizeValue);
        To.BitsPerFixedLengthSetPortion = To.BitsPerSetPointer + To.BitsPerSetSizeValue;
    }

    /// <inheritdoc />
    protected override void CopyRecords()
    {
        To.SetPointerAndSizeData = new FixedLengthElementArray(
            To.MemoryRecycler, (long)To.BitsPerFixedLengthSetPortion * (To.MaxOrdinal + 1));
        To.ElementData = new FixedLengthElementArray(
            To.MemoryRecycler, To.BitsPerElement * To.TotalNumberOfBuckets);

        long bucketCounter = 0;

        for (int ordinal = 0; ordinal <= To.MaxOrdinal; ordinal++)
        {
            HollowSetTypeDataElements source = From[ordinal & FromMask];
            int fromOrdinal = ordinal >> FromOrdinalShift;

            int size = 0;

            // A slot past a lopsided source's last ordinal holds an empty set, which occupies no
            // buckets — only the pointer below, repeating the previous one.
            if (fromOrdinal <= source.MaxOrdinal)
            {
                (bucketCounter, size) = To.CopyBucketsFrom(bucketCounter, source, fromOrdinal);
            }

            To.WritePointerAndSize(ordinal, bucketCounter, size);
        }
    }
}
