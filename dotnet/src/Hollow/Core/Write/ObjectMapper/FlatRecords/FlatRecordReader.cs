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

namespace Hollow.Core.Write.ObjectMapper.FlatRecords;

/// <summary>
/// Walks a <see cref="FlatRecord"/> from front to back, one field at a time.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <see cref="FlatRecordOrdinalReader"/>: that one indexes the record so any part
/// of it can be read from any thread, this one keeps a pointer and costs nothing up front. Reading a
/// whole record once is what it is for, and it is the caller's job to read the fields in the order the
/// schema declares them.
/// </para>
/// <para>
/// Not thread-safe: the pointer is the state.
/// </para>
/// </remarks>
public sealed class FlatRecordReader
{
    private readonly FlatRecord _record;

    /// <summary>Reads <paramref name="record"/> from its first record.</summary>
    public FlatRecordReader(FlatRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        _record = record;
        Pointer = record.DataStartByte;
    }

    /// <summary>Where in the record the next read will start.</summary>
    /// <remarks>
    /// Settable, which is how a caller goes back to somewhere it noted earlier — Java's
    /// <c>resetTo</c>.
    /// </remarks>
    public int Pointer { get; set; }

    /// <summary>Whether there is another record to read.</summary>
    public bool HasMore => Pointer < _record.DataEndByte;

    /// <summary>Goes back to the first record.</summary>
    public void Reset() => Pointer = _record.DataStartByte;

    /// <summary>Reads the schema at the pointer and steps past it.</summary>
    public HollowSchema ReadSchema()
    {
        int schemaId = VarInt.ReadVInt(_record.Data, Pointer);
        Pointer += VarInt.SizeOfVInt(schemaId);

        return _record.SchemaIdMapper.GetSchema(schemaId)
            ?? throw new InvalidOperationException(
                $"the schema identifier mapper does not know schema {schemaId}");
    }

    /// <summary>Reads how many elements or entries a collection record holds.</summary>
    public int ReadCollectionSize()
    {
        int size = VarInt.ReadVInt(_record.Data, Pointer);
        Pointer += VarInt.SizeOfVInt(size);

        return size;
    }

    /// <summary>Reads the index a reference holds, or -1 where it is null.</summary>
    public int ReadOrdinal()
    {
        if (VarInt.ReadVNull(_record.Data, Pointer))
        {
            Pointer++;

            return -1;
        }

        int value = VarInt.ReadVInt(_record.Data, Pointer);
        Pointer += VarInt.SizeOfVInt(value);

        return value;
    }

    /// <summary>Reads a boolean field.</summary>
    public bool? ReadBoolean()
    {
        if (VarInt.ReadVNull(_record.Data, Pointer))
        {
            Pointer++;

            return null;
        }

        return _record.Data.Get(Pointer++) == 1;
    }

    /// <summary>Reads an int field, or <see cref="int.MinValue"/> where it is null.</summary>
    public int ReadInt()
    {
        if (VarInt.ReadVNull(_record.Data, Pointer))
        {
            Pointer++;

            return int.MinValue;
        }

        int value = VarInt.ReadVInt(_record.Data, Pointer);
        Pointer += VarInt.SizeOfVInt(value);

        return ZigZag.DecodeInt(value);
    }

    /// <summary>Reads a long field, or <see cref="long.MinValue"/> where it is null.</summary>
    public long ReadLong()
    {
        if (VarInt.ReadVNull(_record.Data, Pointer))
        {
            Pointer++;

            return long.MinValue;
        }

        long value = VarInt.ReadVLong(_record.Data, Pointer);
        Pointer += VarInt.SizeOfVLong(value);

        return ZigZag.DecodeLong(value);
    }

    /// <summary>Reads a float field, or <see cref="float.NaN"/> where it is null.</summary>
    public float ReadFloat()
    {
        int bits = _record.Data.ReadInt32Bits(Pointer);
        Pointer += sizeof(int);

        return bits == HollowObjectWriteRecord.NullFloatBits
            ? float.NaN
            : BitConverter.Int32BitsToSingle(bits);
    }

    /// <summary>Reads a double field, or <see cref="double.NaN"/> where it is null.</summary>
    public double ReadDouble()
    {
        long bits = _record.Data.ReadInt64Bits(Pointer);
        Pointer += sizeof(long);

        return bits == HollowObjectWriteRecord.NullDoubleBits
            ? double.NaN
            : BitConverter.Int64BitsToDouble(bits);
    }

    /// <summary>
    /// Reads a decimal field.
    /// </summary>
    /// <remarks>
    /// This port's own field type — see the format extension section of <c>PORTING.md</c>. Null is
    /// null rather than a sentinel, because every decimal is a value a field could hold.
    /// </remarks>
    public decimal? ReadDecimal()
    {
        long low = _record.Data.ReadInt64Bits(Pointer);
        long high = _record.Data.ReadInt64Bits(Pointer + sizeof(long));
        Pointer += sizeof(long) * 2;

        return low == DecimalBits.NullLow && high == DecimalBits.NullHigh
            ? null
            : DecimalBits.Unpack(low, high);
    }

    /// <summary>Reads a string field.</summary>
    public string? ReadString()
    {
        if (VarInt.ReadVNull(_record.Data, Pointer))
        {
            Pointer++;

            return null;
        }

        int length = VarInt.ReadVInt(_record.Data, Pointer);
        Pointer += VarInt.SizeOfVInt(length);

        char[] characters = new char[VarInt.CountVarIntsInRange(_record.Data, Pointer, length)];
        VarInt.ReadVIntsInto(_record.Data, Pointer, length, characters);
        Pointer += length;

        return new string(characters);
    }

    /// <summary>Reads a bytes field.</summary>
    public byte[]? ReadBytes()
    {
        if (VarInt.ReadVNull(_record.Data, Pointer))
        {
            Pointer++;

            return null;
        }

        int length = VarInt.ReadVInt(_record.Data, Pointer);
        Pointer += VarInt.SizeOfVInt(length);

        byte[] bytes = new byte[length];
        _record.Data.CopyTo(Pointer, bytes);
        Pointer += length;

        return bytes;
    }

    /// <summary>Steps over a whole record of <paramref name="schema"/> without reading it.</summary>
    public void SkipSchema(HollowSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        Pointer += Sizing.SizeOfSchema(schema, _record, Pointer);
    }

    /// <summary>Steps over one field without reading it.</summary>
    public void SkipField(FieldType fieldType) =>
        Pointer += Sizing.SizeOfFieldValue(fieldType, _record, Pointer);
}
