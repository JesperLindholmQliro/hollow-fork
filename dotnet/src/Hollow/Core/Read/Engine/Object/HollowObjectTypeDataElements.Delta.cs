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
/// <strong>Port note.</strong> Java's <c>HollowObjectDeltaApplicator</c> has a fast path that bulk-copies
/// runs of unchanged records with <c>copyBits</c> and then fixes up their variable-length pointers with
/// <c>incrementMany</c>. Only the record-at-a-time path is ported: it produces identical output and is
/// far easier to verify, at the cost of a slower delta application.
/// </remarks>
public sealed partial class HollowObjectTypeDataElements
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

        ReadFieldStatistics(input, Schema);

        FixedLengthData = FixedLengthElementArray.NewFrom(input, _memoryRecycler);

        ReadVarLengthData(input, Schema);
    }

    /// <summary>
    /// The raw fixed-length value of a field, before any null decoding.
    /// </summary>
    internal long GetFixedFieldValue(int ordinal, int fieldIndex) =>
        FixedLengthData!.GetLargeElementValue(
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
        HollowObjectTypeDataElements target = new(schema, from._memoryRecycler)
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
                target.BitsPerField[i] == 64 ? -1L : (1L << target.BitsPerField[i]) - 1;
            target.BitOffsetPerField[i] = bitsPerRecord;
            bitsPerRecord += target.BitsPerField[i];
        }

        target.BitsPerRecord = bitsPerRecord;
        target.FixedLengthData = new FixedLengthElementArray(
            from._memoryRecycler, (long)bitsPerRecord * (target.MaxOrdinal + 1));

        for (int i = 0; i < schema.FieldCount; i++)
        {
            if (schema.GetFieldType(i).IsVariableLength())
            {
                target.VarLengthData[i] = new SegmentedByteArray(from._memoryRecycler);
            }
        }

        long[] varLengthWritePointers = new long[schema.FieldCount];

        GapEncodedVariableLengthIntegerReader additions =
            delta.EncodedAdditions ?? GapEncodedVariableLengthIntegerReader.EmptyReader;
        additions.Reset();

        int deltaOrdinal = 0;

        for (int ordinal = 0; ordinal <= target.MaxOrdinal; ordinal++)
        {
            bool addedByDelta = additions.NextElement() == ordinal;

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

                for (long i = 0; i < length; i++)
                {
                    ((SegmentedByteArray)targetData).Set(varLengthWritePointers[fieldIndex] + i, sourceData.Get(start + i));
                }

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
