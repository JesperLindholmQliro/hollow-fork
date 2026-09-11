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

namespace Hollow.Core.Schema;

/// <summary>
/// The allowable field types of a <see cref="HollowObjectSchema"/>.
/// </summary>
/// <remarks>
/// Java nests this as <c>HollowObjectSchema.FieldType</c> and gives it two instance fields. C# enums
/// carry no state and nesting a public enum inside a class is discouraged, so it is a top-level enum
/// here with its per-member data exposed through <see cref="FieldTypeExtensions"/>.
/// </remarks>
public enum FieldType
{
    /// <summary>
    /// A reference to another record. References are typed fixed-length fields encoded as the ordinal
    /// of the referenced record.
    /// </summary>
    Reference,

    /// <summary>
    /// An integer value of up to 32 bits, encoded as a fixed-length zig-zag field.
    /// <see cref="int.MinValue"/> is reserved as the sentinel for null.
    /// </summary>
    Int,

    /// <summary>
    /// An integer value of up to 64 bits, encoded as a fixed-length zig-zag field.
    /// <see cref="long.MinValue"/> is reserved as the sentinel for null.
    /// </summary>
    Long,

    /// <summary>
    /// A boolean value, encoded in two bits because the field may be true, false, or null.
    /// </summary>
    Boolean,

    /// <summary>A single-precision floating-point number, encoded as a fixed-length four-byte field.</summary>
    Float,

    /// <summary>A double-precision floating-point number, encoded as a fixed-length eight-byte field.</summary>
    Double,

    /// <summary>
    /// A string. Every string for a given field is stored in one packed array of variable-length
    /// characters, ordered by the ordinal of the owning record. Each record holds a fixed-length
    /// pointer to the end of its range; the start of the range is the previous record's pointer.
    /// </summary>
    String,

    /// <summary>
    /// A byte array, stored the same way as <see cref="String"/>: one packed array per field, with each
    /// record holding a fixed-length pointer to the end of its range.
    /// </summary>
    Bytes,

    /// <summary>
    /// A .NET <see cref="decimal"/>, encoded as a fixed-length sixteen-byte field holding the four
    /// integers <see cref="decimal.GetBits(decimal)"/> returns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is an extension to the Hollow blob format, not part of Netflix Hollow.</strong> A
    /// Java Hollow consumer cannot read a blob whose schema declares a field of this type, and this
    /// port's own compatibility rule is that a dataset which uses no <c>Decimal</c> field must remain
    /// byte-identical to what Netflix Hollow would produce. See the "Format extension: the Decimal
    /// field type" section of <c>PORTING.md</c> before changing anything about it.
    /// </para>
    /// <para>
    /// It is the only field type wider than 64 bits, so its value is read and written as two 64-bit
    /// halves rather than as a single element.
    /// </para>
    /// </remarks>
    Decimal,
}

/// <summary>
/// The per-member data Java attaches to <c>FieldType</c> enum constants, plus the conversions between
/// a <see cref="FieldType"/> and its serialised name.
/// </summary>
public static class FieldTypeExtensions
{
    /// <summary>
    /// The fixed width of this field in bytes, or -1 when the width depends on the data.
    /// </summary>
    public static int GetFixedLength(this FieldType fieldType) => fieldType switch
    {
        FieldType.Boolean => 1,
        FieldType.Float => 4,
        FieldType.Double => 8,
        FieldType.Decimal => 16,
        _ => -1,
    };

    /// <summary>
    /// Whether values of this field type are stored in the variable-length portion of a record, with a
    /// variable-length integer encoding their length.
    /// </summary>
    public static bool IsVariableLength(this FieldType fieldType) =>
        fieldType is FieldType.String or FieldType.Bytes;

    /// <summary>
    /// The name used for this field type in serialised schemas.
    /// </summary>
    /// <remarks>
    /// Java writes <c>FieldType.name()</c>, which is the constant's upper-case name. The .NET names are
    /// PascalCase, so they are mapped explicitly rather than derived — the serialised form is part of
    /// the blob contract.
    /// </remarks>
    public static string ToWireName(this FieldType fieldType) => fieldType switch
    {
        FieldType.Reference => "REFERENCE",
        FieldType.Int => "INT",
        FieldType.Long => "LONG",
        FieldType.Boolean => "BOOLEAN",
        FieldType.Float => "FLOAT",
        FieldType.Double => "DOUBLE",
        FieldType.String => "STRING",
        FieldType.Bytes => "BYTES",
        FieldType.Decimal => "DECIMAL",
        _ => throw new ArgumentOutOfRangeException(nameof(fieldType), fieldType, "unknown field type"),
    };

    /// <summary>
    /// The name used for this field type in the textual schema syntax.
    /// </summary>
    public static string ToSchemaName(this FieldType fieldType) => fieldType switch
    {
        FieldType.Reference => "reference",
        FieldType.Int => "int",
        FieldType.Long => "long",
        FieldType.Boolean => "boolean",
        FieldType.Float => "float",
        FieldType.Double => "double",
        FieldType.String => "string",
        FieldType.Bytes => "bytes",
        FieldType.Decimal => "decimal",
        _ => throw new ArgumentOutOfRangeException(nameof(fieldType), fieldType, "unknown field type"),
    };

    /// <summary>
    /// Parses a serialised field type name, accepting either the wire form (<c>INT</c>) or the schema
    /// syntax form (<c>int</c>).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> names no known field type.</exception>
    public static FieldType ParseWireName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.ToUpperInvariant() switch
        {
            "REFERENCE" => FieldType.Reference,
            "INT" => FieldType.Int,
            "LONG" => FieldType.Long,
            "BOOLEAN" => FieldType.Boolean,
            "FLOAT" => FieldType.Float,
            "DOUBLE" => FieldType.Double,
            "STRING" => FieldType.String,
            "BYTES" => FieldType.Bytes,
            "DECIMAL" => FieldType.Decimal,
            _ => throw new ArgumentException($"unknown field type '{name}'", nameof(name)),
        };
    }

    /// <summary>
    /// Parses a field type name, returning <see langword="false"/> rather than throwing when the name
    /// is not a known field type.
    /// </summary>
    public static bool TryParseWireName(string name, out FieldType fieldType)
    {
        switch (name?.ToUpperInvariant())
        {
            case "REFERENCE": fieldType = FieldType.Reference; return true;
            case "INT": fieldType = FieldType.Int; return true;
            case "LONG": fieldType = FieldType.Long; return true;
            case "BOOLEAN": fieldType = FieldType.Boolean; return true;
            case "FLOAT": fieldType = FieldType.Float; return true;
            case "DOUBLE": fieldType = FieldType.Double; return true;
            case "STRING": fieldType = FieldType.String; return true;
            case "BYTES": fieldType = FieldType.Bytes; return true;
            case "DECIMAL": fieldType = FieldType.Decimal; return true;
            default: fieldType = default; return false;
        }
    }
}
