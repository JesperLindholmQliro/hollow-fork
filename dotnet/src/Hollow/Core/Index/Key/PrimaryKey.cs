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

using Hollow.Core.Schema;

namespace Hollow.Core.Index.Key;

/// <summary>
/// Defines a set of one or more fields which should be unique for each record of a specific type.
/// </summary>
/// <remarks>
/// <para>
/// Field definitions may be hierarchical, traversing multiple record types via dot notation. For
/// example, the field definition <c>movie.country.id</c> traverses the child record referenced by the
/// field <c>movie</c>, then its child record referenced by the field <c>country</c>, and finally that
/// country's field <c>id</c>.
/// </para>
/// </remarks>
public sealed class PrimaryKey : IEquatable<PrimaryKey>
{
    private readonly string[] _fieldPaths;

    /// <summary>
    /// Defines a primary key over <paramref name="fieldPaths"/>, which carry the type they identify.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="fieldPaths"/> is empty, or its paths do not all start at one type.
    /// </exception>
    public PrimaryKey(params FieldPath[] fieldPaths)
        : this(RootOf(fieldPaths), [.. fieldPaths.Select(path => path.Path)])
    {
    }

    /// <summary>
    /// The one type every path in <paramref name="fieldPaths"/> starts at.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// There are no paths, or they do not all start at the same type.
    /// </exception>
    private static string RootOf(FieldPath[] fieldPaths)
    {
        ArgumentNullException.ThrowIfNull(fieldPaths);

        if (fieldPaths.Length == 0)
        {
            throw new ArgumentException("a key needs at least one field path", nameof(fieldPaths));
        }

        string root = fieldPaths[0].RootTypeName;

        foreach (FieldPath path in fieldPaths)
        {
            path.RequireRoot(root);
        }

        return root;
    }

    /// <inheritdoc cref="PrimaryKey(FieldPath[])" />
    /// <param name="type">The type the key identifies.</param>
    /// <param name="fieldPaths">The key's field paths, written out as text.</param>
    public PrimaryKey(string type, params string[] fieldPaths)
    {
        ArgumentNullException.ThrowIfNull(fieldPaths);

        if (fieldPaths.Length == 0)
        {
            throw new ArgumentException("fieldPaths cannot be empty", nameof(fieldPaths));
        }

        Type = type;
        _fieldPaths = (string[])fieldPaths.Clone();
    }

    /// <summary>The name of the type this key identifies records of.</summary>
    public string Type { get; }

    /// <summary>The number of fields making up this key.</summary>
    public int FieldCount => _fieldPaths.Length;

    /// <summary>The field paths making up this key.</summary>
    public IReadOnlyList<string> FieldPaths => _fieldPaths;

    /// <summary>Gets the field path at <paramref name="index"/>.</summary>
    public string GetFieldPath(int index) => _fieldPaths[index];

    /// <summary>
    /// Creates a key over <paramref name="fieldPaths"/>, or, when none are given, the key declared on
    /// <paramref name="type"/>'s own schema.
    /// </summary>
    /// <returns>
    /// The key, or <see langword="null"/> when no paths were given and <paramref name="type"/> is not an
    /// object type.
    /// </returns>
    public static PrimaryKey? Create(IHollowDataset dataset, string type, params string[]? fieldPaths)
    {
        if (fieldPaths is not null && fieldPaths.Length != 0)
        {
            return new PrimaryKey(type, fieldPaths);
        }

        ArgumentNullException.ThrowIfNull(dataset);

        return (dataset.GetSchema(type) as HollowObjectSchema)?.PrimaryKey;
    }

    /// <summary>
    /// Resolves the type of the field at <paramref name="fieldPathIndex"/> against
    /// <paramref name="dataset"/>.
    /// </summary>
    public FieldType GetFieldType(IHollowDataset dataset, int fieldPathIndex) =>
        GetFieldType(dataset, Type, _fieldPaths[fieldPathIndex]);

