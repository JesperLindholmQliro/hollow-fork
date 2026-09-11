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
using Hollow.Core.Read;
using Hollow.Core.Write;

namespace Hollow.Core.Schema;

/// <summary>
/// Defines the structure of a single named record type in a Hollow data model.
/// </summary>
/// <remarks>
/// A schema is one of:
/// <list type="table">
///   <item>
///     <term><see cref="HollowObjectSchema"/></term>
///     <description>A fixed set of strongly typed fields; see <see cref="FieldType"/>.</description>
///   </item>
///   <item>
///     <term><see cref="HollowListSchema"/></term>
///     <description>An ordered collection of records of a specific element type.</description>
///   </item>
///   <item>
///     <term><see cref="HollowSetSchema"/></term>
///     <description>An unordered collection of records of a specific element type, without duplicates.</description>
///   </item>
///   <item>
///     <term><see cref="HollowMapSchema"/></term>
///     <description>A key/value mapping between a specific key type and a specific value type.</description>
///   </item>
/// </list>
/// </remarks>
public abstract class HollowSchema
{
    /// <summary>
    /// Initialises a schema for the type named <paramref name="name"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    protected HollowSchema(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException(
                $"Type name in Hollow Schema was {(name is null ? "null" : "an empty string")}",
                nameof(name));
        }

        Name = name;
    }

    /// <summary>The name of the record type this schema describes.</summary>
    public string Name { get; }

    /// <summary>Which of the four kinds of schema this is.</summary>
    public abstract SchemaType SchemaType { get; }

    /// <summary>
    /// Writes this schema in the serialised form used by Hollow blobs.
    /// </summary>
    public abstract void WriteTo(HollowBlobOutput output);

    /// <summary>
    /// Returns <paramref name="schema"/> with any hash key removed, or <paramref name="schema"/>
    /// itself when it has none.
    /// </summary>
    public static HollowSchema WithoutKeys(HollowSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        switch (schema)
        {
            case HollowSetSchema { HashKey: not null } setSchema:
                return new HollowSetSchema(setSchema.Name, setSchema.ElementType);
            case HollowMapSchema { HashKey: not null } mapSchema:
                return new HollowMapSchema(mapSchema.Name, mapSchema.KeyType, mapSchema.ValueType);
            default:
                return schema;
        }
    }

    /// <summary>
    /// Reads a schema in the serialised form used by Hollow blobs.
    /// </summary>
    public static HollowSchema ReadFrom(HollowBlobInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        int schemaTypeId = input.Read();
        string schemaName = input.ReadUtf();

        return SchemaTypeExtensions.FromTypeId(schemaTypeId) switch
        {
            SchemaType.Object => ReadObjectSchemaFrom(input, schemaName, SchemaTypeExtensions.HasKey(schemaTypeId)),
            SchemaType.List => ReadListSchemaFrom(input, schemaName),
            SchemaType.Set => ReadSetSchemaFrom(input, schemaName, SchemaTypeExtensions.HasKey(schemaTypeId)),
            SchemaType.Map => ReadMapSchemaFrom(input, schemaName, SchemaTypeExtensions.HasKey(schemaTypeId)),
            _ => throw new InvalidDataException($"unrecognised schema type id {schemaTypeId}"),
        };
    }

    /// <summary>
    /// Reads a schema from a stream in the serialised form used by Hollow blobs.
    /// </summary>
    public static HollowSchema ReadFrom(Stream stream)
    {
        using HollowBlobInput input = HollowBlobInput.Serial(stream, leaveOpen: true);
        return ReadFrom(input);
    }

    /// <summary>
    /// Compares two possibly-null references for equality.
    /// </summary>
    /// <remarks>
    /// Java needs a helper for this; <see cref="EqualityComparer{T}.Default"/> already handles nulls,
    /// so this simply wraps it for the benefit of the ported call sites.
    /// </remarks>
    private protected static bool IsNullableObjectEquals<T>(T? first, T? second) =>
        EqualityComparer<T?>.Default.Equals(first, second);

    private static HollowObjectSchema ReadObjectSchemaFrom(
        HollowBlobInput input, string schemaName, bool hasPrimaryKey)
    {
        string[]? keyFieldPaths = hasPrimaryKey ? ReadFieldPaths(input) : null;

        int numFields = input.ReadInt16();
        HollowObjectSchema schema = new(schemaName, numFields, keyFieldPaths);

        for (int i = 0; i < numFields; i++)
        {
            string fieldName = input.ReadUtf();
            FieldType fieldType = FieldTypeExtensions.ParseWireName(input.ReadUtf());
            string? referencedType = fieldType == FieldType.Reference ? input.ReadUtf() : null;
            schema.AddField(fieldName, fieldType, referencedType);
        }

        return schema;
    }

    private static HollowSetSchema ReadSetSchemaFrom(HollowBlobInput input, string schemaName, bool hasHashKey)
    {
        string elementType = input.ReadUtf();
        string[]? hashKeyFields = hasHashKey ? ReadFieldPaths(input) : null;

        return new HollowSetSchema(schemaName, elementType, hashKeyFields);
    }

    private static HollowListSchema ReadListSchemaFrom(HollowBlobInput input, string schemaName) =>
        new(schemaName, input.ReadUtf());

    private static HollowMapSchema ReadMapSchemaFrom(HollowBlobInput input, string schemaName, bool hasHashKey)
    {
        string keyType = input.ReadUtf();
        string valueType = input.ReadUtf();
        string[]? hashKeyFields = hasHashKey ? ReadFieldPaths(input) : null;

        return new HollowMapSchema(schemaName, keyType, valueType, hashKeyFields);
    }

    private static string[] ReadFieldPaths(HollowBlobInput input)
    {
        int numFields = VarInt.ReadVInt(input);
        string[] fieldPaths = new string[numFields];
        for (int i = 0; i < numFields; i++)
        {
            fieldPaths[i] = input.ReadUtf();
        }

        return fieldPaths;
    }
}
