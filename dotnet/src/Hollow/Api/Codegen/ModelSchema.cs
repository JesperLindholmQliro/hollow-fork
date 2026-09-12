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

using System.Text;

namespace Hollow.Api.Codegen;

/// <summary>
/// What a field of a generated object type holds.
/// </summary>
/// <remarks>
/// The same set as <c>Hollow.Core.Schema.FieldType</c>, declared again so the emitters depend on
/// nothing — see <see cref="ModelSchema"/> for why that matters.
/// </remarks>
internal enum ModelFieldType
{
    /// <summary>A pointer to a record of another type.</summary>
    Reference,

    /// <summary>A 32-bit integer.</summary>
    Int,

    /// <summary>A 64-bit integer.</summary>
    Long,

    /// <summary>A 32-bit float.</summary>
    Float,

    /// <summary>A 64-bit float.</summary>
    Double,

    /// <summary>A boolean.</summary>
    Boolean,

    /// <summary>A decimal — this port's format extension.</summary>
    Decimal,

    /// <summary>Variable-length text.</summary>
    String,

    /// <summary>Variable-length bytes.</summary>
    Bytes,
}

/// <summary>Which kind of record a type holds.</summary>
internal enum ModelSchemaKind
{
    /// <summary>A record of named fields.</summary>
    Object,

    /// <summary>An ordered sequence of elements.</summary>
    List,

    /// <summary>An unordered collection of distinct elements.</summary>
    Set,

    /// <summary>A collection of key-value entries.</summary>
    Map,
}

/// <summary>One field of an object type.</summary>
/// <param name="Name">The field's name.</param>
/// <param name="Type">What the field holds.</param>
/// <param name="ReferencedType">The type a reference field points at.</param>
internal sealed record ModelField(string Name, ModelFieldType Type, string? ReferencedType = null);

/// <summary>
/// One type of a data model, described in the least the emitters need.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately independent of <c>Hollow.Core.Schema</c>. The emitters are compiled into two
/// assemblies: this one, where the model comes from schemas the runtime already has, and the source
/// generator, which is a Roslyn analyser targeting <c>netstandard2.0</c> and so cannot reference the
/// runtime at all. A description this thin is what lets one set of emitters serve both.
/// </para>
/// <para>
/// The two producers of it — <see cref="HollowCodeGenerator"/> from a dataset, and the source
/// generator from Roslyn symbols — have to agree, since a client generated one way has to read a blob
/// written by a producer mapping the same model the other way. A test asserts they do.
/// </para>
/// </remarks>
internal abstract class ModelSchema
{
    private protected ModelSchema(string name)
    {
        Name = name;
    }

    /// <summary>The type's name in the dataset.</summary>
    internal string Name { get; }

    /// <summary>Which kind of record this type holds.</summary>
    internal abstract ModelSchemaKind Kind { get; }

    /// <summary>
    /// The schema in Hollow's own schema syntax.
    /// </summary>
    /// <remarks>
    /// Deliberately byte-for-byte what <c>Hollow.Core.Schema.HollowSchema.ToString()</c> produces for
    /// the same type, so the two derivations of a model — from loaded types and from Roslyn symbols —
    /// can be compared against each other directly. A test does exactly that.
    /// </remarks>
    public abstract override string ToString();

    /// <summary>A hash key rendered as schema syntax, or nothing where there is none.</summary>
    private protected static string HashKeySuffix(IReadOnlyList<string>? hashKeyFieldPaths) =>
        hashKeyFieldPaths is { Count: > 0 } key ? " @HashKey(" + string.Join(", ", key) + ")" : string.Empty;

    /// <summary>The name a field type has in schema syntax.</summary>
    private protected static string SchemaNameOf(ModelFieldType fieldType) =>
        fieldType switch
        {
            ModelFieldType.Reference => "reference",
            ModelFieldType.Int => "int",
            ModelFieldType.Long => "long",
            ModelFieldType.Boolean => "boolean",
            ModelFieldType.Float => "float",
            ModelFieldType.Double => "double",
            ModelFieldType.String => "string",
            ModelFieldType.Bytes => "bytes",
            ModelFieldType.Decimal => "decimal",
            _ => throw new ArgumentOutOfRangeException(nameof(fieldType), fieldType, "unknown field type"),
        };
}

