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

using Hollow.Core;
using Hollow.Core.Schema;

namespace Hollow.Api.Codegen;

/// <summary>
/// A field of a generated object type, with the names and types the emitters need.
/// </summary>
/// <param name="Name">The field's name in the schema.</param>
/// <param name="Position">The field's position in the generated API's field list.</param>
/// <param name="Type">The field's type in the schema.</param>
/// <param name="ReferencedType">The type a reference field points at.</param>
/// <param name="PropertyName">The name of the generated property.</param>
internal sealed record GeneratedField(
    string Name, int Position, FieldType Type, string? ReferencedType, string PropertyName)
{
    /// <summary>Whether this field points at another record rather than holding a value.</summary>
    internal bool IsReference => Type == FieldType.Reference;
}

/// <summary>
/// One type of the data model, resolved to the names its generated code uses.
/// </summary>
internal sealed class GeneratedType
{
    internal required HollowSchema Schema { get; init; }

    /// <summary>The type's name in the dataset.</summary>
    internal required string TypeName { get; init; }

    /// <summary>The record wrapper's class name, which may be a runtime type for a scalar.</summary>
    internal required string RecordType { get; init; }

    /// <summary>
    /// The scalar wrapper this type is read as, or <see langword="null"/> when a wrapper is generated
    /// for it.
    /// </summary>
    /// <remarks>
    /// Hollow's built-in <c>String</c>, <c>Integer</c> and friends are already in
    /// <c>Hollow.Core.Types</c>; generating another set per model would be six more classes saying the
    /// same thing.
    /// </remarks>
    internal BuiltInScalar? BuiltIn { get; init; }

    /// <summary>
    /// The value type this type reads as when a field referencing it takes the ergonomic shortcut, or
    /// <see langword="null"/> when it has no single value field.
    /// </summary>
    internal string? ShortcutValueType { get; init; }

    /// <summary>The name of the single value field, where there is one.</summary>
    internal string? ShortcutFieldName { get; init; }

    internal IReadOnlyList<GeneratedField> Fields { get; init; } = [];

    /// <summary>The element type of a list or set.</summary>
    internal string? ElementType { get; init; }

    /// <summary>The key type of a map.</summary>
    internal string? KeyType { get; init; }

    /// <summary>The value type of a map.</summary>
    internal string? ValueType { get; init; }

    internal SchemaType Kind => Schema.SchemaType;

    /// <summary>Whether the generator emits a wrapper class for this type.</summary>
    internal bool IsGenerated => BuiltIn is null;
}

/// <summary>
/// One of Hollow's built-in scalar wrapper types, named as <c>Hollow.Core.Types</c> declares it.
/// </summary>
/// <remarks>
/// This table has to agree with <c>HollowScalarTypes.Instantiate</c> and
/// <c>HollowScalarTypes.TypeApiFor</c>, which pick the same classes at run time; a test asserts they
/// do.
/// </remarks>
/// <param name="TypeName">The Hollow type name.</param>
/// <param name="RecordType">The wrapper class.</param>
/// <param name="TypeApiType">The type API class.</param>
/// <param name="ValueType">The CLR type its value reads as.</param>
/// <param name="FieldType">The field type its single field has.</param>
internal sealed record BuiltInScalar(
    string TypeName, string RecordType, string TypeApiType, string ValueType, FieldType FieldType)
{
    internal static readonly IReadOnlyList<BuiltInScalar> All =
    [
        new("String", "HString", "HStringTypeApi", "string?", FieldType.String),
        new("Integer", "HInteger", "HIntegerTypeApi", "int?", FieldType.Int),
        new("Long", "HLong", "HLongTypeApi", "long?", FieldType.Long),
        new("Double", "HDouble", "HDoubleTypeApi", "double?", FieldType.Double),
        new("Float", "HFloat", "HFloatTypeApi", "float?", FieldType.Float),
        new("Boolean", "HBoolean", "HBooleanTypeApi", "bool?", FieldType.Boolean),
        new("Decimal", "HDecimal", "HDecimalTypeApi", "decimal?", FieldType.Decimal),
    ];

    /// <summary>
    /// The built-in wrapper for <paramref name="schema"/>, or <see langword="null"/> if it is not one.
    /// </summary>
    internal static BuiltInScalar? For(HollowSchema schema) =>
        schema is HollowObjectSchema { FieldCount: 1 } objectSchema
            ? All.FirstOrDefault(
                scalar => scalar.TypeName == schema.Name && scalar.FieldType == objectSchema.GetFieldType(0))
            : null;
}

