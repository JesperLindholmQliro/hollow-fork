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
using Hollow.Core.Util;

namespace Hollow.Core.Write.ObjectMapper.FlatRecords;

/// <summary>
/// Reads any record of a <see cref="FlatRecord"/> by its index, from any thread.
/// </summary>
/// <remarks>
/// <para>
/// A flat record is a run of variable-length records, so finding the tenth means walking the first
/// nine. This walks them all once, at construction, and remembers where each one starts; after that
/// every read is a lookup and some arithmetic, and no read touches state another thread might be
/// using.
/// </para>
/// <para>
/// A null field reads back as a sentinel rather than as nothing — <see cref="int.MinValue"/>,
/// <see cref="float.NaN"/>, <see langword="null"/> for a string. That is Java's shape and it is
/// lossy where a field may legitimately hold the sentinel; <see cref="IsNull"/> is the question to
/// ask when it matters.
/// </para>
/// </remarks>
public sealed class FlatRecordOrdinalReader
{
    private readonly FlatRecord _record;
    private readonly IntList _ordinalOffsets = new();

    /// <summary>Indexes <paramref name="record"/>, walking it once.</summary>
    public FlatRecordOrdinalReader(FlatRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        _record = record;

        int offset = record.DataStartByte;

        while (offset < record.DataEndByte)
        {
            _ordinalOffsets.Add(offset);
            offset += SizeOfOrdinal(_ordinalOffsets.Count - 1);
        }
    }

    /// <summary>How many records the flat record holds.</summary>
    public int OrdinalCount => _ordinalOffsets.Count;

    /// <summary>The schema of the record at <paramref name="ordinal"/>.</summary>
    public HollowSchema ReadSchema(int ordinal) =>
        _record.SchemaIdMapper.GetSchema(VarInt.ReadVInt(_record.Data, OffsetOf(ordinal)))
        ?? throw new InvalidOperationException(
            $"the schema identifier mapper does not know the schema of record {ordinal}");

    /// <summary>How many elements or entries a collection record holds.</summary>
    /// <exception cref="ArgumentException">It is not a collection record.</exception>
    public int ReadSize(int ordinal)
    {
        int offset = SkipSchemaId(ordinal, out HollowSchema schema);

        if (schema.SchemaType is not (SchemaType.List or SchemaType.Set or SchemaType.Map))
        {
            throw new ArgumentException(
                $"record {ordinal} is a {schema.SchemaType} record, which has no size", nameof(ordinal));
        }

        return VarInt.ReadVInt(_record.Data, offset);
    }

    /// <summary>Reads a list record's element indexes into <paramref name="elements"/>.</summary>
    /// <exception cref="ArgumentException">It is not a list record.</exception>
    public void ReadListElementsInto(int ordinal, Span<int> elements)
    {
        int offset = SkipSchemaId(ordinal, out HollowSchema schema);
        Expect(schema, SchemaType.List, ordinal);

        int size = VarInt.ReadVInt(_record.Data, offset);
        offset += VarInt.SizeOfVInt(size);

        for (int i = 0; i < size; i++)
        {
            elements[i] = VarInt.ReadVInt(_record.Data, offset);
            offset += VarInt.SizeOfVInt(elements[i]);
        }
    }

    /// <summary>Reads a set record's element indexes into <paramref name="elements"/>.</summary>
    /// <remarks>
    /// Stored as the gap from the last element rather than as the element itself, which is what makes
    /// a set of consecutive ordinals cost a byte each.
    /// </remarks>
    /// <exception cref="ArgumentException">It is not a set record.</exception>
    public void ReadSetElementsInto(int ordinal, Span<int> elements)
    {
        int offset = SkipSchemaId(ordinal, out HollowSchema schema);
        Expect(schema, SchemaType.Set, ordinal);

        int size = VarInt.ReadVInt(_record.Data, offset);
        offset += VarInt.SizeOfVInt(size);

        int element = 0;

        for (int i = 0; i < size; i++)
        {
            int delta = VarInt.ReadVInt(_record.Data, offset);
            offset += VarInt.SizeOfVInt(delta);
            element += delta;
            elements[i] = element;
        }
    }

