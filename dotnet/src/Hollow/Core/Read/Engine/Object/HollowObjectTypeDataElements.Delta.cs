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

using Hollow.Core.Memory;
using Hollow.Core.Memory.Encoding;
using Hollow.Core.Schema;

namespace Hollow.Core.Read.Engine.Object;

/// <summary>
/// Delta support for an object type's record storage.
/// </summary>
/// <remarks>
/// Two paths, as in Java's <c>HollowObjectDeltaApplicator</c>: a run of records the delta leaves alone
/// is copied wholesale when the record layout is unchanged, and anything else is re-encoded field by
/// field. The two produce identical output, which is what <c>DeltaTests</c> asserts by comparing an
/// applied delta against a snapshot of the same cycle.
/// </remarks>
public sealed partial class HollowObjectTypeDataElements
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

        ReadFieldStatistics(input, Schema);

        FixedLengthData = FixedLengthElementArray.NewFrom(input, MemoryRecycler);

        ReadVarLengthData(input, Schema);
    }

    /// <summary>
    /// The raw fixed-length value of a field, before any null decoding.
    /// </summary>
    internal long GetFixedFieldValue(int ordinal, int fieldIndex) =>
        FixedLengthData!.GetLargeElementValue(
            ((long)BitsPerRecord * ordinal) + BitOffsetPerField[fieldIndex], BitsPerField[fieldIndex]);

    /// <summary>
    /// The raw fixed-length value of a field that may be wider than 64 bits.
    /// </summary>
    internal (long Low, long High) GetWideFixedFieldValue(int ordinal, int fieldIndex) =>
        FixedLengthData!.GetWideElementValue(
            ((long)BitsPerRecord * ordinal) + BitOffsetPerField[fieldIndex], BitsPerField[fieldIndex]);

    /// <summary>
    /// The byte range of a variable-length field, and whether it is null.
    /// </summary>
    internal (long Start, long End, bool IsNull) GetVarLengthRange(int ordinal, int fieldIndex)
    {
        int numBits = BitsPerField[fieldIndex];
        long end = GetFixedFieldValue(ordinal, fieldIndex);
        bool isNull = (end & (1L << (numBits - 1))) != 0;

        end &= (1L << (numBits - 1)) - 1;

        long start = ordinal == 0
            ? 0
            : GetFixedFieldValue(ordinal - 1, fieldIndex) & ((1L << (numBits - 1)) - 1);

        return (start, end, isNull);
    }

    /// <summary>
    /// Builds the successor to <paramref name="from"/> by taking each ordinal's record from
    /// <paramref name="delta"/> where the delta adds it, and from <paramref name="from"/> otherwise.
    /// </summary>
    /// <remarks>
    /// Removed ordinals are left as null records rather than dropped: a record's ordinal is its
    /// identity, so the ordinal space keeps its shape and later cycles can reuse the hole.
    /// </remarks>
    internal static HollowObjectTypeDataElements ApplyDelta(
        HollowObjectTypeDataElements from, HollowObjectTypeDataElements delta)
    {
        HollowObjectSchema schema = from.Schema;
        HollowObjectTypeDataElements target = new(schema, from.MemoryRecycler)
        {
            MaxOrdinal = delta.MaxOrdinal,
            EncodedRemovals = delta.EncodedRemovals,
        };

        // A field's width in the new state is the delta's where the delta carries it, otherwise the
        // width it already had.
        int bitsPerRecord = 0;
        for (int i = 0; i < schema.FieldCount; i++)
        {
            int deltaFieldIndex = delta.Schema.GetPosition(schema.GetFieldName(i));
            target.BitsPerField[i] = deltaFieldIndex == -1 ? from.BitsPerField[i] : delta.BitsPerField[deltaFieldIndex];
            target.NullValueForField[i] =
                target.BitsPerField[i] >= 64 ? -1L : (1L << target.BitsPerField[i]) - 1;
            target.BitOffsetPerField[i] = bitsPerRecord;
            bitsPerRecord += target.BitsPerField[i];
        }

        target.BitsPerRecord = bitsPerRecord;
        target.FixedLengthData = new FixedLengthElementArray(
            from.MemoryRecycler, (long)bitsPerRecord * (target.MaxOrdinal + 1));

        for (int i = 0; i < schema.FieldCount; i++)
        {
            if (schema.GetFieldType(i).IsVariableLength())
            {
                target.VarLengthData[i] = new SegmentedByteArray(from.MemoryRecycler);
            }
        }

        long[] varLengthWritePointers = new long[schema.FieldCount];

        GapEncodedVariableLengthIntegerReader additions =
            delta.EncodedAdditions ?? GapEncodedVariableLengthIntegerReader.EmptyReader;
        additions.Reset();

        int deltaOrdinal = 0;

        // A record the delta leaves alone keeps its ordinal, so when the layout is unchanged its bits
        // are identical in both states at the same offset — a run of them is one memory copy rather
        // than a field-by-field re-encode. See CopyUnchangedRun.
        bool layoutUnchanged = LayoutUnchanged(target, from);

        for (int ordinal = 0; ordinal <= target.MaxOrdinal; ordinal++)
        {
            bool addedByDelta = additions.NextElement() == ordinal;

            if (!addedByDelta && layoutUnchanged && ordinal <= from.MaxOrdinal)
            {
                // The run ends where the delta's next addition begins, or where either state's ordinals
                // run out, whichever comes first. An exhausted reader reports int.MaxValue, which the
                // other two bounds then decide.
                int runEnd = Math.Min(
                    Math.Min(from.MaxOrdinal, target.MaxOrdinal), additions.NextElement() - 1);

                CopyUnchangedRun(target, from, ordinal, runEnd, varLengthWritePointers);

                ordinal = runEnd;
                continue;
            }

            HollowObjectTypeDataElements? source;
            int sourceOrdinal;

            if (addedByDelta)
            {
                source = delta;
                sourceOrdinal = deltaOrdinal++;
                additions.Advance();
            }
            else if (ordinal <= from.MaxOrdinal)
            {
                source = from;
                sourceOrdinal = ordinal;
            }
            else
            {
                // Beyond the previous state's ordinals and not added: an empty slot.
                source = null;
                sourceOrdinal = -1;
            }

            for (int fieldIndex = 0; fieldIndex < schema.FieldCount; fieldIndex++)
            {
                long fieldBitOffset = ((long)bitsPerRecord * ordinal) + target.BitOffsetPerField[fieldIndex];
                int sourceFieldIndex = source is null
                    ? -1
                    : source.Schema.GetPosition(schema.GetFieldName(fieldIndex));

                if (schema.GetFieldType(fieldIndex).IsVariableLength())
                {
                    CopyVarLengthField(
                        target, source, sourceOrdinal, sourceFieldIndex, fieldIndex, fieldBitOffset, varLengthWritePointers);
                }
                else if (schema.GetFieldType(fieldIndex) == FieldType.Decimal)
                {
                    // A decimal is always 128 bits, so it never has to be re-encoded at a new width --
                    // but it does need both halves carried across.
                    (long low, long high) = source is null || sourceFieldIndex == -1
                        ? (DecimalBits.NullLow, DecimalBits.NullHigh)
                        : source.GetWideFixedFieldValue(sourceOrdinal, sourceFieldIndex);

                    target.FixedLengthData.SetWideElementValue(
                        fieldBitOffset, target.BitsPerField[fieldIndex], low, high);
                }
                else
                {
                    long value = source is null || sourceFieldIndex == -1
                        ? target.NullValueForField[fieldIndex]
                        : source.GetFixedFieldValue(sourceOrdinal, sourceFieldIndex);

                    // A null in the source is all-ones at the source's width; re-encode it at the
                    // target's width rather than copying the bits.
                    if (source is not null && sourceFieldIndex != -1
                        && value == source.NullValueForField[sourceFieldIndex])
                    {
                        value = target.NullValueForField[fieldIndex];
                    }

                    target.FixedLengthData.SetElementValue(fieldBitOffset, target.BitsPerField[fieldIndex], value);
                }
            }
        }

        return target;
    }

    /// <summary>
    /// Whether a record occupies the same bits in <paramref name="target"/> as it did in
    /// <paramref name="from"/>, which is what lets a run of unchanged records be copied rather than
    /// re-encoded.
    /// </summary>
    /// <remarks>
    /// The two share a schema, so the fields are in the same order; all that can differ is a width,
    /// which the delta widens when the new state needs more bits for a value or a var-length offset.
    /// </remarks>
    private static bool LayoutUnchanged(
        HollowObjectTypeDataElements target, HollowObjectTypeDataElements from)
    {
        if (target.BitsPerRecord != from.BitsPerRecord)
        {
            return false;
        }

        for (int i = 0; i < target.Schema.FieldCount; i++)
        {
            if (target.BitsPerField[i] != from.BitsPerField[i])
            {
                return false;
            }

            // A var-length field's bytes are copied wholesale, which needs both sides segmented.
            if (target.Schema.GetFieldType(i).IsVariableLength()
                && from.VarLengthData[i] is not SegmentedByteArray)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Copies ordinals <paramref name="firstOrdinal"/> through <paramref name="lastOrdinal"/> from
    /// <paramref name="from"/> in bulk, which they are only eligible for when the layout is unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three memory copies replace a field-by-field re-encode of every record in the run: the records'
    /// fixed-length bits, which are identical because the layout and the ordinals both are; and per
    /// var-length field, one copy of the run's bytes.
    /// </para>
    /// <para>
    /// Those bytes land at a different offset than they had in the source, so every pointer in the run
    /// is off by the same amount and is corrected in one strided pass. The correction cannot disturb
    /// the null flag in the pointer's top bit: the flag survives because the corrected offset still
    /// fits below it, which is exactly what the field was widened to guarantee.
    /// </para>
    /// </remarks>
    private static void CopyUnchangedRun(
        HollowObjectTypeDataElements target,
        HollowObjectTypeDataElements from,
        int firstOrdinal,
        int lastOrdinal,
        long[] varLengthWritePointers)
    {
        HollowObjectSchema schema = target.Schema;
        int recordCount = lastOrdinal - firstOrdinal + 1;
        RecordCopyDiagnostics.BulkCopiedObjects += recordCount;
        long bitsPerRecord = target.BitsPerRecord;
        long runStartBit = bitsPerRecord * firstOrdinal;

        target.FixedLengthData!.CopyBits(
            from.FixedLengthData!, runStartBit, runStartBit, bitsPerRecord * recordCount);

        for (int fieldIndex = 0; fieldIndex < schema.FieldCount; fieldIndex++)
        {
            if (!schema.GetFieldType(fieldIndex).IsVariableLength())
            {
                continue;
            }

            // Offsets never decrease, so the run's bytes are one contiguous range even where a record
            // inside it holds nothing.
            long sourceStart = from.GetVarLengthRange(firstOrdinal, fieldIndex).Start;
            long sourceEnd = from.GetVarLengthRange(lastOrdinal, fieldIndex).End;
            long length = sourceEnd - sourceStart;

            long writePointer = varLengthWritePointers[fieldIndex];

            if (length > 0)
            {
                ((SegmentedByteArray)target.VarLengthData[fieldIndex]!).Copy(
                    from.VarLengthData[fieldIndex]!, sourceStart, writePointer, length);
            }

            if (writePointer != sourceStart)
            {
                target.FixedLengthData.IncrementMany(
                    runStartBit + target.BitOffsetPerField[fieldIndex],
                    writePointer - sourceStart,
                    bitsPerRecord,
                    recordCount);
            }

            varLengthWritePointers[fieldIndex] = writePointer + length;
        }
    }

    private static void CopyVarLengthField(
        HollowObjectTypeDataElements target,
        HollowObjectTypeDataElements? source,
        int sourceOrdinal,
        int sourceFieldIndex,
        int fieldIndex,
        long fieldBitOffset,
        long[] varLengthWritePointers)
    {
        int numBits = target.BitsPerField[fieldIndex];
        IVariableLengthData targetData = target.VarLengthData[fieldIndex]!;

        bool isNull = source is null || sourceFieldIndex == -1;

        if (!isNull)
        {
            (long start, long end, bool sourceIsNull) = source!.GetVarLengthRange(sourceOrdinal, sourceFieldIndex);
            isNull = sourceIsNull;

            if (!sourceIsNull)
            {
                long length = end - start;
                IVariableLengthData sourceData = source.VarLengthData[sourceFieldIndex]!;

                ((SegmentedByteArray)targetData).Copy(
                    sourceData, start, varLengthWritePointers[fieldIndex], length);

                varLengthWritePointers[fieldIndex] += length;
            }
        }

        // The offset written is always the end of this record's range, null or not, because the next
        // record reads it as its own start.
        long offset = varLengthWritePointers[fieldIndex];
        target.FixedLengthData!.SetElementValue(
            fieldBitOffset, numBits, isNull ? offset | (1L << (numBits - 1)) : offset);
    }
}
