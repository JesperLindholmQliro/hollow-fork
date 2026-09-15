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

using Hollow.Core.Index.Key;
using Hollow.Core.Memory;
using Hollow.Core.Memory.Encoding;
using Hollow.Core.Schema;

namespace Hollow.Core.Write.ObjectMapper.FlatRecords;

/// <summary>
/// One record and everything it references, serialised on its own.
/// </summary>
/// <remarks>
/// <para>
/// A dataset's records live in a blob and are reached by ordinal. A flat record is the other thing: a
/// single record, complete, that can be handed from one process to another without either of them
/// holding a dataset. That is what makes it useful as the unit of a change feed — see
/// <c>PORTING.md</c> — and it is what the incremental producer takes instead of an object.
/// </para>
/// <para>
/// The layout, all of it variable-length integers:
/// </para>
/// <code>
/// [where the top record starts] [how long the records are] [record]* [primary key field locations]*
/// </code>
/// <para>
/// Each record is a schema id followed by that schema's own encoding — the same one the blob uses, so
/// the write records serialise themselves into it unchanged. A reference field holds the <em>index</em>
/// of another record in this same flat record rather than a dataset ordinal, which is what makes the
/// whole thing self-contained. The top record is last, because a record can only reference one already
/// written.
/// </para>
/// <para>
/// The trailing locations are the primary key's field values, already found, so that a reader can key
/// the record without walking it. This is what lets the incremental producer key a flat record it has
/// no model class for.
/// </para>
/// </remarks>
public sealed class FlatRecord
{
    /// <summary>
    /// Reads the flat record in <paramref name="recordData"/>, naming its schemas through
    /// <paramref name="schemaIdMapper"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The mapper does not know the top record's schema, or a primary key field is null.
    /// </exception>
    public FlatRecord(IByteData recordData, IHollowSchemaIdentifierMapper schemaIdMapper)
    {
        ArgumentNullException.ThrowIfNull(recordData);
        ArgumentNullException.ThrowIfNull(schemaIdMapper);

        Data = recordData;
        SchemaIdMapper = schemaIdMapper;

        int pointer = 0;

        int topRecordLocation = VarInt.ReadVInt(recordData, pointer);
        pointer += VarInt.SizeOfVInt(topRecordLocation);

        int length = VarInt.ReadVInt(recordData, pointer);
        pointer += VarInt.SizeOfVInt(length);

        DataStartByte = pointer;
        DataEndByte = length + DataStartByte + topRecordLocation;

        int topRecordSchemaId = VarInt.ReadVInt(recordData, DataStartByte + topRecordLocation);

        HollowSchema topRecordSchema = schemaIdMapper.GetSchema(topRecordSchemaId)
            ?? throw new InvalidOperationException(
                $"the schema identifier mapper does not know schema {topRecordSchemaId}");

        RecordPrimaryKey = ReadRecordPrimaryKey(topRecordSchema, topRecordSchemaId);
    }

    /// <summary>
    /// The key of the top record, or null where its type declares none.
    /// </summary>
    /// <remarks>
    /// Read without a model class and without the dataset: the locations of the key's fields are
    /// written into the record, so this is what lets a flat record be keyed by whoever receives it.
    /// </remarks>
    public RecordPrimaryKey? RecordPrimaryKey { get; }

    /// <summary>How many bytes the whole record occupies.</summary>
    public long Size => Data.Length;

    internal IByteData Data { get; }

    internal IHollowSchemaIdentifierMapper SchemaIdMapper { get; }

    /// <summary>Where the first record begins, past the two lengths at the front.</summary>
    internal int DataStartByte { get; }

    /// <summary>Where the records end and the primary key locations begin.</summary>
    internal int DataEndByte { get; }

    /// <summary>The bytes of the whole record, for handing to something that wants an array.</summary>
    public byte[] ToArray()
    {
        byte[] bytes = new byte[Data.Length];
        Data.CopyTo(0, bytes);

        return bytes;
    }

    private RecordPrimaryKey? ReadRecordPrimaryKey(HollowSchema topRecordSchema, int topRecordSchemaId)
    {
        // Only an object type has a key, and only some object types declare one.
        if (topRecordSchema is not HollowObjectSchema { PrimaryKey: { } primaryKey } objectSchema)
        {
            return null;
        }

        FieldType[] fieldTypes = SchemaIdMapper.GetPrimaryKeyFieldTypes(topRecordSchemaId);
        object?[] key = new object?[primaryKey.FieldCount];
        int pointer = DataEndByte;

        for (int i = 0; i < key.Length; i++)
        {
            int fieldLocation = VarInt.ReadVInt(Data, pointer);
            pointer += VarInt.SizeOfVInt(fieldLocation);

            key[i] = ReadPrimaryKeyField(fieldLocation + DataStartByte, fieldTypes[i])
                ?? throw new InvalidOperationException(
                    $"the primary key field {primaryKey.GetFieldPath(i)} of "
                    + $"{objectSchema.Name} is null, and a key field cannot be");
        }

        return new RecordPrimaryKey(objectSchema.Name, key);
    }

