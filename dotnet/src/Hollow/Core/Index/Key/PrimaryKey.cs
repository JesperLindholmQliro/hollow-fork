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
/// <para>
/// <strong>Port note.</strong> The Java class also resolves field paths against a dataset
/// (<c>getFieldType</c>, <c>getFieldPathIndex</c>, <c>getCompleteFieldPathParts</c>). Those depend on
/// <c>core/index/FieldPaths</c>, which is part of the indexing layer rather than the core engine, and
/// are not ported here — see <c>PORTING.md</c>.
/// </para>
/// </remarks>
public sealed class PrimaryKey : IEquatable<PrimaryKey>
{
    private readonly string[] _fieldPaths;

    /// <summary>
    /// Defines a primary key over <paramref name="fieldPaths"/> of <paramref name="type"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="fieldPaths"/> is empty.</exception>
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
