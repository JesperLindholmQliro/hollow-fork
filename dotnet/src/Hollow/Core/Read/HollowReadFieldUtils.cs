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

using Hollow.Core.Memory.Encoding;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Schema;
using Hollow.Core.Util;

namespace Hollow.Core.Read;

/// <summary>
/// Hashes, compares and reads the fields of object records.
/// </summary>
/// <remarks>
/// The hash codes here are part of the index layout contract, so they reproduce Java's exactly rather
/// than using .NET's own <see cref="object.GetHashCode"/>.
/// </remarks>
public static class HollowReadFieldUtils
{
    /// <summary>
    /// Hashes the field at <paramref name="fieldPosition"/> of <paramref name="ordinal"/>'s record.
    /// </summary>
    /// <exception cref="InvalidOperationException">The field type cannot be hashed.</exception>
    public static int FieldHashCode(IHollowObjectTypeDataAccess typeAccess, int ordinal, int fieldPosition)
    {
        ArgumentNullException.ThrowIfNull(typeAccess);

        FieldType fieldType = typeAccess.Schema.GetFieldType(fieldPosition);

        return fieldType switch
        {
            FieldType.Boolean => BooleanHashCode(typeAccess.ReadBoolean(ordinal, fieldPosition)),
            FieldType.Bytes or FieldType.String =>
                typeAccess.FindVarLengthFieldHashCode(ordinal, fieldPosition),
            FieldType.Double => DoubleHashCode(typeAccess.ReadDouble(ordinal, fieldPosition)),
            FieldType.Float => FloatHashCode(typeAccess.ReadFloat(ordinal, fieldPosition)),
            FieldType.Int => IntHashCode(typeAccess.ReadInt(ordinal, fieldPosition)),
            FieldType.Long => LongHashCode(typeAccess.ReadLong(ordinal, fieldPosition)),
            FieldType.Reference => typeAccess.ReadOrdinal(ordinal, fieldPosition),
            FieldType.Decimal => DecimalHashCode(typeAccess.ReadDecimal(ordinal, fieldPosition)),
            _ => throw new InvalidOperationException($"cannot hash a {fieldType} field"),
        };
    }

    /// <summary>
    /// Hashes a boxed field value the same way <see cref="FieldHashCode"/> hashes the stored field.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a valid field value.</exception>
    public static int HashObject(object? value) =>
        value switch
        {
            null => 0,
            int i => IntHashCode(i),
            string s => StringHashCode(s),
            float f => FloatHashCode(f),
            double d => DoubleHashCode(d),
            bool b => BooleanHashCode(b),
            long l => LongHashCode(l),
            byte[] bytes => ByteArrayHashCode(bytes),
            decimal d => DecimalHashCode(d),
            _ => throw new ArgumentException($"cannot hash a field of type {value.GetType()}", nameof(value)),
        };

    /// <summary>
    /// Returns whether two object records' fields hold exactly the same value.
    /// </summary>
    /// <exception cref="InvalidOperationException">The field type cannot be compared.</exception>
    public static bool FieldsAreEqual(
        IHollowObjectTypeDataAccess typeAccess1,
        int ordinal1,
        int fieldPosition1,
        IHollowObjectTypeDataAccess typeAccess2,
        int ordinal2,
        int fieldPosition2)
    {
        ArgumentNullException.ThrowIfNull(typeAccess1);
        ArgumentNullException.ThrowIfNull(typeAccess2);

        FieldType fieldType = typeAccess1.Schema.GetFieldType(fieldPosition1);

        switch (fieldType)
        {
            case FieldType.Boolean:
                return typeAccess1.ReadBoolean(ordinal1, fieldPosition1)
                    == typeAccess2.ReadBoolean(ordinal2, fieldPosition2);

            case FieldType.Bytes:
                return ((ReadOnlySpan<byte>)typeAccess1.ReadBytes(ordinal1, fieldPosition1))
                    .SequenceEqual(typeAccess2.ReadBytes(ordinal2, fieldPosition2));

            case FieldType.Double:
                return typeAccess1.ReadDouble(ordinal1, fieldPosition1)
                    .CompareTo(typeAccess2.ReadDouble(ordinal2, fieldPosition2)) == 0;

            case FieldType.Float:
                return typeAccess1.ReadFloat(ordinal1, fieldPosition1)
                    .CompareTo(typeAccess2.ReadFloat(ordinal2, fieldPosition2)) == 0;

            case FieldType.Int:
                return typeAccess1.ReadInt(ordinal1, fieldPosition1)
                    == typeAccess2.ReadInt(ordinal2, fieldPosition2);

            case FieldType.Long:
                return typeAccess1.ReadLong(ordinal1, fieldPosition1)
                    == typeAccess2.ReadLong(ordinal2, fieldPosition2);

            case FieldType.String:
                return typeAccess2.IsStringFieldEqual(
                    ordinal2, fieldPosition2, typeAccess1.ReadString(ordinal1, fieldPosition1));

            // Compared by value rather than by stored form, so 1.50m equals 1.5m as it does in .NET.
            case FieldType.Decimal:
                return typeAccess1.ReadDecimal(ordinal1, fieldPosition1)
                    == typeAccess2.ReadDecimal(ordinal2, fieldPosition2);

            // Two ordinals are only comparable when they index the same type, which is guaranteed only
            // when both sides read the same field of the same type.
            case FieldType.Reference
                when ReferenceEquals(typeAccess1, typeAccess2) && fieldPosition1 == fieldPosition2:
                return typeAccess1.ReadOrdinal(ordinal1, fieldPosition1)
                    == typeAccess2.ReadOrdinal(ordinal2, fieldPosition2);

            default:
                throw new InvalidOperationException($"cannot test equality for a {fieldType} field");
        }
    }

