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

using Hollow.Core.Index;
using Hollow.Core.Index.Key;
using Hollow.Core.Memory;
using Hollow.Core.Memory.Encoding;
using Hollow.Core.Read;
using Hollow.Core.Schema;

namespace Hollow.Core.Write;

/// <summary>
/// Hashes a record by a declared key, reading the key's fields straight out of the producer's
/// serialised record bytes.
/// </summary>
/// <remarks>
/// Used when a set or map schema declares a hash key: the producer has to place each element in the
/// bucket a consumer will probe for, which means hashing the referenced record's key fields rather than
/// its ordinal.
/// </remarks>
internal sealed class HollowWriteStateEnginePrimaryKeyHasher
{
    private readonly HollowObjectTypeWriteState[][] _typeStates;
    private readonly int[][] _fieldPathIndexes;

    /// <summary>
    /// Resolves <paramref name="primaryKey"/> against <paramref name="writeEngine"/>.
    /// </summary>
    /// <exception cref="FieldPathException">One of the key's field paths cannot be bound.</exception>
    internal HollowWriteStateEnginePrimaryKeyHasher(PrimaryKey primaryKey, HollowWriteStateEngine writeEngine)
    {
        ArgumentNullException.ThrowIfNull(primaryKey);
        ArgumentNullException.ThrowIfNull(writeEngine);

        HollowObjectTypeWriteState rootTypeState =
            writeEngine.GetTypeState(primaryKey.Type) as HollowObjectTypeWriteState
            ?? throw new ArgumentException(
                $"type {primaryKey.Type} is not an object type in this state", nameof(primaryKey));

        _fieldPathIndexes = new int[primaryKey.FieldCount][];
        _typeStates = new HollowObjectTypeWriteState[primaryKey.FieldCount][];

        for (int i = 0; i < primaryKey.FieldCount; i++)
        {
            _fieldPathIndexes[i] = primaryKey.GetFieldPathIndex(writeEngine, i);
            _typeStates[i] = new HollowObjectTypeWriteState[_fieldPathIndexes[i].Length];
            _typeStates[i][0] = rootTypeState;

            for (int j = 1; j < _typeStates[i].Length; j++)
            {
                string referencedType =
                    _typeStates[i][j - 1].Schema.GetReferencedType(_fieldPathIndexes[i][j - 1])!;

                _typeStates[i][j] = writeEngine.GetTypeState(referencedType) as HollowObjectTypeWriteState
                    ?? throw new ArgumentException(
                        $"type {referencedType} is not an object type in this state", nameof(primaryKey));
            }
        }
    }

    /// <summary>
    /// Creates a hasher for <paramref name="hashKey"/>, or returns <see langword="null"/> when there is
    /// no key or one of its paths names a type the state does not hold.
    /// </summary>
    /// <remarks>
    /// A key that cannot be bound is not fatal: the records are still written, hashed by element ordinal
    /// as they would be without a declared key. Only lookups by that key stop working.
    /// </remarks>
    internal static HollowWriteStateEnginePrimaryKeyHasher? TryCreate(
        PrimaryKey? hashKey, HollowWriteStateEngine? writeEngine)
    {
        if (hashKey is null || writeEngine is null)
        {
            return null;
        }

        try
        {
            return new HollowWriteStateEnginePrimaryKeyHasher(hashKey, writeEngine);
        }
        catch (FieldPathException e) when (e.Error == FieldPathError.NotBindable)
        {
            return null;
        }
    }

    /// <summary>
    /// Hashes the key of the record at <paramref name="ordinal"/> of the key's root type.
    /// </summary>
    internal int GetRecordHash(int ordinal)
    {
        int hash = 0;

        for (int i = 0; i < _fieldPathIndexes.Length; i++)
        {
            hash *= 31;
            hash ^= HashValue(ordinal, i);
        }

        return hash;
    }