    /// <summary>Reads a map record's key and value indexes.</summary>
    /// <remarks>Keys are gap-encoded, as a set's elements are; values are not.</remarks>
    /// <exception cref="ArgumentException">It is not a map record.</exception>
    public void ReadMapElementsInto(int ordinal, Span<int> keys, Span<int> values)
    {
        int offset = SkipSchemaId(ordinal, out HollowSchema schema);
        Expect(schema, SchemaType.Map, ordinal);

        int size = VarInt.ReadVInt(_record.Data, offset);
        offset += VarInt.SizeOfVInt(size);

        int key = 0;

        for (int i = 0; i < size; i++)
        {
            int delta = VarInt.ReadVInt(_record.Data, offset);
            offset += VarInt.SizeOfVInt(delta);
            key += delta;
            keys[i] = key;

            values[i] = VarInt.ReadVInt(_record.Data, offset);
            offset += VarInt.SizeOfVInt(values[i]);
        }
    }

    /// <summary>The index a reference field points at, or -1 where it is null or absent.</summary>
    public int ReadFieldReference(int ordinal, string field)
    {
        int offset = SkipToField(ordinal, FieldType.Reference, field);

        return offset == -1 || VarInt.ReadVNull(_record.Data, offset)
            ? -1
            : VarInt.ReadVInt(_record.Data, offset);
    }

    /// <summary>A boolean field, or null where it is null or absent.</summary>
    public bool? ReadFieldBoolean(int ordinal, string field)
    {
        int offset = SkipToField(ordinal, FieldType.Boolean, field);

        return offset == -1 || VarInt.ReadVNull(_record.Data, offset)
            ? null
            : _record.Data.Get(offset) == 1;
    }

    /// <summary>An int field, or <see cref="int.MinValue"/> where it is null or absent.</summary>
    public int ReadFieldInt(int ordinal, string field)
    {
        int offset = SkipToField(ordinal, FieldType.Int, field);

        return offset == -1 || VarInt.ReadVNull(_record.Data, offset)
            ? int.MinValue
            : ZigZag.DecodeInt(VarInt.ReadVInt(_record.Data, offset));
    }

    /// <summary>A long field, or <see cref="long.MinValue"/> where it is null or absent.</summary>
    public long ReadFieldLong(int ordinal, string field)
    {
        int offset = SkipToField(ordinal, FieldType.Long, field);

        return offset == -1 || VarInt.ReadVNull(_record.Data, offset)
            ? long.MinValue
            : ZigZag.DecodeLong(VarInt.ReadVLong(_record.Data, offset));
    }

    /// <summary>A float field, or <see cref="float.NaN"/> where it is null or absent.</summary>
    public float ReadFieldFloat(int ordinal, string field)
    {
        int offset = SkipToField(ordinal, FieldType.Float, field);

        if (offset == -1)
        {
            return float.NaN;
        }

        int bits = _record.Data.ReadInt32Bits(offset);

        return bits == HollowObjectWriteRecord.NullFloatBits ? float.NaN : BitConverter.Int32BitsToSingle(bits);
    }

    /// <summary>A double field, or <see cref="double.NaN"/> where it is null or absent.</summary>
    public double ReadFieldDouble(int ordinal, string field)
    {
        int offset = SkipToField(ordinal, FieldType.Double, field);

        if (offset == -1)
        {
            return double.NaN;
        }

        long bits = _record.Data.ReadInt64Bits(offset);

        return bits == HollowObjectWriteRecord.NullDoubleBits
            ? double.NaN
            : BitConverter.Int64BitsToDouble(bits);
    }

    /// <summary>
    /// A decimal field, or null where it is null or absent.
    /// </summary>
    /// <remarks>
    /// Java has no such field type — see the format extension section of <c>PORTING.md</c>. Unlike the
    /// other numeric reads it answers null rather than a sentinel, because every decimal is a value a
    /// field could legitimately hold.
    /// </remarks>
    public decimal? ReadFieldDecimal(int ordinal, string field)
    {
        int offset = SkipToField(ordinal, FieldType.Decimal, field);

        if (offset == -1)
        {
            return null;
        }

        long low = _record.Data.ReadInt64Bits(offset);
        long high = _record.Data.ReadInt64Bits(offset + sizeof(long));

        return low == DecimalBits.NullLow && high == DecimalBits.NullHigh
            ? null
            : DecimalBits.Unpack(low, high);
    }

    /// <summary>A string field, or null where it is null or absent.</summary>
    public string? ReadFieldString(int ordinal, string field)
    {
        int offset = SkipToField(ordinal, FieldType.String, field);

        if (offset == -1 || VarInt.ReadVNull(_record.Data, offset))
        {
            return null;
        }

        int length = VarInt.ReadVInt(_record.Data, offset);
        offset += VarInt.SizeOfVInt(length);

        char[] characters = new char[VarInt.CountVarIntsInRange(_record.Data, offset, length)];
        VarInt.ReadVIntsInto(_record.Data, offset, length, characters);

        return new string(characters);
    }

