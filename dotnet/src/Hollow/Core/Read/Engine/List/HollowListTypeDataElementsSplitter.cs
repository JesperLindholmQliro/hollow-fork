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

namespace Hollow.Core.Read.Engine.List;

/// <summary>
/// Divides one shard of a list type into several.
/// </summary>
public sealed class HollowListTypeDataElementsSplitter(HollowListTypeDataElements from, int numSplits)
    : HollowTypeDataElementsSplitter<HollowListTypeDataElements>(from, numSplits)
{
    /// <inheritdoc />
    protected override HollowListTypeDataElements[] CreateSplits() =>
        [.. Enumerable.Range(0, NumSplits).Select(_ => new HollowListTypeDataElements(From.MemoryRecycler))];

    /// <inheritdoc />
    protected override void PopulateStats()
    {
        long[] totalOfListSizes = new long[NumSplits];

        for (int ordinal = 0; ordinal <= From.MaxOrdinal; ordinal++)
        {
            int toIndex = ordinal & ToMask;
            To[toIndex].MaxOrdinal = ordinal >> ToOrdinalShift;

            totalOfListSizes[toIndex] += From.GetEndElement(ordinal) - From.GetStartElement(ordinal);
        }

        // Every split is given a pointer wide enough for the largest of them, so that a record can be
        // moved between splits later without re-encoding its pointer.
        long maxTotalOfListSizes = totalOfListSizes.Length == 0 ? 0 : totalOfListSizes.Max();

        foreach ((HollowListTypeDataElements target, long total) in To.Zip(totalOfListSizes))
        {
            target.BitsPerElement = From.BitsPerElement;
            target.BitsPerListPointer =
                maxTotalOfListSizes == 0 ? 1 : 64 - BitOperations.LeadingZeroCount((ulong)maxTotalOfListSizes);
            target.TotalNumberOfElements = total;
        }
    }

    /// <inheritdoc />
    protected override void CopyRecords()
    {
        long[] elementCounter = new long[NumSplits];

        foreach (HollowListTypeDataElements target in To)
        {
            target.ListPointerData = new FixedLengthElementArray(
                target.MemoryRecycler, (long)target.BitsPerListPointer * (target.MaxOrdinal + 1));
            target.ElementData = new FixedLengthElementArray(
                target.MemoryRecycler, target.BitsPerElement * target.TotalNumberOfElements);
        }

        for (int ordinal = 0; ordinal <= From.MaxOrdinal; ordinal++)
        {
            int toIndex = ordinal & ToMask;
            HollowListTypeDataElements target = To[toIndex];

            elementCounter[toIndex] = target.CopyElementsFrom(elementCounter[toIndex], From, ordinal);

            target.ListPointerData!.SetElementValue(
                (long)target.BitsPerListPointer * (ordinal >> ToOrdinalShift),
                target.BitsPerListPointer,
                elementCounter[toIndex]);
        }
    }
}

/// <summary>
/// Merges several shards of a list type into one.
/// </summary>
public sealed class HollowListTypeDataElementsJoiner(HollowListTypeDataElements[] from)
    : HollowTypeDataElementsJoiner<HollowListTypeDataElements>(from)
{
    /// <inheritdoc />
    protected override HollowListTypeDataElements CreateJoined() => new(From[0].MemoryRecycler);

    /// <inheritdoc />
    protected override void PopulateStats()
    {
        To.MaxOrdinal = JoinedMaxOrdinal();
        To.TotalNumberOfElements = From.Sum(source => source.TotalNumberOfElements);

        // An element ordinal's width need not agree across the sources, so the join takes the widest.
        To.BitsPerElement = From.Max(source => source.BitsPerElement);
        To.BitsPerListPointer = To.TotalNumberOfElements == 0
            ? 1
            : 64 - BitOperations.LeadingZeroCount((ulong)To.TotalNumberOfElements);
    }

    /// <inheritdoc />
    protected override void CopyRecords()
    {
        To.ListPointerData = new FixedLengthElementArray(
            To.MemoryRecycler, (long)To.BitsPerListPointer * (To.MaxOrdinal + 1));
        To.ElementData = new FixedLengthElementArray(
            To.MemoryRecycler, To.BitsPerElement * To.TotalNumberOfElements);

        long elementCounter = 0;

        for (int ordinal = 0; ordinal <= To.MaxOrdinal; ordinal++)
        {
            HollowListTypeDataElements source = From[ordinal & FromMask];
            int fromOrdinal = ordinal >> FromOrdinalShift;

            // A slot past a lopsided source's last ordinal holds an empty list, which needs no elements
            // — only the pointer below, repeating the previous one.
            if (fromOrdinal <= source.MaxOrdinal)
            {
                elementCounter = To.CopyElementsFrom(elementCounter, source, fromOrdinal);
            }

            To.ListPointerData.SetElementValue(
                (long)To.BitsPerListPointer * ordinal, To.BitsPerListPointer, elementCounter);
        }
    }
}