    private object? ReadPrimaryKeyField(int location, FieldType fieldType)
    {
        if (VarInt.ReadVNull(Data, location))
        {
            return null;
        }

        switch (fieldType)
        {
            case FieldType.Boolean:
                return Data.Get(location) == 1;

            case FieldType.Int:
                return ZigZag.DecodeInt(VarInt.ReadVInt(Data, location));

            case FieldType.Long:
                return ZigZag.DecodeLong(VarInt.ReadVLong(Data, location));

            case FieldType.Double:
                return BitConverter.Int64BitsToDouble(Data.ReadInt64Bits(location));

            case FieldType.Float:
                return BitConverter.Int32BitsToSingle(Data.ReadInt32Bits(location));

            case FieldType.Decimal:
                // This port's own field type, stored as the two longs the blob stores.
                return DecimalBits.Unpack(
                    Data.ReadInt64Bits(location), Data.ReadInt64Bits(location + sizeof(long)));

            case FieldType.String:
            {
                int length = VarInt.ReadVInt(Data, location);
                location += VarInt.SizeOfVInt(length);

                char[] characters = new char[VarInt.CountVarIntsInRange(Data, location, length)];
                VarInt.ReadVIntsInto(Data, location, length, characters);

                return new string(characters);
            }

            case FieldType.Bytes:
            {
                int length = VarInt.ReadVInt(Data, location);
                location += VarInt.SizeOfVInt(length);

                byte[] bytes = new byte[length];
                Data.CopyTo(location, bytes);

                return bytes;
            }

            default:
                throw new InvalidOperationException(
                    $"a primary key cannot have a {fieldType} field, so one should never have been written");
        }
    }
}

/// <summary>
/// How long a record, or one field of one, occupies in a flat record.
/// </summary>
/// <remarks>
/// Java's package-private <c>Sizing</c>. Everything in a flat record is variable-length, so the only
/// way past a record is through it, and both readers need the same arithmetic.
/// </remarks>
internal static class Sizing
{
    /// <summary>How many bytes the body of a <paramref name="schema"/> record occupies.</summary>
    internal static int SizeOfSchema(HollowSchema schema, FlatRecord record, int offset)
    {
        int start = offset;

        switch (schema.SchemaType)
        {
            case SchemaType.Object:
            {
                HollowObjectSchema objectSchema = (HollowObjectSchema)schema;

                for (int i = 0; i < objectSchema.FieldCount; i++)
                {
                    offset += SizeOfFieldValue(objectSchema.GetFieldType(i), record, offset);
                }

                break;
            }

            case SchemaType.List:
            case SchemaType.Set:
            {
                int size = VarInt.ReadVInt(record.Data, offset);
                offset += VarInt.SizeOfVInt(size);

                for (int i = 0; i < size; i++)
                {
                    offset += VarInt.NextVLongSize(record.Data, offset);
                }

                break;
            }

            case SchemaType.Map:
            {
                int size = VarInt.ReadVInt(record.Data, offset);
                offset += VarInt.SizeOfVInt(size);

                for (int i = 0; i < size; i++)
                {
                    offset += VarInt.NextVLongSize(record.Data, offset);
                    offset += VarInt.NextVLongSize(record.Data, offset);
                }

                break;
            }

            default:
                throw new InvalidOperationException($"unknown schema type {schema.SchemaType}");
        }

        return offset - start;
    }

    /// <summary>How many bytes one field of an object record occupies.</summary>
    internal static int SizeOfFieldValue(FieldType fieldType, FlatRecord record, int offset)
    {
        switch (fieldType)
        {
            case FieldType.Int:
            case FieldType.Long:
            case FieldType.Reference:
                return VarInt.NextVLongSize(record.Data, offset);

            case FieldType.Bytes:
            case FieldType.String:
            {
                if (VarInt.ReadVNull(record.Data, offset))
                {
                    return 1;
                }

                int length = VarInt.ReadVInt(record.Data, offset);

                return VarInt.SizeOfVInt(length) + length;
            }

            case FieldType.Boolean:
                return 1;

            case FieldType.Double:
                return sizeof(long);

            case FieldType.Float:
                return sizeof(int);

            case FieldType.Decimal:
                // Two longs: the unscaled value and the scale. See the format extension in PORTING.md.
                return sizeof(long) * 2;

            default:
                throw new InvalidOperationException($"unknown field type {fieldType}");
        }
    }
}
