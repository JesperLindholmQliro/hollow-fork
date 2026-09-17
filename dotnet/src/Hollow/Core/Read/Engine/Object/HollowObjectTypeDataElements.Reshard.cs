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
using Hollow.Core.Memory;
using Hollow.Core.Memory.Encoding;
using Hollow.Core.Schema;

namespace Hollow.Core.Read.Engine.Object;

public sealed partial class HollowObjectTypeDataElements
{
    /// <summary>
    /// The number of bytes a variable-length field occupies for one record.
    /// </summary>
    internal long VarLengthSize(int ordinal, int fieldIndex)
    {
        (long start, long end, _) = GetVarLengthRange(ordinal, fieldIndex);

        return end - start;
    }

    /// <summary>
    /// Copies one record from <paramref name="from"/> into this shard's storage.
    /// </summary>
    /// <remarks>
    /// The two shards hold the same schema but need not share field widths — splitting narrows a
    /// variable-length field's pointer to fit the smaller shard, joining widens it — so every field is
    /// re-encoded at this shard's width rather than copied bit for bit.
    /// </remarks>
    internal void CopyRecord(
        int toOrdinal, HollowObjectTypeDataElements from, int fromOrdinal, long[] varLengthWritePointers)
    {
        if (LayoutMatches(from))
        {
            RecordCopyDiagnostics.BulkResharded++;

            // Same widths in the same order, so the record's fixed-length half is the same bits here as
            // it was there. Only its var-length pointers are wrong, and the loop below rewrites those.
            FixedLengthData!.CopyBits(
                from.FixedLengthData!,
                (long)from.BitsPerRecord * fromOrdinal,
                (long)BitsPerRecord * toOrdinal,
                BitsPerRecord);

            for (int fieldIndex = 0; fieldIndex < Schema.FieldCount; fieldIndex++)
            {
                if (!Schema.GetFieldType(fieldIndex).IsVariableLength())
                {
                    continue;
                }

                long fieldBitOffset = ((long)BitsPerRecord * toOrdinal) + BitOffsetPerField[fieldIndex];

                // SetElementValue ORs into what is already there, because the storage it is designed
                // for starts out zeroed. These bits do not: the copy above just filled them with the
                // pointer this record had in the shard it came from.
                FixedLengthData.ClearElementValue(fieldBitOffset, BitsPerField[fieldIndex]);

                CopyVarLengthField(from, fromOrdinal, fieldIndex, fieldBitOffset, varLengthWritePointers);
            }

            return;
        }

        RecordCopyDiagnostics.ReencodedByReshard++;

        for (int fieldIndex = 0; fieldIndex < Schema.FieldCount; fieldIndex++)
        {
            long fieldBitOffset = ((long)BitsPerRecord * toOrdinal) + BitOffsetPerField[fieldIndex];

            if (Schema.GetFieldType(fieldIndex).IsVariableLength())
            {
                CopyVarLengthField(from, fromOrdinal, fieldIndex, fieldBitOffset, varLengthWritePointers);
            }
            else
            {
                long value = from.GetFixedFieldValue(fromOrdinal, fieldIndex);

                if (value == from.NullValueForField[fieldIndex])
                {
                    value = NullValueForField[fieldIndex];
                }

                FixedLengthData!.SetElementValue(fieldBitOffset, BitsPerField[fieldIndex], value);
            }
        }
    }

    /// <summary>
    /// Writes a record whose every field is null, for an ordinal no source shard holds.
    /// </summary>
    /// <remarks>
    /// Joining lopsided shards produces these: if one shard reaches a higher ordinal than another, the
    /// joined shard has slots in between that nothing came from.
    /// </remarks>
    internal void WriteNullRecord(int toOrdinal, long[] varLengthWritePointers)
    {
        for (int fieldIndex = 0; fieldIndex < Schema.FieldCount; fieldIndex++)
        {
            long fieldBitOffset = ((long)BitsPerRecord * toOrdinal) + BitOffsetPerField[fieldIndex];

            if (Schema.GetFieldType(fieldIndex).IsVariableLength())
            {
                int numBits = BitsPerField[fieldIndex];
                FixedLengthData!.SetElementValue(
                    fieldBitOffset, numBits, varLengthWritePointers[fieldIndex] | (1L << (numBits - 1)));
            }
            else
            {
                FixedLengthData!.SetElementValue(
                    fieldBitOffset, BitsPerField[fieldIndex], NullValueForField[fieldIndex]);
            }
        }
    }

