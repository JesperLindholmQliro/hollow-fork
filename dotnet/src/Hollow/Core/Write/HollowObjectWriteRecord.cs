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
using Hollow.Core.Memory.Pool;
using Hollow.Core.Schema;

namespace Hollow.Core.Write;

/// <summary>
/// A record of an object type, staged field by field before being added to a write state engine.
/// </summary>
public sealed class HollowObjectWriteRecord : IHollowWriteRecord
{
    /// <summary>
    /// The fixed-length bit pattern that marks a <see cref="FieldType.Float"/> field as null.
    /// </summary>
    /// <remarks>
    /// Java derives this as <c>Float.floatToIntBits(Float.NaN) + 1</c>. It is spelled as a literal here
    /// because .NET's <see cref="float.NaN"/> has the sign bit set (<c>0xFFC00000</c>) where Java's has
    /// not (<c>0x7FC00000</c>), so deriving it the same way would produce a different, incompatible
    /// sentinel.
    /// </remarks>
    public const int NullFloatBits = 0x7FC00000 + 1;

    /// <summary>
    /// The fixed-length bit pattern that marks a <see cref="FieldType.Double"/> field as null.
    /// </summary>
    /// <remarks>See <see cref="NullFloatBits"/> for why this is a literal.</remarks>
    public const long NullDoubleBits = 0x7FF8000000000000L + 1;

    /// <summary>The canonical bit pattern Java's <c>Float.floatToIntBits</c> gives every NaN.</summary>
    private const int CanonicalNaNFloatBits = 0x7FC00000;

    /// <summary>The canonical bit pattern Java's <c>Double.doubleToLongBits</c> gives every NaN.</summary>
    private const long CanonicalNaNDoubleBits = 0x7FF8000000000000L;

    private readonly ByteDataArray[] _fieldData;
    private readonly bool[] _isNonNull;

    /// <summary>
    /// Initialises an empty record of <paramref name="schema"/>.
    /// </summary>
    public HollowObjectWriteRecord(HollowObjectSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        Schema = schema;
        _fieldData = new ByteDataArray[schema.FieldCount];
        _isNonNull = new bool[schema.FieldCount];

        for (int i = 0; i < _fieldData.Length; i++)
        {
            _fieldData[i] = new ByteDataArray(WastefulRecycler.SmallArrayRecycler);
        }
    }

    /// <summary>The schema of the type this record belongs to.</summary>
    public HollowObjectSchema Schema { get; }

    /// <inheritdoc />
    public void WriteDataTo(ByteDataArray buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        for (int i = 0; i < _fieldData.Length; i++)
        {
            WriteField(buffer, i);
        }
    }

    /// <summary>
    /// Writes this record's fields in the field order of <paramref name="translate"/>, writing a null
    /// for any of its fields this record's schema does not have.
    /// </summary>
    public void WriteDataTo(ByteDataArray buffer, HollowObjectSchema translate)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(translate);