/// <summary>An object type, and the fields it declares.</summary>
internal sealed class ModelObjectSchema(
    string name, IReadOnlyList<ModelField> fields, IReadOnlyList<string>? primaryKeyFieldPaths = null)
    : ModelSchema(name)
{
    /// <inheritdoc />
    internal override ModelSchemaKind Kind => ModelSchemaKind.Object;

    /// <summary>The fields, in schema order.</summary>
    internal IReadOnlyList<ModelField> Fields { get; } = fields;

    /// <summary>The declared primary key's field paths, if it declares one.</summary>
    internal IReadOnlyList<string>? PrimaryKeyFieldPaths { get; } = primaryKeyFieldPaths;

    /// <inheritdoc />
    public override string ToString()
    {
        StringBuilder schema = new(Name);

        if (PrimaryKeyFieldPaths is { Count: > 0 } key)
        {
            schema.Append(" @PrimaryKey(").Append(string.Join(", ", key)).Append(')');
        }

        schema.Append(" {\n");

        foreach (ModelField field in Fields)
        {
            schema.Append('\t')
                .Append(field.Type == ModelFieldType.Reference
                    ? field.ReferencedType
                    : SchemaNameOf(field.Type))
                .Append(' ')
                .Append(field.Name)
                .Append(";\n");
        }

        return schema.Append('}').ToString();
    }
}

/// <summary>A list type.</summary>
internal sealed class ModelListSchema(string name, string elementType) : ModelSchema(name)
{
    /// <inheritdoc />
    internal override ModelSchemaKind Kind => ModelSchemaKind.List;

    /// <summary>The type of this list's elements.</summary>
    internal string ElementType { get; } = elementType;

    /// <inheritdoc />
    public override string ToString() => $"{Name} List<{ElementType}>;";
}

/// <summary>A set type.</summary>
internal sealed class ModelSetSchema(
    string name, string elementType, IReadOnlyList<string>? hashKeyFieldPaths = null)
    : ModelSchema(name)
{
    /// <inheritdoc />
    internal override ModelSchemaKind Kind => ModelSchemaKind.Set;

    /// <summary>The type of this set's elements.</summary>
    internal string ElementType { get; } = elementType;

    /// <summary>
    /// The hash key its elements are laid out by, where it has one.
    /// </summary>
    /// <remarks>
    /// Nothing the emitters produce depends on this — it is a write-side layout decision. It is
    /// described anyway so the two derivations of a model can be compared in full rather than only in
    /// the part that happens to matter today.
    /// </remarks>
    internal IReadOnlyList<string>? HashKeyFieldPaths { get; } = hashKeyFieldPaths;

    /// <inheritdoc />
    public override string ToString() => $"{Name} Set<{ElementType}>{HashKeySuffix(HashKeyFieldPaths)};";
}

/// <summary>A map type.</summary>
internal sealed class ModelMapSchema(
    string name, string keyType, string valueType, IReadOnlyList<string>? hashKeyFieldPaths = null)
    : ModelSchema(name)
{
    /// <inheritdoc />
    internal override ModelSchemaKind Kind => ModelSchemaKind.Map;

    /// <summary>The type of this map's keys.</summary>
    internal string KeyType { get; } = keyType;

    /// <summary>The type of this map's values.</summary>
    internal string ValueType { get; } = valueType;

    /// <summary>The hash key its keys are laid out by, where it has one.</summary>
    internal IReadOnlyList<string>? HashKeyFieldPaths { get; } = hashKeyFieldPaths;

    /// <inheritdoc />
    public override string ToString() =>
        $"{Name} Map<{KeyType},{ValueType}>{HashKeySuffix(HashKeyFieldPaths)};";
}

/// <summary>
/// What the emitters need to know about the output, independent of how the model was found.
/// </summary>
/// <remarks>
/// <see cref="HollowCodeGeneratorOptions"/> is the public form of this, for callers of the text
/// emitter; the source generator builds one of these straight from the marker attribute.
/// </remarks>
internal sealed class EmitterOptions
{
    /// <summary>The namespace the generated types are declared in.</summary>
    internal required string Namespace { get; init; }

    /// <summary>The name of the generated API class.</summary>
    internal required string ApiClassName { get; init; }

    /// <summary>The types the generated API caches when it is told to cache none explicitly.</summary>
    internal IReadOnlyCollection<string> DefaultCachedTypes { get; init; } = [];

    /// <summary>
    /// Whether a field referencing a type with one value field reads as that value.
    /// </summary>
    internal bool UseErgonomicShortcuts { get; init; } = true;

    /// <summary>Whether to emit a unique-key index for each type declaring a primary key.</summary>
    internal bool GenerateUniqueKeyIndexes { get; init; } = true;

    /// <summary>Whether to emit a cached delegate per object type.</summary>
    internal bool GenerateCachedDelegates { get; init; } = true;
}