/// <summary>
/// The whole data model, resolved once so the emitters can look a type up by name.
/// </summary>
internal sealed class GeneratedModel
{
    private readonly Dictionary<string, GeneratedType> _byTypeName = new(StringComparer.Ordinal);

    private GeneratedModel(IReadOnlyList<GeneratedType> types)
    {
        Types = types;

        foreach (GeneratedType type in types)
        {
            _byTypeName[type.TypeName] = type;
        }
    }

    internal IReadOnlyList<GeneratedType> Types { get; }

    /// <summary>Resolves every schema of <paramref name="dataset"/>, in name order.</summary>
    internal static GeneratedModel From(IHollowDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        List<HollowSchema> schemas = [.. dataset.Schemas.OrderBy(schema => schema.Name, StringComparer.Ordinal)];

        return new GeneratedModel([.. schemas.Select(Describe)]);
    }

    /// <summary>The resolved type named <paramref name="typeName"/>, if the model has it.</summary>
    internal GeneratedType? Find(string typeName) =>
        _byTypeName.TryGetValue(typeName, out GeneratedType? type) ? type : null;

    /// <summary>
    /// The C# type a reference to <paramref name="typeName"/> reads as, defaulting to a generic record
    /// for a type outside the model.
    /// </summary>
    internal string RecordTypeOf(string? typeName) =>
        typeName is not null && Find(typeName) is { } type ? type.RecordType : "IHollowRecord";

    private static GeneratedType Describe(HollowSchema schema)
    {
        BuiltInScalar? builtIn = BuiltInScalar.For(schema);

        return schema switch
        {
            HollowObjectSchema objectSchema => new GeneratedType
            {
                Schema = objectSchema,
                TypeName = objectSchema.Name,
                RecordType = builtIn?.RecordType ?? CodeNames.RecordType(objectSchema.Name),
                BuiltIn = builtIn,
                ShortcutValueType = ShortcutValueTypeOf(objectSchema),
                ShortcutFieldName = ShortcutValueTypeOf(objectSchema) is null
                    ? null
                    : objectSchema.GetFieldName(0),
                Fields =
                [
                    .. Enumerable.Range(0, objectSchema.FieldCount)
                        .Select(index => new GeneratedField(
                            objectSchema.GetFieldName(index),
                            index,
                            objectSchema.GetFieldType(index),
                            objectSchema.GetReferencedType(index),
                            CodeNames.Property(objectSchema.Name, objectSchema.GetFieldName(index)))),
                ],
            },

            HollowListSchema listSchema => new GeneratedType
            {
                Schema = listSchema,
                TypeName = listSchema.Name,
                RecordType = CodeNames.RecordType(listSchema.Name),
                ElementType = listSchema.ElementType,
            },

            HollowSetSchema setSchema => new GeneratedType
            {
                Schema = setSchema,
                TypeName = setSchema.Name,
                RecordType = CodeNames.RecordType(setSchema.Name),
                ElementType = setSchema.ElementType,
            },

            HollowMapSchema mapSchema => new GeneratedType
            {
                Schema = mapSchema,
                TypeName = mapSchema.Name,
                RecordType = CodeNames.RecordType(mapSchema.Name),
                KeyType = mapSchema.KeyType,
                ValueType = mapSchema.ValueType,
            },

            _ => throw new ArgumentOutOfRangeException(
                nameof(schema), schema.SchemaType, "unknown record kind"),
        };
    }

    /// <summary>
    /// The CLR type a reference to this schema collapses to under the ergonomic shortcut, or
    /// <see langword="null"/> where it does not collapse.
    /// </summary>
    private static string? ShortcutValueTypeOf(HollowObjectSchema schema)
    {
        if (schema.FieldCount != 1 || schema.GetFieldType(0) == FieldType.Reference)
        {
            return null;
        }

        // Always nullable: the reference taking the shortcut may itself be null, quite apart from
        // whether the value behind it is.
        string valueType = CodeNames.ValueTypeOf(schema.GetFieldType(0));

        return valueType.EndsWith('?') || valueType.EndsWith("[]", StringComparison.Ordinal)
            ? valueType
            : valueType + "?";
    }
}
