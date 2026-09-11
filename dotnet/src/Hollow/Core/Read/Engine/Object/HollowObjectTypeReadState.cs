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

using System.Buffers;
using System.Numerics;
using Hollow.Core.Memory;
using Hollow.Core.Memory.Encoding;
using Hollow.Core.Memory.Pool;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Schema;
using Hollow.Core.Write;

namespace Hollow.Core.Read.Engine.Object;

/// <summary>
/// Holds the records of an object type, decoding fields out of the packed bit string on demand.
/// </summary>
/// <remarks>
/// <strong>Port note.</strong> The Java class re-reads through a volatile shards holder on every
/// access so that a concurrent resharding cannot be observed half-applied. Resharding is not ported,
/// so the shards here are assigned once during <see cref="ReadSnapshot"/> and never replaced.
/// </remarks>
public sealed partial class HollowObjectTypeReadState : HollowTypeReadState, IHollowObjectTypeDataAccess
{
    private readonly HollowObjectSchema _unfilteredSchema;

    private volatile ShardsHolder<Shard> _shardsVolatile = ShardsHolder<Shard>.Empty;
    private int _maxOrdinal = -1;

    /// <summary>
    /// Initialises a read state for <paramref name="schema"/>.
    /// </summary>
    /// <param name="stateEngine">The engine this type belongs to.</param>
    /// <param name="memoryMode">The memory mode to hold record data in.</param>
    /// <param name="schema">The schema after field filtering, which reads are served against.</param>
    /// <param name="unfilteredSchema">
    /// The schema as written in the blob; defaults to <paramref name="schema"/> when unfiltered.
    /// </param>
    public HollowObjectTypeReadState(
        HollowReadStateEngine stateEngine,
        MemoryMode memoryMode,
        HollowObjectSchema schema,
        HollowObjectSchema? unfilteredSchema = null)
        : base(stateEngine, memoryMode, schema)
    {
        _unfilteredSchema = unfilteredSchema ?? schema;
    }

    /// <summary>The schema reads are served against.</summary>
    public new HollowObjectSchema Schema => (HollowObjectSchema)base.Schema;

    /// <inheritdoc />
    public override int MaxOrdinal => _maxOrdinal;

    /// <inheritdoc />
    public override ShardsHolder ShardsVolatile => _shardsVolatile;

    /// <inheritdoc />
    internal override void UpdateShards(HollowTypeReadStateShard[] shards) =>
        _shardsVolatile = new ShardsHolder<Shard>([.. shards.Cast<Shard>()]);

    /// <inheritdoc />
    internal override HollowTypeDataElements[] CreateTypeDataElements(int length) =>
        new HollowObjectTypeDataElements[length];

    /// <inheritdoc />
    internal override HollowTypeReadStateShard CreateTypeReadStateShard(
        HollowTypeDataElements elements, int shardOrdinalShift) =>
        new Shard((HollowObjectTypeDataElements)elements, shardOrdinalShift);

    /// <inheritdoc />
    public override long ApproxHeapFootprintInBytes
    {
        get
        {
            long total = 0;
            foreach (Shard shard in _shardsVolatile.TypedShards)
            {
                total += (long)shard.DataElements.BitsPerRecord * (shard.DataElements.MaxOrdinal + 1) / 8;

                foreach (IVariableLengthData? varLengthData in shard.DataElements.VarLengthData)
                {
                    total += varLengthData?.Size ?? 0;
                }
            }

            return total;
        }
    }

    /// <inheritdoc />
    public override void ReadSnapshot(HollowBlobInput input, IArraySegmentRecycler memoryRecycler, int numShards)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(memoryRecycler);

        if (numShards <= 0 || (numShards & (numShards - 1)) != 0)
        {
            throw new ArgumentException("Number of shards must be a power of 2!", nameof(numShards));
        }

        if (numShards > 1)
        {
            _maxOrdinal = VarInt.ReadVInt(input);
        }