    /// <summary>
    /// Reads the field at <paramref name="fieldPosition"/> as a boxed value, with a null field read as
    /// <see langword="null"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The field type cannot be read as a value.</exception>
    public static object? FieldValueObject(
        IHollowObjectTypeDataAccess typeAccess, int ordinal, int fieldPosition)
    {
        ArgumentNullException.ThrowIfNull(typeAccess);

        HollowObjectSchema schema = typeAccess.Schema;
        FieldType fieldType = schema.GetFieldType(fieldPosition);

        switch (fieldType)
        {
            case FieldType.Boolean:
                return typeAccess.ReadBoolean(ordinal, fieldPosition);

            case FieldType.Bytes:
                return typeAccess.ReadBytes(ordinal, fieldPosition);

            case FieldType.String:
                return typeAccess.ReadString(ordinal, fieldPosition);

            case FieldType.Double:
                double d = typeAccess.ReadDouble(ordinal, fieldPosition);
                return double.IsNaN(d) ? null : d;

            case FieldType.Float:
                float f = typeAccess.ReadFloat(ordinal, fieldPosition);
                return float.IsNaN(f) ? null : f;

            case FieldType.Int:
                int i = typeAccess.ReadInt(ordinal, fieldPosition);
                return i == int.MinValue ? null : i;

            case FieldType.Long:
                long l = typeAccess.ReadLong(ordinal, fieldPosition);
                return l == long.MinValue ? null : l;

            case FieldType.Decimal:
                return typeAccess.ReadDecimal(ordinal, fieldPosition);

            case FieldType.Reference:
                int referencedOrdinal = typeAccess.ReadOrdinal(ordinal, fieldPosition);
                return referencedOrdinal < 0 ? null : referencedOrdinal;

            default:
                throw new InvalidOperationException(
                    $"cannot read a {fieldType} field as a value (schema {schema.Name}, field {fieldPosition.Invariant()})");
        }
    }

    /// <summary>
    /// Returns whether a field holds the value <paramref name="testValue"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The field type cannot be compared.</exception>
    public static bool FieldValueEquals(
        IHollowObjectTypeDataAccess typeAccess, int ordinal, int fieldPosition, object? testValue)
    {
        ArgumentNullException.ThrowIfNull(typeAccess);

        FieldType fieldType = typeAccess.Schema.GetFieldType(fieldPosition);

        switch (fieldType)
        {
            case FieldType.Boolean:
                bool? storedBoolean = typeAccess.ReadBoolean(ordinal, fieldPosition);
                return testValue is bool b ? storedBoolean == b : testValue is null && storedBoolean is null;

            case FieldType.Bytes:
                byte[]? storedBytes = typeAccess.ReadBytes(ordinal, fieldPosition);
                return testValue is byte[] bytes
                    ? ((ReadOnlySpan<byte>)storedBytes).SequenceEqual(bytes)
                    : testValue is null && storedBytes is null;

            case FieldType.String:
                string? storedString = typeAccess.ReadString(ordinal, fieldPosition);
                return testValue is string s ? storedString == s : testValue is null && storedString is null;

            case FieldType.Double:
                double storedDouble = typeAccess.ReadDouble(ordinal, fieldPosition);
                return testValue is double d
                    ? storedDouble.Equals(d)
                    : testValue is null && double.IsNaN(storedDouble);

            case FieldType.Float:
                float storedFloat = typeAccess.ReadFloat(ordinal, fieldPosition);
                return testValue is float f
                    ? storedFloat.Equals(f)
                    : testValue is null && float.IsNaN(storedFloat);

            case FieldType.Int:
                int storedInt = typeAccess.ReadInt(ordinal, fieldPosition);
                return testValue is int i ? storedInt == i : testValue is null && storedInt == int.MinValue;

            case FieldType.Long:
                long storedLong = typeAccess.ReadLong(ordinal, fieldPosition);
                return testValue is long l ? storedLong == l : testValue is null && storedLong == long.MinValue;

            case FieldType.Decimal:
                decimal? storedDecimal = typeAccess.ReadDecimal(ordinal, fieldPosition);
                return testValue is decimal dec ? storedDecimal == dec : testValue is null && storedDecimal is null;

            case FieldType.Reference:
                int storedOrdinal = typeAccess.ReadOrdinal(ordinal, fieldPosition);
                return testValue is int referenced
                    ? storedOrdinal == referenced
                    : testValue is null && storedOrdinal < 0;

            default:
                throw new InvalidOperationException($"cannot test equality for a {fieldType} field");
        }
    }