    /// <summary>
    /// Resolves the schema declaring the field at <paramref name="fieldPathIndex"/> against
    /// <paramref name="dataset"/>.
    /// </summary>
    public HollowObjectSchema GetFieldSchema(IHollowDataset dataset, int fieldPathIndex) =>
        GetFieldSchema(dataset, Type, _fieldPaths[fieldPathIndex]);

    /// <summary>
    /// Resolves the field at <paramref name="fieldPathIndex"/> into the schema field positions to
    /// traverse.
    /// </summary>
    public int[] GetFieldPathIndex(IHollowDataset dataset, int fieldPathIndex) =>
        GetFieldPathIndex(dataset, Type, _fieldPaths[fieldPathIndex]);

    /// <summary>
    /// Resolves the ultimate field type of <paramref name="fieldPath"/> starting from
    /// <paramref name="type"/>.
    /// </summary>
    /// <exception cref="FieldPathException">The path cannot be bound.</exception>
    public static FieldType GetFieldType(IHollowDataset dataset, string type, string fieldPath)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        HollowObjectSchema schema = (HollowObjectSchema)dataset.GetNonNullSchema(type);
        int[] pathIndexes = GetFieldPathIndex(dataset, type, fieldPath);

        for (int i = 0; i < pathIndexes.Length - 1; i++)
        {
            schema = (HollowObjectSchema)dataset.GetNonNullSchema(schema.GetReferencedType(pathIndexes[i])!);
        }

        return schema.GetFieldType(pathIndexes[^1]);
    }

    /// <summary>
    /// Resolves the schema that <paramref name="fieldPath"/> ends up referring to, starting from
    /// <paramref name="type"/>.
    /// </summary>
    /// <exception cref="FieldPathException">The path cannot be bound.</exception>
    public static HollowObjectSchema GetFieldSchema(IHollowDataset dataset, string type, string fieldPath)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        HollowObjectSchema schema = (HollowObjectSchema)dataset.GetNonNullSchema(type);

        foreach (int pathIndex in GetFieldPathIndex(dataset, type, fieldPath))
        {
            schema = (HollowObjectSchema)dataset.GetNonNullSchema(schema.GetReferencedType(pathIndex)!);
        }

        return schema;
    }

    /// <summary>
    /// Splits <paramref name="fieldPath"/> into its field names, auto-expanded where the declared path
    /// stopped short of a value field.
    /// </summary>
    /// <exception cref="FieldPathException">The path cannot be bound.</exception>
    public static string[] GetCompleteFieldPathParts(IHollowDataset dataset, string type, string fieldPath)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        int[] pathIndexes = GetFieldPathIndex(dataset, type, fieldPath);
        string[] parts = new string[pathIndexes.Length];

        HollowObjectSchema schema = (HollowObjectSchema)dataset.GetNonNullSchema(type);
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = schema.GetFieldName(pathIndexes[i]);

            string? referencedType = schema.GetReferencedType(pathIndexes[i]);
            if (referencedType is null)
            {
                break;
            }

            schema = (HollowObjectSchema)dataset.GetNonNullSchema(referencedType);
        }

        return parts;
    }

    /// <summary>
    /// Resolves <paramref name="fieldPath"/> into the schema field positions to traverse, one per
    /// segment of the auto-expanded path.
    /// </summary>
    /// <exception cref="FieldPathException">The path cannot be bound.</exception>
    public static int[] GetFieldPathIndex(IHollowDataset dataset, string type, string fieldPath) =>
        [
            // Qualified because this type's own FieldPaths property would otherwise shadow the class.
            .. Index.FieldPaths.CreateFieldPathForPrimaryKey(dataset, type, fieldPath).Segments
                .Select(segment => segment.Index),
        ];

    /// <inheritdoc />
    public bool Equals(PrimaryKey? other) =>
        other is not null
        && (ReferenceEquals(this, other)
            || (Type == other.Type && _fieldPaths.AsSpan().SequenceEqual(other._fieldPaths)));

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as PrimaryKey);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        System.HashCode hash = default;
        hash.Add(Type);
        foreach (string fieldPath in _fieldPaths)
        {
            hash.Add(fieldPath);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"PrimaryKey [type={Type}, fieldPaths=[{string.Join(", ", _fieldPaths)}]]";
}