    /// <summary>A bytes field, or null where it is null or absent.</summary>
    public byte[]? ReadFieldBytes(int ordinal, string field)
    {
        int offset = SkipToField(ordinal, FieldType.Bytes, field);

        if (offset == -1 || VarInt.ReadVNull(_record.Data, offset))
        {
            return null;
        }

        int length = VarInt.ReadVInt(_record.Data, offset);
        offset += VarInt.SizeOfVInt(length);

        byte[] bytes = new byte[length];
        _record.Data.CopyTo(offset, bytes);

        return bytes;
    }

    /// <summary>
    /// Whether a field is null, which a field the record does not have also counts as.
    /// </summary>
    /// <exception cref="ArgumentException">The record is not an object record.</exception>
    public bool IsNull(int ordinal, string field)
    {
        HollowSchema schema = ReadSchema(ordinal);
        Expect(schema, SchemaType.Object, ordinal);

        HollowObjectSchema objectSchema = (HollowObjectSchema)schema;
        int position = objectSchema.GetPosition(field);

        if (position == -1)
        {
            return true;
        }

        FieldType fieldType = objectSchema.GetFieldType(position);
        int offset = SkipToField(ordinal, fieldType, field);

        if (offset == -1)
        {
            return true;
        }

        return fieldType switch
        {
            FieldType.Float => _record.Data.ReadInt32Bits(offset) == HollowObjectWriteRecord.NullFloatBits,
            FieldType.Double => _record.Data.ReadInt64Bits(offset) == HollowObjectWriteRecord.NullDoubleBits,
            FieldType.Decimal => _record.Data.ReadInt64Bits(offset) == DecimalBits.NullLow
                && _record.Data.ReadInt64Bits(offset + sizeof(long)) == DecimalBits.NullHigh,
            _ => VarInt.ReadVNull(_record.Data, offset),
        };
    }

    private static void Expect(HollowSchema schema, SchemaType expected, int ordinal)
    {
        if (schema.SchemaType != expected)
        {
            throw new ArgumentException(
                $"record {ordinal} is a {schema.SchemaType} record, not a {expected} one", nameof(ordinal));
        }
    }

    private int OffsetOf(int ordinal) => _ordinalOffsets.Get(ordinal);

    /// <summary>Steps past a record's schema id, handing back the schema and where the body starts.</summary>
    private int SkipSchemaId(int ordinal, out HollowSchema schema)
    {
        int offset = OffsetOf(ordinal);
        int schemaId = VarInt.ReadVInt(_record.Data, offset);

        schema = _record.SchemaIdMapper.GetSchema(schemaId)
            ?? throw new InvalidOperationException(
                $"the schema identifier mapper does not know schema {schemaId}");

        return offset + VarInt.SizeOfVInt(schemaId);
    }

    /// <summary>Where a field's value sits, or -1 where the record has no such field.</summary>
    private int SkipToField(int ordinal, FieldType fieldType, string field)
    {
        int offset = SkipSchemaId(ordinal, out HollowSchema schema);
        Expect(schema, SchemaType.Object, ordinal);

        HollowObjectSchema objectSchema = (HollowObjectSchema)schema;
        int fieldIndex = objectSchema.GetPosition(field);

        if (fieldIndex == -1)
        {
            return -1;
        }

        if (fieldType != objectSchema.GetFieldType(fieldIndex))
        {
            throw new ArgumentException(
                $"{field} is a {objectSchema.GetFieldType(fieldIndex)} field, not a {fieldType} one",
                nameof(field));
        }

        for (int i = 0; i < fieldIndex; i++)
        {
            offset += Sizing.SizeOfFieldValue(objectSchema.GetFieldType(i), _record, offset);
        }

        return offset;
    }

    private int SizeOfOrdinal(int ordinal)
    {
        int offset = OffsetOf(ordinal);
        int schemaId = VarInt.ReadVInt(_record.Data, offset);
        int schemaIdSize = VarInt.SizeOfVInt(schemaId);

        HollowSchema schema = _record.SchemaIdMapper.GetSchema(schemaId)
            ?? throw new InvalidOperationException(
                $"the schema identifier mapper does not know schema {schemaId}");

        return schemaIdSize + Sizing.SizeOfSchema(schema, _record, offset + schemaIdSize);
    }
}
