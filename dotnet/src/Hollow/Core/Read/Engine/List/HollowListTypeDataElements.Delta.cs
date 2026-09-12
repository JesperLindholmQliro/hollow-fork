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
/// Two paths, as with the object type: a run of records the delta neither adds nor removes is copied
/// wholesale when the widths allow it, and anything else is merged record by record.
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

        // Unchanged records keep their ordinals, so when neither width moved their pointers and
        // elements can be copied and shifted rather than rebuilt. See CopyUnchangedRun.
        bool widthsUnchanged = target.BitsPerElement == from.BitsPerElement
            && target.BitsPerListPointer == from.BitsPerListPointer;

        int lastCarryable = Math.Min(from.MaxOrdinal, target.MaxOrdinal);

        for (int ordinal = 0; ordinal <= target.MaxOrdinal; ordinal++)
        {
            bool addedByDelta = additions.NextElement() == ordinal;
            bool removed = removals.NextElement() == ordinal;

            if (widthsUnchanged && !addedByDelta && !removed && ordinal <= lastCarryable)
            {
                // A removal inside the run would drop its elements and shift everything after it by a
                // different amount, so the run stops at the next one of either kind.
                int runEnd = Math.Min(
                    lastCarryable, Math.Min(additions.NextElement(), removals.NextElement()) - 1);

                writeElement = CopyUnchangedRun(target, from, ordinal, runEnd, writeElement);
                ordinal = runEnd;
                continue;
            }

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

    /// <summary>
    /// Carries ordinals <paramref name="firstOrdinal"/> through <paramref name="lastOrdinal"/> across
    /// unchanged, returning where the next record's elements start.
    /// </summary>
    /// <remarks>
    /// The elements of a run with no removals in it are contiguous on both sides, so they move in one
    /// copy. Their pointers move too, all by the same amount, because everything the run displaced lies
    /// before it — which is what makes one strided pass enough to correct them.
    /// </remarks>
    private static long CopyUnchangedRun(
        HollowListTypeDataElements target,
        HollowListTypeDataElements from,
        int firstOrdinal,
        int lastOrdinal,
        long writeElement)
    {
        int recordCount = lastOrdinal - firstOrdinal + 1;
        long sourceStart = from.GetStartElement(firstOrdinal);
        long elementCount = from.GetEndElement(lastOrdinal) - sourceStart;

        target.ElementData!.CopyBits(
            from.ElementData!,
            sourceStart * from.BitsPerElement,
            writeElement * target.BitsPerElement,
            elementCount * target.BitsPerElement);

        long pointerStartBit = (long)firstOrdinal * target.BitsPerListPointer;

        target.ListPointerData!.CopyBits(
            from.ListPointerData!,
            pointerStartBit,
            pointerStartBit,
            (long)recordCount * target.BitsPerListPointer);

        if (writeElement != sourceStart)
        {
            target.ListPointerData.IncrementMany(
                pointerStartBit, writeElement - sourceStart, target.BitsPerListPointer, recordCount);
        }

        RecordCopyDiagnostics.BulkCopiedLists += recordCount;

        return writeElement + elementCount;
    }

    private static long CopyElements(
        HollowListTypeDataElements target, HollowListTypeDataElements source, int sourceOrdinal, long writeElement) =>
        target.CopyElementsFrom(writeElement, source, sourceOrdinal);

    /// <summary>
    /// Copies one list's elements into this shard at <paramref name="writeElement"/>, returning the
    /// index one past the last element written.
    /// </summary>
    /// <remarks>
    /// Bit for bit where the two shards store an element at the same width, and value by value where
    /// they do not — which happens whenever they were sized for different record counts. The delta
    /// merge, the resharding splitter and the joiner all come through here.
    /// </remarks>
    internal long CopyElementsFrom(long writeElement, HollowListTypeDataElements source, int sourceOrdinal)
    {
        long start = source.GetStartElement(sourceOrdinal);
        long end = source.GetEndElement(sourceOrdinal);

        if (BitsPerElement == source.BitsPerElement)
        {
            ElementData!.CopyBits(
                source.ElementData!,
                start * BitsPerElement,
                writeElement * BitsPerElement,
                (end - start) * BitsPerElement);

            return writeElement + (end - start);
        }

        for (long element = start; element < end; element++)
        {
            ElementData!.SetElementValue(
                writeElement * BitsPerElement, BitsPerElement, source.GetElementValue(element));
            writeElement++;
        }

        return writeElement;
    }
}
