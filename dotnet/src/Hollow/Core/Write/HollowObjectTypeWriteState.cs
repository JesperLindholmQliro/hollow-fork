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
using Hollow.Core.Memory.Pool;
using Hollow.Core.Schema;

namespace Hollow.Core.Write;

/// <summary>
/// The write state of an object type: packs each record's fields into a fixed-width bit string, with
/// string and byte-array payloads appended to per-field variable-length buffers.
/// </summary>
public sealed partial class HollowObjectTypeWriteState : HollowTypeWriteState
{
    private FieldStatistics? _fieldStats;
    private FixedLengthElementArray[]? _fixedLengthLongArray;
    private ByteDataArray?[][]? _varLengthByteArrays;
    private long[]? _recordBitOffset;

    /// <summary>
    /// Initialises a write state for <paramref name="schema"/>.
    /// </summary>
    /// <param name="schema">The schema of the type.</param>
    /// <param name="numShards">
    /// The number of shards, which must be a power of two, or -1 to derive it from the data size.
    /// </param>
    /// <param name="usePartitionedOrdinalMap">
    /// Whether to spread this type's records across four ordinal maps rather than one, so that adding
    /// records contends on four write locks instead of one.
    /// </param>
    public HollowObjectTypeWriteState(
        HollowObjectSchema schema, int numShards = -1, bool usePartitionedOrdinalMap = false)
        : base(schema, numShards, usePartitionedOrdinalMap)
    {
    }

    /// <summary>The schema of the type this state holds.</summary>
    public new HollowObjectSchema Schema => (HollowObjectSchema)base.Schema;

    /// <inheritdoc />
    public override void PrepareForWrite(bool canReshard)
    {
        base.PrepareForWrite(canReshard);

        GatherFieldStats();
        GatherShardingStats(MaxOrdinal, canReshard);
    }

    /// <inheritdoc />
    public override void PrepareForNextCycle()
    {
        base.PrepareForNextCycle();
        _fieldStats = null;
    }

    /// <inheritdoc />
    public override void CalculateSnapshot()
    {
        FieldStatistics fieldStats = _fieldStats
            ?? throw new InvalidOperationException($"{nameof(PrepareForWrite)} has not been called");

        int numBitsPerRecord = fieldStats.NumBitsPerRecord;
        int numShards = NumShards;

        _fixedLengthLongArray = new FixedLengthElementArray[numShards];
        _varLengthByteArrays = new ByteDataArray[numShards][];
        _recordBitOffset = new long[numShards];

        for (int i = 0; i < numShards; i++)
        {
            _fixedLengthLongArray[i] = new FixedLengthElementArray(
                WastefulRecycler.DefaultInstance, (long)numBitsPerRecord * (MaxShardOrdinal[i] + 1));
            _varLengthByteArrays[i] = new ByteDataArray[Schema.FieldCount];
        }

        int shardMask = numShards - 1;

        for (int ordinal = 0; ordinal <= MaxOrdinal; ordinal++)
        {
            int shardNumber = ordinal & shardMask;

            if (CurrentCyclePopulated.Get(ordinal))
            {
                AddRecord(ordinal, _recordBitOffset[shardNumber], _fixedLengthLongArray[shardNumber], _varLengthByteArrays[shardNumber]);
            }
            else
            {
                AddNullRecord(_recordBitOffset[shardNumber], _fixedLengthLongArray[shardNumber], _varLengthByteArrays[shardNumber]);
            }

            _recordBitOffset[shardNumber] += numBitsPerRecord;
        }
    }

    /// <inheritdoc />
    public override void WriteSnapshot(HollowBlobOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (_fixedLengthLongArray is null)
        {
            throw new InvalidOperationException($"{nameof(CalculateSnapshot)} has not been called");
        }

        // An unsharded blob omits the overall max ordinal, for compatibility with pre-2.1.0 readers.
        if (NumShards == 1)
        {
            WriteSnapshotShard(output, 0);
        }
        else
        {
            VarInt.WriteVInt(output, MaxOrdinal);
            for (int i = 0; i < NumShards; i++)
            {
                WriteSnapshotShard(output, i);
            }
        }

        CurrentCyclePopulated.SerializeBitsTo(output);

        _fixedLengthLongArray = null;
        _varLengthByteArrays = null;
        _recordBitOffset = null;
    }

    /// <inheritdoc />
    protected override int TypeStateNumShards(int maxOrdinal)
    {
        FieldStatistics fieldStats = _fieldStats!;

        long projectedSizeOfType = ((long)fieldStats.NumBitsPerRecord * (maxOrdinal + 1)) / 8;
        projectedSizeOfType += fieldStats.GetTotalSizeOfAllVarLengthData();

        long targetMaxShardSize = StateEngine?.TargetMaxTypeShardSize
            ?? HollowWriteStateEngine.DefaultTargetMaxTypeShardSize;

        int targetNumShards = 1;
        while (targetMaxShardSize * targetNumShards < projectedSizeOfType)
        {
            targetNumShards *= 2;
        }

        return targetNumShards;
    }