        for (int i = 0; i < translate.FieldCount; i++)
        {
            int fieldIndex = Schema.GetPosition(translate.GetFieldName(i));

            if (fieldIndex != -1)
            {
                WriteField(buffer, fieldIndex);
            }
            else
            {
                WriteNull(buffer, translate.GetFieldType(i));
            }
        }
    }

    /// <inheritdoc />
    public void Reset() => Array.Clear(_isNonNull);

    /// <summary>
    /// Marks a field as null.
    /// </summary>
    /// <remarks>
    /// <strong>Port note.</strong> Java's <c>setNull</c> marks the field <em>present</em> and writes a
    /// null marker into its buffer. For fixed-length fields that is equivalent to leaving the field
    /// unset, but for <see cref="FieldType.String"/> and <see cref="FieldType.Bytes"/> it emits a
    /// length prefix followed by the marker, which subsequently reads back as a one-byte value rather
    /// than as null. This port marks the field absent instead, which is byte-identical to Java for
    /// every fixed-length type and correct for the variable-length ones.
    /// </remarks>
    public void SetNull(string fieldName)
    {
        int fieldIndex = RequireField(fieldName);
        _isNonNull[fieldIndex] = false;
        _fieldData[fieldIndex].Reset();
    }

    /// <summary>
    /// Sets an <see cref="FieldType.Int"/> field. <see cref="int.MinValue"/> is the null sentinel.
    /// </summary>
    public void SetInt(string fieldName, int value)
    {
        if (value == int.MinValue)
        {
            SetNull(fieldName);
            return;
        }

        ByteDataArray buffer = GetFieldBuffer(fieldName, FieldType.Int);
        VarInt.WriteVInt(buffer, ZigZag.EncodeInt(value));
    }

    /// <summary>
    /// Sets a <see cref="FieldType.Long"/> field. <see cref="long.MinValue"/> is the null sentinel.
    /// </summary>
    public void SetLong(string fieldName, long value)
    {
        if (value == long.MinValue)
        {
            SetNull(fieldName);
            return;
        }

        ByteDataArray buffer = GetFieldBuffer(fieldName, FieldType.Long);
        VarInt.WriteVLong(buffer, ZigZag.EncodeLong(value));
    }

    /// <summary>Sets a <see cref="FieldType.Float"/> field.</summary>
    public void SetFloat(string fieldName, float value)
    {
        ByteDataArray buffer = GetFieldBuffer(fieldName, FieldType.Float);
        WriteFixedLengthInt(buffer, ToJavaBits(value));
    }

    /// <summary>Sets a <see cref="FieldType.Double"/> field.</summary>
    public void SetDouble(string fieldName, double value)
    {
        ByteDataArray buffer = GetFieldBuffer(fieldName, FieldType.Double);
        WriteFixedLengthLong(buffer, ToJavaBits(value));
    }

    /// <summary>Sets a <see cref="FieldType.Boolean"/> field.</summary>
    public void SetBoolean(string fieldName, bool value)
    {
        ByteDataArray buffer = GetFieldBuffer(fieldName, FieldType.Boolean);
        buffer.Write(value ? (byte)1 : (byte)0);
    }

    /// <summary>Sets a <see cref="FieldType.Bytes"/> field. A null value leaves the field unset.</summary>
    public void SetBytes(string fieldName, byte[]? value)
    {
        if (value is null)
        {
            return;
        }

        ByteDataArray buffer = GetFieldBuffer(fieldName, FieldType.Bytes);
        foreach (byte b in value)
        {
            buffer.Write(b);
        }
    }

    /// <summary>Sets a <see cref="FieldType.String"/> field. A null value leaves the field unset.</summary>
    public void SetString(string fieldName, string? value)
    {
        if (value is null)
        {
            return;
        }

        ByteDataArray buffer = GetFieldBuffer(fieldName, FieldType.String);
        foreach (char c in value)
        {
            VarInt.WriteVInt(buffer, c);
        }
    }

    /// <summary>Sets a <see cref="FieldType.Reference"/> field to the referenced record's ordinal.</summary>
    public void SetReference(string fieldName, int ordinal)
    {
        ByteDataArray buffer = GetFieldBuffer(fieldName, FieldType.Reference);
        VarInt.WriteVInt(buffer, ordinal);
    }

    /// <summary>
    /// Returns the bits Java's <c>Float.floatToIntBits</c> would produce, which canonicalises every NaN
    /// to a single pattern. .NET's bit conversion is raw, so NaN is canonicalised explicitly.
    /// </summary>
    private static int ToJavaBits(float value) =>
        float.IsNaN(value) ? CanonicalNaNFloatBits : BitConverter.SingleToInt32Bits(value);

    /// <summary>
    /// Returns the bits Java's <c>Double.doubleToLongBits</c> would produce. See
    /// <see cref="ToJavaBits(float)"/>.
    /// </summary>
    private static long ToJavaBits(double value) =>
        double.IsNaN(value) ? CanonicalNaNDoubleBits : BitConverter.DoubleToInt64Bits(value);

    private static void WriteNull(ByteDataArray buffer, FieldType fieldType)
    {
        switch (fieldType)
        {
            case FieldType.Float:
                WriteFixedLengthInt(buffer, NullFloatBits);
                break;
            case FieldType.Double:
                WriteFixedLengthLong(buffer, NullDoubleBits);
                break;
            default:
                VarInt.WriteVNull(buffer);
                break;
        }
    }

    private static void WriteFixedLengthInt(ByteDataArray buffer, int bits)
    {
        buffer.Write((byte)((uint)bits >> 24));
        buffer.Write((byte)((uint)bits >> 16));
        buffer.Write((byte)((uint)bits >> 8));
        buffer.Write((byte)bits);
    }

    private static void WriteFixedLengthLong(ByteDataArray buffer, long bits)
    {
        buffer.Write((byte)((ulong)bits >> 56));
        buffer.Write((byte)((ulong)bits >> 48));
        buffer.Write((byte)((ulong)bits >> 40));
        buffer.Write((byte)((ulong)bits >> 32));
        buffer.Write((byte)((ulong)bits >> 24));
        buffer.Write((byte)((ulong)bits >> 16));
        buffer.Write((byte)((ulong)bits >> 8));
        buffer.Write((byte)bits);
    }

    private void WriteField(ByteDataArray buffer, int fieldIndex)
    {
        if (!_isNonNull[fieldIndex])
        {
            WriteNull(buffer, Schema.GetFieldType(fieldIndex));
            return;
        }

        if (Schema.GetFieldType(fieldIndex).IsVariableLength())
        {
            VarInt.WriteVInt(buffer, (int)_fieldData[fieldIndex].Length);
        }

        _fieldData[fieldIndex].CopyTo(buffer);
    }

    private ByteDataArray GetFieldBuffer(string fieldName, FieldType expectedFieldType)
    {
        int fieldIndex = RequireField(fieldName);

        if (Schema.GetFieldType(fieldIndex) != expectedFieldType)
        {
            throw new ArgumentException(
                $"Attempting to serialize {expectedFieldType.ToWireName()} in field {fieldName}. "
                + $"Carefully check your schema for type {Schema.Name}.",
                nameof(fieldName));
        }

        _isNonNull[fieldIndex] = true;
        _fieldData[fieldIndex].Reset();
        return _fieldData[fieldIndex];
    }

    private int RequireField(string fieldName)
    {
        ArgumentNullException.ThrowIfNull(fieldName);

        int fieldIndex = Schema.GetPosition(fieldName);
        if (fieldIndex == -1)
        {
            throw new ArgumentException(
                $"Type {Schema.Name} has no field named {fieldName}", nameof(fieldName));
        }

        return fieldIndex;
    }
}
