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

using System.Collections;
using System.Globalization;

namespace Hollow.Core.Write.ObjectMapper;

/// <summary>
/// Identifies one record: the type it belongs to and the values of that type's primary key fields.
/// </summary>
/// <remarks>
/// This is what an incremental cycle is expressed in. It has value equality, including over the key
/// values, so that two references to the same record collapse into one when they land in a dictionary.
/// </remarks>
public sealed class RecordPrimaryKey : IEquatable<RecordPrimaryKey>
{
    private readonly object?[] _key;

    /// <summary>
    /// Identifies the record of <paramref name="type"/> whose primary key fields hold
    /// <paramref name="key"/>, in the order the schema declares them.
    /// </summary>
    public RecordPrimaryKey(string type, params object?[] key)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(key);

        Type = type;
        _key = key;
    }

    /// <summary>The type the record belongs to.</summary>
    public string Type { get; }

    /// <summary>The values of the type's primary key fields.</summary>
    public IReadOnlyList<object?> Key => _key;

    /// <summary>
    /// The key values as the array <see cref="Index.HollowPrimaryKeyIndex.GetMatchingOrdinal"/> takes.
    /// </summary>
    public object?[] ToKeyArray() => [.. _key];

    /// <inheritdoc />
    public bool Equals(RecordPrimaryKey? other) =>
        other is not null
        && string.Equals(Type, other.Type, StringComparison.Ordinal)
        && _key.Length == other._key.Length
        && _key.Zip(other._key).All(static pair => KeyValuesEqual(pair.First, pair.Second));

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as RecordPrimaryKey);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Type, StringComparer.Ordinal);

        foreach (object? value in _key)
        {
            // A byte[] key hashes by contents, to agree with the equality below.
            hash.Add(value is byte[] bytes ? StructuralComparisons.StructuralEqualityComparer.GetHashCode(bytes) : value?.GetHashCode() ?? 0);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Type}: ({string.Join(", ", _key)})");

    private static bool KeyValuesEqual(object? first, object? second) =>
        (first, second) switch
        {
            (byte[] firstBytes, byte[] secondBytes) => firstBytes.AsSpan().SequenceEqual(secondBytes),
            _ => Equals(first, second),
        };
}