        int shardOrdinalShift = BitOperations.TrailingZeroCount((uint)numShards);

        Shard[] shards = new Shard[numShards];
        for (int i = 0; i < numShards; i++)
        {
            HollowObjectTypeDataElements dataElements = new(Schema, memoryRecycler);
            dataElements.ReadSnapshot(input, _unfilteredSchema);
            shards[i] = new Shard(dataElements, shardOrdinalShift);
        }

        _shardsVolatile = new ShardsHolder<Shard>(shards);

        if (numShards == 1)
        {
            _maxOrdinal = shards[0].DataElements.MaxOrdinal;
        }

        SnapshotPopulatedOrdinalsReader.ReadOrdinals(input, Listeners);
    }

    /// <inheritdoc />
    public override void Destroy(IArraySegmentRecycler memoryRecycler)
    {
        foreach (Shard shard in _shardsVolatile.TypedShards)
        {
            shard.DataElements.Destroy();
        }

        _shardsVolatile = ShardsHolder<Shard>.Empty;
    }

    /// <inheritdoc />
    public bool IsNull(int ordinal, int fieldIndex)
    {
        Shard shard = ShardFor(ordinal);
        long fixedLengthValue = shard.ReadValue(ShardOrdinal(ordinal, shard), fieldIndex);

        switch (Schema.GetFieldType(fieldIndex))
        {
            case FieldType.Bytes:
            case FieldType.String:
                int numBits = shard.DataElements.BitsPerField[fieldIndex];
                return (fixedLengthValue & (1L << (numBits - 1))) != 0;
            case FieldType.Float:
                return (int)fixedLengthValue == HollowObjectWriteRecord.NullFloatBits;
            case FieldType.Double:
                return fixedLengthValue == HollowObjectWriteRecord.NullDoubleBits;
            case FieldType.Decimal:
                (long low, long high) = shard.ReadWideValue(ShardOrdinal(ordinal, shard), fieldIndex);
                return DecimalBits.IsNull(low, high);
            default:
                return fixedLengthValue == shard.DataElements.NullValueForField[fieldIndex];
        }
    }

    /// <inheritdoc />
    public int ReadOrdinal(int ordinal, int fieldIndex)
    {
        Shard shard = ShardFor(ordinal);
        long value = shard.ReadValue(ShardOrdinal(ordinal, shard), fieldIndex);

        return value == shard.DataElements.NullValueForField[fieldIndex]
            ? HollowConstants.OrdinalNone
            : (int)value;
    }

    /// <inheritdoc />
    public int ReadInt(int ordinal, int fieldIndex)
    {
        Shard shard = ShardFor(ordinal);
        long value = shard.ReadValue(ShardOrdinal(ordinal, shard), fieldIndex);

        return value == shard.DataElements.NullValueForField[fieldIndex]
            ? int.MinValue
            : ZigZag.DecodeInt((int)value);
    }

    /// <inheritdoc />
    public long ReadLong(int ordinal, int fieldIndex)
    {
        Shard shard = ShardFor(ordinal);
        long value = shard.ReadValue(ShardOrdinal(ordinal, shard), fieldIndex);

        return value == shard.DataElements.NullValueForField[fieldIndex]
            ? long.MinValue
            : ZigZag.DecodeLong(value);
    }

    /// <inheritdoc />
    public float ReadFloat(int ordinal, int fieldIndex)
    {
        Shard shard = ShardFor(ordinal);
        int value = (int)shard.ReadValue(ShardOrdinal(ordinal, shard), fieldIndex);

        return value == HollowObjectWriteRecord.NullFloatBits
            ? float.NaN
            : BitConverter.Int32BitsToSingle(value);
    }

    /// <inheritdoc />
    public double ReadDouble(int ordinal, int fieldIndex)
    {
        Shard shard = ShardFor(ordinal);
        int shardOrdinal = ShardOrdinal(ordinal, shard);
        long value = shard.DataElements.FixedLengthData!.GetLargeElementValue(
            shard.FieldOffset(shardOrdinal, fieldIndex), 64, -1L);

        return value == HollowObjectWriteRecord.NullDoubleBits
            ? double.NaN
            : BitConverter.Int64BitsToDouble(value);
    }

    /// <inheritdoc />
    public decimal? ReadDecimal(int ordinal, int fieldIndex)
    {
        Shard shard = ShardFor(ordinal);
        (long low, long high) = shard.ReadWideValue(ShardOrdinal(ordinal, shard), fieldIndex);

        return DecimalBits.IsNull(low, high) ? null : DecimalBits.Unpack(low, high);
    }

    /// <inheritdoc />
    public bool? ReadBoolean(int ordinal, int fieldIndex)
    {
        Shard shard = ShardFor(ordinal);
        long value = shard.ReadValue(ShardOrdinal(ordinal, shard), fieldIndex);

        return value == shard.DataElements.NullValueForField[fieldIndex] ? null : value == 1;
    }

    /// <inheritdoc />
    public byte[]? ReadBytes(int ordinal, int fieldIndex)
    {
        Shard shard = ShardFor(ordinal);
        (long startByte, long endByte, int numBits) = shard.VarLengthRange(ShardOrdinal(ordinal, shard), fieldIndex);

        if (IsVarLengthNull(endByte, numBits))
        {
            return null;
        }

        startByte &= (1L << (numBits - 1)) - 1;
        int length = (int)(endByte - startByte);

        IVariableLengthData data = shard.DataElements.VarLengthData[fieldIndex]!;
        byte[] result = new byte[length];
        for (int i = 0; i < length; i++)
        {
            result[i] = data.Get(startByte + i);
        }

        return result;
    }

    /// <inheritdoc />
    public string? ReadString(int ordinal, int fieldIndex)
    {
        Shard shard = ShardFor(ordinal);
        (long startByte, long endByte, int numBits) = shard.VarLengthRange(ShardOrdinal(ordinal, shard), fieldIndex);

        if (IsVarLengthNull(endByte, numBits))
        {
            return null;
        }

        startByte &= (1L << (numBits - 1)) - 1;
        int length = (int)(endByte - startByte);

        return ReadString(shard.DataElements.VarLengthData[fieldIndex]!, startByte, length);
    }

    /// <inheritdoc />
    public bool IsStringFieldEqual(int ordinal, int fieldIndex, string? testValue)
    {
        Shard shard = ShardFor(ordinal);
        (long startByte, long endByte, int numBits) = shard.VarLengthRange(ShardOrdinal(ordinal, shard), fieldIndex);

        if (IsVarLengthNull(endByte, numBits))
        {
            return testValue is null;
        }

        if (testValue is null)
        {
            return false;
        }

        startByte &= (1L << (numBits - 1)) - 1;
        int length = (int)(endByte - startByte);

        return TestStringEquality(shard.DataElements.VarLengthData[fieldIndex]!, startByte, length, testValue);
    }

    /// <inheritdoc />
    public int FindVarLengthFieldHashCode(int ordinal, int fieldIndex)
    {
        Shard shard = ShardFor(ordinal);
        (long startByte, long endByte, int numBits) = shard.VarLengthRange(ShardOrdinal(ordinal, shard), fieldIndex);

        if (IsVarLengthNull(endByte, numBits))
        {
            return -1;
        }

        startByte &= (1L << (numBits - 1)) - 1;
        int length = (int)(endByte - startByte);

        return HashCodes.Compute(shard.DataElements.VarLengthData[fieldIndex]!, startByte, length);
    }

    /// <summary>
    /// The width of a field in this type's records, or 0 when the field is not present.
    /// </summary>
    public int BitsRequiredForField(string fieldName)
    {
        int fieldIndex = Schema.GetPosition(fieldName);
        Shard[] shards = _shardsVolatile.TypedShards;

        return fieldIndex == -1 || shards.Length == 0 ? 0 : shards[0].DataElements.BitsPerField[fieldIndex];
    }

    /// <summary>
    /// A variable-length field is null when the high bit of its end offset is set.
    /// </summary>
    private static bool IsVarLengthNull(long endByte, int numBitsForField) =>
        (endByte & (1L << (numBitsForField - 1))) != 0;

    private static string ReadString(IVariableLengthData data, long position, int length)
    {
        char[] rented = ArrayPool<char>.Shared.Rent(length);
        try
        {
            Span<char> chars = rented.AsSpan(0, length);
            chars.Clear();

            int count = VarInt.ReadVIntsInto(data, position, length, chars);
            return new string(chars[..count]);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    private static bool TestStringEquality(IVariableLengthData data, long position, int length, string testValue)
    {
        // A one-byte-per-character encoding is the best case, so a stored range shorter than the test
        // string cannot possibly match.
        if (length < testValue.Length)
        {
            return false;
        }

        long endPosition = position + length;
        int characterIndex = 0;

        while (position < endPosition)
        {
            if (characterIndex >= testValue.Length)
            {
                return false;
            }

            int value = VarInt.ReadVInt(data, position);
            if (testValue[characterIndex++] != (char)value)
            {
                return false;
            }

            position += VarInt.SizeOfVInt(value);
        }

        return characterIndex == testValue.Length;
    }

    /// <summary>
    /// The shard holding <paramref name="ordinal"/>, read through one load of the shards holder so
    /// that a concurrent reshard cannot change the mask between selecting a shard and using it.
    /// </summary>
    private Shard ShardFor(int ordinal)
    {
        ShardsHolder<Shard> shards = _shardsVolatile;

        return shards.TypedShards[ordinal & shards.ShardNumberMask];
    }

    private static int ShardOrdinal(int ordinal, Shard shard) => ordinal >> shard.ShardOrdinalShift;

    /// <summary>
    /// One shard's records, addressed by the ordinal's high bits.
    /// </summary>
    internal sealed class Shard(HollowObjectTypeDataElements dataElements, int shardOrdinalShift)
        : HollowTypeReadStateShard(shardOrdinalShift)
    {
        internal HollowObjectTypeDataElements DataElements { get; } = dataElements;

        /// <inheritdoc />
        public override HollowTypeDataElements Elements => DataElements;

        internal long FieldOffset(int shardOrdinal, int fieldIndex) =>
            ((long)DataElements.BitsPerRecord * shardOrdinal) + DataElements.BitOffsetPerField[fieldIndex];

        internal long ReadValue(int shardOrdinal, int fieldIndex) =>
            DataElements.FixedLengthData!.GetLargeElementValue(
                FieldOffset(shardOrdinal, fieldIndex), DataElements.BitsPerField[fieldIndex]);

        /// <summary>
        /// Reads a field that may be wider than one element, which only a decimal is.
        /// </summary>
        internal (long Low, long High) ReadWideValue(int shardOrdinal, int fieldIndex) =>
            DataElements.FixedLengthData!.GetWideElementValue(
                FieldOffset(shardOrdinal, fieldIndex), DataElements.BitsPerField[fieldIndex]);

        /// <summary>
        /// The byte range of a variable-length field: its end offset is stored on the record, and its
        /// start offset is the previous record's end offset.
        /// </summary>
        internal (long StartByte, long EndByte, int NumBitsForField) VarLengthRange(int shardOrdinal, int fieldIndex)
        {
            int numBitsForField = DataElements.BitsPerField[fieldIndex];
            long currentBitOffset = FieldOffset(shardOrdinal, fieldIndex);

            long endByte = DataElements.FixedLengthData!.GetLargeElementValue(currentBitOffset, numBitsForField);
            long startByte = shardOrdinal != 0
                ? DataElements.FixedLengthData.GetLargeElementValue(
                    currentBitOffset - DataElements.BitsPerRecord, numBitsForField)
                : 0;

            return (startByte, endByte, numBitsForField);
        }
    }
}
