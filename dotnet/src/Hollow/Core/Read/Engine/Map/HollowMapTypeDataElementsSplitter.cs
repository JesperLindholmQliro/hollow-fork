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

namespace Hollow.Core.Read.Engine.Map;

/// <summary>
/// Divides one shard of a map type into several.
/// </summary>
public sealed class HollowMapTypeDataElementsSplitter(HollowMapTypeDataElements from, int numSplits)
    : HollowTypeDataElementsSplitter<HollowMapTypeDataElements>(from, numSplits)
{
    /// <inheritdoc />
    protected override HollowMapTypeDataElements[] CreateSplits() =>
        [.. Enumerable.Range(0, NumSplits).Select(_ => new HollowMapTypeDataElements(From.MemoryRecycler))];

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

        foreach ((HollowMapTypeDataElements target, long buckets) in To.Zip(totalBuckets))
        {
            target.BitsPerKeyElement = From.BitsPerKeyElement;
            target.BitsPerValueElement = From.BitsPerValueElement;
            target.BitsPerMapEntry = From.BitsPerMapEntry;
            target.EmptyBucketKeyValue = From.EmptyBucketKeyValue;
            target.BitsPerMapPointer =
                maxTotalBuckets == 0 ? 1 : 64 - BitOperations.LeadingZeroCount((ulong)maxTotalBuckets);
            target.BitsPerMapSizeValue =
                maxSize == 0 ? 1 : 32 - BitOperations.LeadingZeroCount((uint)maxSize);
            target.BitsPerFixedLengthMapPortion = target.BitsPerMapPointer + target.BitsPerMapSizeValue;
            target.TotalNumberOfBuckets = buckets;
        }
    }

    /// <inheritdoc />
    protected override void CopyRecords()
    {
        long[] bucketCounter = new long[NumSplits];

        foreach (HollowMapTypeDataElements target in To)
        {
            target.MapPointerAndSizeData = new FixedLengthElementArray(
                target.MemoryRecycler, (long)target.BitsPerFixedLengthMapPortion * (target.MaxOrdinal + 1));
            target.EntryData = new FixedLengthElementArray(
                target.MemoryRecycler, target.BitsPerMapEntry * target.TotalNumberOfBuckets);
        }

        for (int ordinal = 0; ordinal <= From.MaxOrdinal; ordinal++)
        {
            int toIndex = ordinal & ToMask;
            HollowMapTypeDataElements target = To[toIndex];

            (bucketCounter[toIndex], int size) = target.CopyBucketsFrom(bucketCounter[toIndex], From, ordinal);

            target.WritePointerAndSize(ordinal >> ToOrdinalShift, bucketCounter[toIndex], size);
        }
    }
}

/// <summary>
/// Merges several shards of a map type into one.
/// </summary>
public sealed class HollowMapTypeDataElementsJoiner(HollowMapTypeDataElements[] from)
    : HollowTypeDataElementsJoiner<HollowMapTypeDataElements>(from)
{
    /// <inheritdoc />
    protected override HollowMapTypeDataElements CreateJoined() => new(From[0].MemoryRecycler);

    /// <inheritdoc />
    protected override void PopulateStats()
    {
        To.MaxOrdinal = JoinedMaxOrdinal();
        To.TotalNumberOfBuckets = From.Sum(source => source.TotalNumberOfBuckets);

        // Neither ordinal width need agree across the sources, so the join takes the widest of each —
        // and with the key width, the matching empty-bucket sentinel.
        To.BitsPerKeyElement = From.Max(source => source.BitsPerKeyElement);
        To.BitsPerValueElement = From.Max(source => source.BitsPerValueElement);
        To.BitsPerMapEntry = To.BitsPerKeyElement + To.BitsPerValueElement;
        To.EmptyBucketKeyValue = (1 << To.BitsPerKeyElement) - 1;

        To.BitsPerMapPointer = To.TotalNumberOfBuckets == 0
            ? 1
            : 64 - BitOperations.LeadingZeroCount((ulong)To.TotalNumberOfBuckets);
        To.BitsPerMapSizeValue = From.Max(source => source.BitsPerMapSizeValue);
        To.BitsPerFixedLengthMapPortion = To.BitsPerMapPointer + To.BitsPerMapSizeValue;
    }

    /// <inheritdoc />
    protected override void CopyRecords()
    {
        To.MapPointerAndSizeData = new FixedLengthElementArray(
            To.MemoryRecycler, (long)To.BitsPerFixedLengthMapPortion * (To.MaxOrdinal + 1));
        To.EntryData = new FixedLengthElementArray(
            To.MemoryRecycler, To.BitsPerMapEntry * To.TotalNumberOfBuckets);

        long bucketCounter = 0;

        for (int ordinal = 0; ordinal <= To.MaxOrdinal; ordinal++)
        {
            HollowMapTypeDataElements source = From[ordinal & FromMask];
            int fromOrdinal = ordinal >> FromOrdinalShift;

            int size = 0;

            // A slot past a lopsided source's last ordinal holds an empty map, which occupies no
            // buckets — only the pointer below, repeating the previous one.
            if (fromOrdinal <= source.MaxOrdinal)
            {
                (bucketCounter, size) = To.CopyBucketsFrom(bucketCounter, source, fromOrdinal);
            }

            To.WritePointerAndSize(ordinal, bucketCounter, size);
        }
    }
}
