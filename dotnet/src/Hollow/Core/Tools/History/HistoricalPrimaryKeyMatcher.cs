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

using Hollow.Core.Index.Key;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Schema;

namespace Hollow.Core.Tools.History;

/// <summary>
/// Asks whether the record at an ordinal has the key it is being looked for by.
/// </summary>
/// <remarks>
/// <para>
/// The history indexes records by key so that one record can be followed across many states. Two
/// records answering to the same key hash still have to be told apart, and that is what this does:
/// it walks the key's field paths and compares the values it finds against the ones supplied.
/// </para>
/// <para>
/// Named <c>HistoricalPrimaryKeyMatcher</c> in Java. It reads through an
/// <see cref="IHollowDataAccess"/> rather than a read state, because the state it is asked about is
/// usually a historical one that no longer exists as a blob.
/// </para>
/// </remarks>
public sealed class HistoricalPrimaryKeyMatcher
{
    private readonly IHollowObjectTypeDataAccess _keyTypeAccess;
    private readonly int[][] _fieldPathIndexes;

    /// <summary>
    /// Prepares to match <paramref name="primaryKey"/> against records read from
    /// <paramref name="dataAccess"/>.
    /// </summary>
    public HistoricalPrimaryKeyMatcher(IHollowDataAccess dataAccess, PrimaryKey primaryKey)
    {
        ArgumentNullException.ThrowIfNull(dataAccess);
        ArgumentNullException.ThrowIfNull(primaryKey);

        _fieldPathIndexes = new int[primaryKey.FieldCount][];
        FieldTypes = new FieldType[primaryKey.FieldCount];

        // Resolved once: a path is the same for every record of the type, and resolving it means
        // walking schemas.
        for (int i = 0; i < primaryKey.FieldCount; i++)
        {
            _fieldPathIndexes[i] = primaryKey.GetFieldPathIndex(dataAccess, i);
            FieldTypes[i] = primaryKey.GetFieldType(dataAccess, i);
        }

        _keyTypeAccess = dataAccess.GetTypeDataAccess(primaryKey.Type) as IHollowObjectTypeDataAccess
            ?? throw new ArgumentException(
                $"the key type {primaryKey.Type} is not an object type in this dataset", nameof(primaryKey));
    }

    /// <summary>The type of each field of the key, in the order the key declares them.</summary>
    public FieldType[] FieldTypes { get; }

    /// <summary>
    /// Whether the record at <paramref name="ordinal"/> has exactly the key <paramref name="keys"/>.
    /// </summary>
    /// <remarks>A key of the wrong length matches nothing, rather than throwing.</remarks>
    public bool KeyMatches(int ordinal, params object?[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        if (keys.Length != _fieldPathIndexes.Length)
        {
            return false;
        }

        for (int i = 0; i < keys.Length; i++)
        {
            if (!KeyMatches(keys[i], ordinal, i))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether field <paramref name="fieldIndex"/> of the record at <paramref name="ordinal"/> holds
    /// <paramref name="key"/>.
    /// </summary>
    public bool KeyMatches(object? key, int ordinal, int fieldIndex)
    {
        IHollowObjectTypeDataAccess dataAccess = _keyTypeAccess;
        HollowObjectSchema schema = (HollowObjectSchema)dataAccess.Schema;

        // Follow the path down to the record actually holding the value. Each step reads a reference
        // and moves to the type it points at.
        int[] path = _fieldPathIndexes[fieldIndex];

        for (int i = 0; i < path.Length - 1; i++)
        {
            int fieldPosition = path[i];
            ordinal = dataAccess.ReadOrdinal(ordinal, fieldPosition);
            dataAccess = (IHollowObjectTypeDataAccess)dataAccess.DataAccess
                .GetTypeDataAccess(schema.GetReferencedType(fieldPosition)!, ordinal)!;
            schema = (HollowObjectSchema)dataAccess.Schema;
        }

        int lastFieldIndex = path[^1];

        return FieldTypes[fieldIndex] switch
        {
            FieldType.Boolean => Equals(dataAccess.ReadBoolean(ordinal, lastFieldIndex), key as bool?),
            FieldType.Bytes => dataAccess.ReadBytes(ordinal, lastFieldIndex).AsSpan()
                .SequenceEqual((key as byte[]).AsSpan()),
            FieldType.Double => dataAccess.ReadDouble(ordinal, lastFieldIndex).Equals(key),
            FieldType.Float => dataAccess.ReadFloat(ordinal, lastFieldIndex).Equals(key),
            FieldType.Int => dataAccess.ReadInt(ordinal, lastFieldIndex).Equals(key),

            // A reference is compared as the ordinal it is, which is what an index over a reference
            // field holds.
            FieldType.Long => dataAccess.ReadLong(ordinal, lastFieldIndex).Equals(key),
            FieldType.Reference => dataAccess.ReadOrdinal(ordinal, lastFieldIndex).Equals(key),
            FieldType.String => dataAccess.IsStringFieldEqual(ordinal, lastFieldIndex, key as string),

            // Java compares a decimal by reading it as a double, which this port cannot do: the field
            // is 128 bits wide. See the format extension note in PORTING.md.
            FieldType.Decimal => dataAccess.ReadDecimal(ordinal, lastFieldIndex).Equals(key as decimal?),
            _ => throw new ArgumentException(
                $"I don't know how to compare a {FieldTypes[fieldIndex]}", nameof(fieldIndex)),
        };
    }
}
