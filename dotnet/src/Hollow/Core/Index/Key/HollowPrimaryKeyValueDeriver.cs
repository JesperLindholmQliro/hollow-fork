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

using Hollow.Core.Read;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Schema;

namespace Hollow.Core.Index.Key;

/// <summary>
/// Reads and compares the primary key values of a type's records.
/// </summary>
public sealed class HollowPrimaryKeyValueDeriver
{
    private readonly HollowObjectTypeReadState _typeState;
    private readonly int[][] _fieldPathIndexes;
    private readonly FieldType[] _fieldTypes;

    /// <summary>
    /// Creates a deriver for <paramref name="primaryKey"/>, resolving its paths against
    /// <paramref name="stateEngine"/>.
    /// </summary>
    /// <exception cref="FieldPathException">One of the key's field paths cannot be bound.</exception>
    public HollowPrimaryKeyValueDeriver(PrimaryKey primaryKey, HollowReadStateEngine stateEngine)
    {
        ArgumentNullException.ThrowIfNull(primaryKey);
        ArgumentNullException.ThrowIfNull(stateEngine);

        _fieldPathIndexes = new int[primaryKey.FieldCount][];
        _fieldTypes = new FieldType[primaryKey.FieldCount];

        for (int i = 0; i < primaryKey.FieldCount; i++)
        {
            _fieldPathIndexes[i] = primaryKey.GetFieldPathIndex(stateEngine, i);
            _fieldTypes[i] = primaryKey.GetFieldType(stateEngine, i);
        }

        _typeState = stateEngine.GetTypeState(primaryKey.Type) as HollowObjectTypeReadState
            ?? throw new ArgumentException(
                $"type {primaryKey.Type} is not an object type in this state", nameof(primaryKey));
    }

    /// <summary>
    /// Creates a deriver over already-resolved field paths.
    /// </summary>
    public HollowPrimaryKeyValueDeriver(
        HollowObjectTypeReadState typeState, int[][] fieldPathIndexes, FieldType[] fieldTypes)
    {
        ArgumentNullException.ThrowIfNull(typeState);
        ArgumentNullException.ThrowIfNull(fieldPathIndexes);
        ArgumentNullException.ThrowIfNull(fieldTypes);

        _typeState = typeState;
        _fieldPathIndexes = fieldPathIndexes;
        _fieldTypes = fieldTypes;
    }

    /// <summary>The resolved field positions of each key field, one array per field.</summary>
    public IReadOnlyList<int[]> FieldPathIndexes => _fieldPathIndexes;

    /// <summary>The type of each key field.</summary>
    public IReadOnlyList<FieldType> FieldTypes => _fieldTypes;

    /// <summary>
    /// Returns whether <paramref name="ordinal"/>'s record has exactly the key <paramref name="keys"/>.
    /// </summary>
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
    /// Returns whether the key field at <paramref name="fieldIndex"/> of <paramref name="ordinal"/>'s
    /// record equals <paramref name="key"/>.
    /// </summary>
    public bool KeyMatches(object? key, int ordinal, int fieldIndex)
    {
        (HollowObjectTypeReadState typeState, int resolvedOrdinal) = Navigate(ordinal, fieldIndex);

        if (resolvedOrdinal == HollowConstants.OrdinalNone)
        {
            return false;
        }

        int[] path = _fieldPathIndexes[fieldIndex];

        return KeyMatches(key, _fieldTypes[fieldIndex], path[^1], resolvedOrdinal, typeState);
    }

    /// <summary>
    /// Returns whether the field at <paramref name="fieldPosition"/> of <paramref name="ordinal"/>'s
    /// record equals <paramref name="key"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="fieldType"/> cannot be compared.</exception>
    public static bool KeyMatches(
        object? key,
        FieldType fieldType,
        int fieldPosition,
        int ordinal,
        IHollowObjectTypeDataAccess dataAccess)
    {
        ArgumentNullException.ThrowIfNull(dataAccess);

        switch (fieldType)
        {
            case FieldType.Boolean:
                bool? stored = dataAccess.ReadBoolean(ordinal, fieldPosition);
                return key is bool b ? stored == b : key is null && stored is null;

            case FieldType.Bytes:
                return key is byte[] bytes
                    && ((ReadOnlySpan<byte>)dataAccess.ReadBytes(ordinal, fieldPosition)).SequenceEqual(bytes);

            case FieldType.Double:
                return key is double d && dataAccess.ReadDouble(ordinal, fieldPosition) == d;

            case FieldType.Float:
                return key is float f && dataAccess.ReadFloat(ordinal, fieldPosition) == f;

            case FieldType.Int:
                return key is int i && dataAccess.ReadInt(ordinal, fieldPosition) == i;

            case FieldType.Long:
                return key is long l && dataAccess.ReadLong(ordinal, fieldPosition) == l;

            case FieldType.Reference:
                return key is int referenced && dataAccess.ReadOrdinal(ordinal, fieldPosition) == referenced;

            case FieldType.String:
                return dataAccess.IsStringFieldEqual(ordinal, fieldPosition, key as string);

            // Compared by value, so a key of 1.5m matches a record storing 1.50m.
            case FieldType.Decimal:
                return key is decimal dec && dataAccess.ReadDecimal(ordinal, fieldPosition) == dec;

            default:
                throw new ArgumentException($"cannot compare a {fieldType} field", nameof(fieldType));
        }
    }

    /// <summary>
    /// Reads <paramref name="ordinal"/>'s record's key as boxed values, one per key field.
    /// </summary>
    public object?[] GetRecordKey(int ordinal)
    {
        object?[] results = new object?[_fieldPathIndexes.Length];

        for (int i = 0; i < results.Length; i++)
        {
            (HollowObjectTypeReadState typeState, int resolvedOrdinal) = Navigate(ordinal, i);

            results[i] = resolvedOrdinal == HollowConstants.OrdinalNone
                ? null
                : HollowReadFieldUtils.FieldValueObject(typeState, resolvedOrdinal, _fieldPathIndexes[i][^1]);
        }

        return results;
    }

    /// <summary>
    /// Follows all but the last segment of a key field's path, returning the record the last segment
    /// reads from.
    /// </summary>
    private (HollowObjectTypeReadState TypeState, int Ordinal) Navigate(int ordinal, int fieldIndex)
    {
        HollowObjectTypeReadState typeState = _typeState;
        int[] path = _fieldPathIndexes[fieldIndex];

        for (int i = 0; i < path.Length - 1; i++)
        {
            ordinal = typeState.ReadOrdinal(ordinal, path[i]);
            if (ordinal == HollowConstants.OrdinalNone)
            {
                return (typeState, HollowConstants.OrdinalNone);
            }

            typeState = (HollowObjectTypeReadState)(typeState.Schema.GetReferencedTypeState(path[i])
                ?? throw new InvalidOperationException(
                    $"type {typeState.Schema.GetReferencedType(path[i])} is not present in the read state"));
        }

        return (typeState, ordinal);
    }
}