    private int HashValue(int ordinal, int fieldIndex)
    {
        int[] path = _fieldPathIndexes[fieldIndex];
        HollowObjectTypeWriteState[] typeStates = _typeStates[fieldIndex];

        for (int i = 0; i < path.Length - 1; i++)
        {
            SegmentedByteArray data = typeStates[i].GetByteDataForOrdinal(ordinal);
            long offset = NavigateToField(
                typeStates[i].Schema, path[i], data, typeStates[i].GetPointerForData(ordinal));

            ordinal = VarInt.ReadVInt(data, offset);
        }

        HollowObjectTypeWriteState lastTypeState = typeStates[^1];
        HollowObjectSchema schema = lastTypeState.Schema;
        SegmentedByteArray lastData = lastTypeState.GetByteDataForOrdinal(ordinal);
        long lastOffset = NavigateToField(
            schema, path[^1], lastData, lastTypeState.GetPointerForData(ordinal));

        return HashCodes.HashInt(FieldHashCode(schema, path[^1], lastData, lastOffset));
    }

    /// <summary>
    /// Skips over the fields preceding <paramref name="fieldIndex"/>, which are laid out back to back
    /// with no per-field offsets.
    /// </summary>
    private static long NavigateToField(
        HollowObjectSchema schema, int fieldIndex, SegmentedByteArray data, long offset)
    {
        for (int i = 0; i < fieldIndex; i++)
        {
            switch (schema.GetFieldType(i))
            {
                case FieldType.Int:
                case FieldType.Long:
                case FieldType.Reference:
                    offset += VarInt.NextVLongSize(data, offset);
                    break;

                case FieldType.Bytes:
                case FieldType.String:
                case FieldType.Decimal:
                    int fieldLength = VarInt.ReadVInt(data, offset);
                    offset += VarInt.SizeOfVInt(fieldLength) + fieldLength;
                    break;

                case FieldType.Boolean:
                    offset++;
                    break;

                case FieldType.Double:
                    offset += 8;
                    break;

                case FieldType.Float:
                    offset += 4;
                    break;

                default:
                    break;
            }
        }

        return offset;
    }

    /// <exception cref="ArgumentException">The field type cannot be hashed.</exception>
    private static int FieldHashCode(
        HollowObjectSchema schema, int fieldIndex, SegmentedByteArray data, long offset)
    {
        switch (schema.GetFieldType(fieldIndex))
        {
            case FieldType.Int:
                return VarInt.ReadVNull(data, offset)
                    ? 0
                    : ZigZag.DecodeInt(VarInt.ReadVInt(data, offset));

            case FieldType.Long:
                if (VarInt.ReadVNull(data, offset))
                {
                    return 0;
                }

                long longValue = ZigZag.DecodeLong(VarInt.ReadVLong(data, offset));
                return (int)(longValue ^ (long)((ulong)longValue >> 32));

            case FieldType.Reference:
                return VarInt.ReadVInt(data, offset);

            case FieldType.Bytes:
                int byteLength = VarInt.ReadVInt(data, offset);
                return HashCodes.Compute(data, offset + VarInt.SizeOfVInt(byteLength), byteLength);

            case FieldType.String:
                int stringByteLength = VarInt.ReadVInt(data, offset);
                return NaturalStringHashCode(
                    data, offset + VarInt.SizeOfVInt(stringByteLength), stringByteLength);

            case FieldType.Boolean:
                return VarInt.ReadVNull(data, offset) ? 0 : data.Get(offset) == 1 ? 1231 : 1237;

            case FieldType.Double:
                long longBits = data.ReadInt64Bits(offset);
                return (int)(longBits ^ (long)((ulong)longBits >> 32));

            case FieldType.Float:
                return data.ReadInt32Bits(offset);

            case FieldType.Decimal:
                int decimalLength = VarInt.ReadVInt(data, offset);
                return HollowReadFieldUtils.DecimalHashCode(
                    DecimalEncoding.Decode(data, offset + VarInt.SizeOfVInt(decimalLength), decimalLength));

            default:
                throw new ArgumentException(
                    $"schema {schema.Name} has an unknown type for field {schema.GetFieldName(fieldIndex)}: "
                    + $"{schema.GetFieldType(fieldIndex)}",
                    nameof(fieldIndex));
        }
    }

    /// <summary>
    /// Reproduces <c>java.lang.String.hashCode</c> over a string still in its serialised form, which is
    /// a run of variable-length-encoded UTF-16 code units.
    /// </summary>
    private static int NaturalStringHashCode(SegmentedByteArray data, long offset, int length)
    {
        int hashCode = 0;
        long endOffset = offset + length;

        while (offset < endOffset)
        {
            int c = VarInt.ReadVInt(data, offset);
            hashCode = (hashCode * 31) + c;
            offset += VarInt.SizeOfVInt(c);
        }

        return hashCode;
    }
}