    /// <summary>
    /// Whether a record occupies the same bits here as it does in <paramref name="from"/>.
    /// </summary>
    /// <remarks>
    /// Splitting usually narrows the var-length fields and joining widens them, since their width
    /// follows the bytes a shard holds — so this is mostly true of types with no var-length field at
    /// all, and of a shard being moved rather than resized.
    /// </remarks>
    private bool LayoutMatches(HollowObjectTypeDataElements from)
    {
        if (BitsPerRecord != from.BitsPerRecord)
        {
            return false;
        }

        for (int fieldIndex = 0; fieldIndex < Schema.FieldCount; fieldIndex++)
        {
            if (BitsPerField[fieldIndex] != from.BitsPerField[fieldIndex])
            {
                return false;
            }
        }

        return true;
    }

    private void CopyVarLengthField(
        HollowObjectTypeDataElements from,
        int fromOrdinal,
        int fieldIndex,
        long fieldBitOffset,
        long[] varLengthWritePointers)
    {
        (long start, long end, bool isNull) = from.GetVarLengthRange(fromOrdinal, fieldIndex);

        if (!isNull)
        {
            IVariableLengthData source = from.VarLengthData[fieldIndex]!;
            SegmentedByteArray target = (SegmentedByteArray)VarLengthData[fieldIndex]!;

            target.Copy(source, start, varLengthWritePointers[fieldIndex], end - start);

            varLengthWritePointers[fieldIndex] += end - start;
        }

        int numBits = BitsPerField[fieldIndex];
        long offset = varLengthWritePointers[fieldIndex];

        FixedLengthData!.SetElementValue(fieldBitOffset, numBits, isNull ? offset | (1L << (numBits - 1)) : offset);
    }

    /// <summary>
    /// Sizes this shard's fields for records totalling <paramref name="varLengthSizes"/> bytes per
    /// variable-length field, and allocates its storage.
    /// </summary>
    /// <remarks>
    /// A variable-length field stores a byte offset plus a null bit, so its width follows the total
    /// bytes the shard holds. Splitting a shard therefore usually narrows those fields and joining
    /// widens them, which is why the records have to be re-encoded rather than copied.
    /// </remarks>
    /// <param name="varLengthSizes">The total bytes each variable-length field will hold.</param>
    /// <param name="fixedFieldWidths">
    /// The width to give each fixed-length field. For a split that is the source's width; for a join it
    /// is the widest of the sources', since they need not agree.
    /// </param>
    internal void AllocateForSizes(long[] varLengthSizes, int[] fixedFieldWidths)
    {
        BitsPerRecord = 0;

        for (int fieldIndex = 0; fieldIndex < Schema.FieldCount; fieldIndex++)
        {
            BitsPerField[fieldIndex] = Schema.GetFieldType(fieldIndex).IsVariableLength()
                ? (64 - BitOperations.LeadingZeroCount((ulong)varLengthSizes[fieldIndex] + 1)) + 1
                : fixedFieldWidths[fieldIndex];

            NullValueForField[fieldIndex] =
                BitsPerField[fieldIndex] >= 64 ? -1L : (1L << BitsPerField[fieldIndex]) - 1;
            BitOffsetPerField[fieldIndex] = BitsPerRecord;
            BitsPerRecord += BitsPerField[fieldIndex];
        }

        FixedLengthData = new FixedLengthElementArray(MemoryRecycler, (long)BitsPerRecord * (MaxOrdinal + 1));

        for (int fieldIndex = 0; fieldIndex < Schema.FieldCount; fieldIndex++)
        {
            if (Schema.GetFieldType(fieldIndex).IsVariableLength())
            {
                VarLengthData[fieldIndex] = new SegmentedByteArray(MemoryRecycler);
            }
        }
    }
}