    private void GatherFieldStats()
    {
        FieldStatistics fieldStats = new(Schema);

        for (int ordinal = 0; ordinal <= MaxOrdinal; ordinal++)
        {
            if (!CurrentCyclePopulated.Get(ordinal) && !PreviousCyclePopulated.Get(ordinal))
            {
                continue;
            }

            long pointer = GetPointerForData(ordinal);
            IByteData data = GetByteDataForOrdinal(ordinal);

            for (int fieldIndex = 0; fieldIndex < Schema.FieldCount; fieldIndex++)
            {
                pointer = MeasureField(fieldStats, data, pointer, fieldIndex);
            }
        }

        fieldStats.CompleteCalculations();
        _fieldStats = fieldStats;
    }

    private long MeasureField(FieldStatistics fieldStats, IByteData data, long pointer, int fieldIndex)
    {
        switch (Schema.GetFieldType(fieldIndex))
        {
            case FieldType.Boolean:
                fieldStats.AddFixedLengthFieldRequiredBits(fieldIndex, 2);
                return pointer + 1;

            case FieldType.Float:
                fieldStats.AddFixedLengthFieldRequiredBits(fieldIndex, 32);
                return pointer + 4;

            case FieldType.Double:
                fieldStats.AddFixedLengthFieldRequiredBits(fieldIndex, 64);
                return pointer + 8;

            case FieldType.Decimal:
                fieldStats.AddFixedLengthFieldRequiredBits(fieldIndex, DecimalBits.BitsPerDecimal);
                return pointer + DecimalBits.BytesPerDecimal;

            case FieldType.Long:
            case FieldType.Int:
            case FieldType.Reference:
                if (VarInt.ReadVNull(data, pointer))
                {
                    fieldStats.AddFixedLengthFieldRequiredBits(fieldIndex, 1);
                    return pointer + 1;
                }
                else
                {
                    long value = VarInt.ReadVLong(data, pointer);

                    // The all-ones value of this width is reserved for null, so the field must be wide
                    // enough to represent value + 1.
                    int requiredBits = 64 - BitOperations.LeadingZeroCount((ulong)(value + 1));
                    fieldStats.AddFixedLengthFieldRequiredBits(fieldIndex, requiredBits);
                    return pointer + VarInt.SizeOfVLong(value);
                }

            case FieldType.Bytes:
            case FieldType.String:
                if (VarInt.ReadVNull(data, pointer))
                {
                    fieldStats.AddFixedLengthFieldRequiredBits(fieldIndex, 1);
                    return pointer + 1;
                }
                else
                {
                    int length = VarInt.ReadVInt(data, pointer);
                    fieldStats.AddVarLengthFieldSize(fieldIndex, length);
                    return pointer + length + VarInt.SizeOfVInt(length);
                }

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(fieldIndex), Schema.GetFieldType(fieldIndex), "unknown field type");
        }
    }

    private void WriteSnapshotShard(HollowBlobOutput output, int shardNumber)
    {
        FieldStatistics fieldStats = _fieldStats!;

        // 1) The shard's max ordinal.
        VarInt.WriteVInt(output, MaxShardOrdinal[shardNumber]);

        // 2) The width of each fixed-length field.
        for (int i = 0; i < Schema.FieldCount; i++)
        {
            VarInt.WriteVInt(output, fieldStats.GetMaxBitsForField(i));
        }

        // 3) The fixed-length bit string.
        long numBitsRequired = _recordBitOffset![shardNumber];
        long numLongsRequired = numBitsRequired == 0 ? 0 : ((numBitsRequired - 1) / 64) + 1;
        _fixedLengthLongArray![shardNumber].WriteTo(output, numLongsRequired);

        // 4) The variable-length payload of each field.
        ByteDataArray?[] varLengthByteArrays = _varLengthByteArrays![shardNumber];
        foreach (ByteDataArray? varLengthBuffer in varLengthByteArrays)
        {
            if (varLengthBuffer is null)
            {
                VarInt.WriteVLong(output, 0);
            }
            else
            {
                VarInt.WriteVLong(output, varLengthBuffer.Length);
                varLengthBuffer.UnderlyingArray.WriteTo(output.Stream, 0, varLengthBuffer.Length);
            }
        }
    }

    /// <summary>
    /// Writes the placeholder for an unpopulated ordinal.
    /// </summary>
    /// <remarks>
    /// Only the variable-length fields need anything written: a record reads its range's start offset
    /// from the <em>previous</em> record's end offset, so every ordinal, populated or not, has to carry
    /// a correct end offset.
    /// </remarks>
    private void AddNullRecord(
        long recordBitOffset, FixedLengthElementArray fixedLengthLongArray, ByteDataArray?[] varLengthByteArrays)
    {
        FieldStatistics fieldStats = _fieldStats!;

        for (int fieldIndex = 0; fieldIndex < Schema.FieldCount; fieldIndex++)
        {
            if (!Schema.GetFieldType(fieldIndex).IsVariableLength())
            {
                continue;
            }

            long fieldBitOffset = recordBitOffset + fieldStats.GetFieldBitOffset(fieldIndex);
            int bitsPerElement = fieldStats.GetMaxBitsForField(fieldIndex);
            long currentPointer = varLengthByteArrays[fieldIndex]?.Length ?? 0;

            fixedLengthLongArray.SetElementValue(fieldBitOffset, bitsPerElement, currentPointer);
        }
    }

    private void AddRecord(
        int ordinal,
        long recordBitOffset,
        FixedLengthElementArray fixedLengthLongArray,
        ByteDataArray?[] varLengthByteArrays)
    {
        long pointer = GetPointerForData(ordinal);
        IByteData data = GetByteDataForOrdinal(ordinal);

        for (int fieldIndex = 0; fieldIndex < Schema.FieldCount; fieldIndex++)
        {
            pointer = AddRecordField(
                data, pointer, recordBitOffset, fieldIndex, fixedLengthLongArray, varLengthByteArrays);
        }
    }

    private long AddRecordField(
        IByteData data,
        long readPointer,
        long recordBitOffset,
        int fieldIndex,
        FixedLengthElementArray fixedLengthLongArray,
        ByteDataArray?[] varLengthByteArrays)
    {
        FieldStatistics fieldStats = _fieldStats!;

        long fieldBitOffset = recordBitOffset + fieldStats.GetFieldBitOffset(fieldIndex);
        int bitsPerElement = fieldStats.GetMaxBitsForField(fieldIndex);

        switch (Schema.GetFieldType(fieldIndex))
        {
            case FieldType.Boolean:
                // Two bits: 0 false, 1 true, 3 null.
                fixedLengthLongArray.SetElementValue(
                    fieldBitOffset, 2, VarInt.ReadVNull(data, readPointer) ? 3 : data.Get(readPointer));
                return readPointer + 1;

            case FieldType.Float:
                fixedLengthLongArray.SetElementValue(
                    fieldBitOffset, 32, data.ReadInt32Bits(readPointer) & 0xFFFFFFFFL);
                return readPointer + 4;

            case FieldType.Double:
                fixedLengthLongArray.SetElementValue(fieldBitOffset, 64, data.ReadInt64Bits(readPointer));
                return readPointer + 8;

            case FieldType.Decimal:
                // Two elements rather than one: no single element can be wider than 64 bits.
                fixedLengthLongArray.SetWideElementValue(
                    fieldBitOffset,
                    DecimalBits.BitsPerDecimal,
                    data.ReadInt64Bits(readPointer),
                    data.ReadInt64Bits(readPointer + 8));
                return readPointer + DecimalBits.BytesPerDecimal;

            case FieldType.Long:
            case FieldType.Int:
            case FieldType.Reference:
                if (VarInt.ReadVNull(data, readPointer))
                {
                    fixedLengthLongArray.SetElementValue(
                        fieldBitOffset, bitsPerElement, fieldStats.GetNullValueForField(fieldIndex));
                    return readPointer + 1;
                }
                else
                {
                    long value = VarInt.ReadVLong(data, readPointer);
                    fixedLengthLongArray.SetElementValue(fieldBitOffset, bitsPerElement, value);
                    return readPointer + VarInt.SizeOfVLong(value);
                }

            case FieldType.Bytes:
            case FieldType.String:
                ByteDataArray varLengthBuffer =
                    varLengthByteArrays[fieldIndex] ??= new ByteDataArray(WastefulRecycler.DefaultInstance);

                if (VarInt.ReadVNull(data, readPointer))
                {
                    // The high bit of the offset marks the field as null; the offset itself still has
                    // to be correct, because it is the next record's range start.
                    fixedLengthLongArray.SetElementValue(
                        fieldBitOffset, bitsPerElement, varLengthBuffer.Length | (1L << (bitsPerElement - 1)));
                    return readPointer + 1;
                }
                else
                {
                    int length = VarInt.ReadVInt(data, readPointer);
                    readPointer += VarInt.SizeOfVInt(length);
                    varLengthBuffer.CopyFrom(data, readPointer, length);

                    fixedLengthLongArray.SetElementValue(fieldBitOffset, bitsPerElement, varLengthBuffer.Length);
                    return readPointer + length;
                }

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(fieldIndex), Schema.GetFieldType(fieldIndex), "unknown field type");
        }
    }
}