    /// <summary>
    /// Renders a field as a displayable string.
    /// </summary>
    /// <exception cref="InvalidOperationException">The field type cannot be displayed.</exception>
    public static string? DisplayString(IHollowObjectTypeDataAccess typeAccess, int ordinal, int fieldPosition)
    {
        ArgumentNullException.ThrowIfNull(typeAccess);

        FieldType fieldType = typeAccess.Schema.GetFieldType(fieldPosition);

        return fieldType switch
        {
            FieldType.Boolean => typeAccess.ReadBoolean(ordinal, fieldPosition)?.ToString(),
            FieldType.Bytes or FieldType.String => typeAccess.ReadString(ordinal, fieldPosition),
            FieldType.Double => typeAccess.ReadDouble(ordinal, fieldPosition).Invariant(),
            FieldType.Float => typeAccess.ReadFloat(ordinal, fieldPosition).Invariant(),
            FieldType.Int => typeAccess.ReadInt(ordinal, fieldPosition).Invariant(),
            FieldType.Long => typeAccess.ReadLong(ordinal, fieldPosition).Invariant(),
            FieldType.Decimal => typeAccess.ReadDecimal(ordinal, fieldPosition)?.Invariant(),
            _ => throw new InvalidOperationException($"cannot display a {fieldType} field"),
        };
    }

    /// <summary>Hashes a byte array as <see cref="FieldHashCode"/> would hash the stored field.</summary>
    public static int ByteArrayHashCode(byte[] data) => HashCodes.Compute(data);

    /// <summary>Hashes a string as <see cref="FieldHashCode"/> would hash the stored field.</summary>
    public static int StringHashCode(string? value) => HashCodes.Compute(value);

    /// <summary>Hashes a boolean as <see cref="FieldHashCode"/> would hash the stored field.</summary>
    public static int BooleanHashCode(bool? value) => value is null ? -1 : value.Value ? 1 : 0;

    /// <summary>Hashes a long as <see cref="FieldHashCode"/> would hash the stored field.</summary>
    public static int LongHashCode(long value) => (int)value ^ (int)(value >> 32);

    /// <summary>Hashes an int as <see cref="FieldHashCode"/> would hash the stored field.</summary>
    public static int IntHashCode(int value) => value;

    /// <summary>Hashes a float as <see cref="FieldHashCode"/> would hash the stored field.</summary>
    /// <remarks>
    /// Java's <c>Float.floatToIntBits</c> collapses every NaN to one pattern; .NET's conversion is raw,
    /// so NaN is canonicalised explicitly.
    /// </remarks>
    public static int FloatHashCode(float value) =>
        float.IsNaN(value) ? 0x7FC00000 : BitConverter.SingleToInt32Bits(value);

    /// <summary>Hashes a double as <see cref="FieldHashCode"/> would hash the stored field.</summary>
    /// <remarks>
    /// Java's <c>Double.doubleToLongBits</c> collapses every NaN to one pattern; .NET's conversion is
    /// raw, so NaN is canonicalised explicitly.
    /// </remarks>
    public static int DoubleHashCode(double value) =>
        LongHashCode(double.IsNaN(value) ? 0x7FF8000000000000L : BitConverter.DoubleToInt64Bits(value));

    /// <summary>Hashes a decimal as <see cref="FieldHashCode"/> would hash the stored field.</summary>
    /// <remarks>
    /// <strong>Format extension.</strong> The hash ignores a decimal's scale, because .NET's own
    /// equality does — see <see cref="DecimalBits.CanonicalHashCode"/> and <c>PORTING.md</c>. A null
    /// decimal hashes to zero, matching what the producer's key hasher computes for one.
    /// </remarks>
    public static int DecimalHashCode(decimal? value) =>
        value is null ? 0 : DecimalBits.CanonicalHashCode(value.Value);
}
