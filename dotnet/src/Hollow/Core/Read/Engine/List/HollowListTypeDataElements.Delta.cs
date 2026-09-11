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

namespace Hollow.Core.Read.Engine.List;

/// <summary>
/// Delta support for a list type's record storage.
/// </summary>
/// <remarks>
/// <strong>Port note.</strong> As with the object type, only Java's record-at-a-time merge is ported,
/// not its bulk-copy fast path. See <c>PORTING.md</c>.
/// </remarks>
public sealed partial class HollowListTypeDataElements
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

        BitsPerListPointer = VarInt.ReadVInt(input);
        BitsPerElement = VarInt.ReadVInt(input);
        TotalNumberOfElements = VarInt.ReadVLong(input);

        ListPointerData = FixedLengthElementArray.NewFrom(input, MemoryRecycler);
        ElementData = FixedLengthElementArray.NewFrom(input, MemoryRecycler);
    }

    /// <summary>
    /// Builds the successor to <paramref name="from"/> by taking each ordinal's element run from
    /// <paramref name="delta"/> where the delta adds it, and from <paramref name="from"/> otherwise.
    /// </summary>
    internal static HollowListTypeDataElements ApplyDelta(
        HollowListTypeDataElements from, HollowListTypeDataElements delta)
    {
        HollowListTypeDataElements target = new(from.MemoryRecycler)
        {
            MaxOrdinal = delta.MaxOrdinal,
            EncodedRemovals = delta.EncodedRemovals,
            BitsPerListPointer = delta.BitsPerListPointer,
            BitsPerElement = delta.BitsPerElement,
            TotalNumberOfElements = delta.TotalNumberOfElements,
        };

        target.ListPointerData = new FixedLengthElementArray(
            from.MemoryRecycler, ((long)target.MaxOrdinal + 1) * target.BitsPerListPointer);
        target.ElementData = new FixedLengthElementArray(
            from.MemoryRecycler, target.TotalNumberOfElements * target.BitsPerElement);

        GapEncodedVariableLengthIntegerReader additions =
            delta.EncodedAdditions ?? GapEncodedVariableLengthIntegerReader.EmptyReader;
        additions.Reset();

        GapEncodedVariableLengthIntegerReader removals =
            from.EncodedRemovals ?? GapEncodedVariableLengthIntegerReader.EmptyReader;
        removals.Reset();

        long writeElement = 0;
        int deltaOrdinal = 0;

        for (int ordinal = 0; ordinal <= target.MaxOrdinal; ordinal++)
        {
            bool addedByDelta = additions.NextElement() == ordinal;
            bool removed = removals.NextElement() == ordinal;

            if (addedByDelta)
            {
                writeElement = CopyElements(target, delta, deltaOrdinal++, writeElement);
                additions.Advance();
            }
            else if (ordinal <= from.MaxOrdinal && !removed)
            {
                writeElement = CopyElements(target, from, ordinal, writeElement);
            }

            if (removed)
            {
                removals.Advance();
            }

            // Every ordinal records the end of its run, because the next one reads it as its start.
            target.ListPointerData.SetElementValue(
                (long)ordinal * target.BitsPerListPointer, target.BitsPerListPointer, writeElement);
        }

        return target;
    }

    private static long CopyElements(
        HollowListTypeDataElements target, HollowListTypeDataElements source, int sourceOrdinal, long writeElement) =>
        target.CopyElementsFrom(writeElement, source, sourceOrdinal);

    /// <summary>
    /// Copies one list's elements into this shard at <paramref name="writeElement"/>, returning the
    /// index one past the last element written.
    /// </summary>
    /// <remarks>
    /// Copied value by value rather than bit by bit, because the element width may differ between the
    /// two shards — which it does whenever they were sized for different record counts.
    /// </remarks>
    internal long CopyElementsFrom(long writeElement, HollowListTypeDataElements source, int sourceOrdinal)
    {
        long start = source.GetStartElement(sourceOrdinal);
        long end = source.GetEndElement(sourceOrdinal);

        for (long element = start; element < end; element++)
        {
            ElementData!.SetElementValue(
                writeElement * BitsPerElement, BitsPerElement, source.GetElementValue(element));
            writeElement++;
        }

        return writeElement;
    }
}
