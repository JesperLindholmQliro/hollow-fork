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
using Hollow.Core.Memory.Encoding;
using Hollow.Core.Read;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Schema;
using Hollow.Core.Tools.Util;
using Hollow.Core.Util;

namespace Hollow.Core.Tools.History.KeyIndex;

/// <summary>
/// Gives every distinct key ever seen a stable ordinal of its own, and remembers the key's values.
/// </summary>
/// <remarks>
/// <para>
/// This is what lets one record be followed across a history. A record's ordinal changes from state
/// to state and means nothing across them; its key does not. So each distinct key is assigned an
/// ordinal here once, and every state's mapping is expressed in terms of that.
/// </para>
/// <para>
/// An open-addressed table with linear probing, and a parallel table per key field holding the
/// interned value of that field. The field values are also indexed by hash, which is what makes the
/// history UI's search possible.
/// </para>
/// <para>
/// Named <c>HollowOrdinalMapper</c> in Java.
/// </para>
/// </remarks>
public sealed class HollowOrdinalMapper
{
    private const double LoadFactor = 0.7;
    private const int StartingSize = 2069;

    private readonly PrimaryKey _primaryKey;
    private readonly int[][] _keyFieldIndices;
    private readonly bool[] _keyFieldIsIndexed;
    private readonly FieldType[] _keyFieldTypes;
    private readonly ObjectInternPool _memoizedPool = new();

    /// <summary>Record hash bucket to the ordinal assigned to the key in it.</summary>
    private int[] _hashToAssignedOrdinal;

    /// <summary>Per field, the same buckets holding the interned value of that field.</summary>
    private int[][] _fieldHashToObjectOrdinal;

    /// <summary>Per field, value hash to every assigned ordinal whose field hashes there.</summary>
    private IntList?[][] _fieldHashToAssignedOrdinal;

    /// <summary>Assigned ordinal to the bucket its key sits in.</summary>
    private int[] _assignedOrdinalToIndex;

    private int _size;

    /// <summary>
    /// Prepares a mapper for <paramref name="primaryKey"/>.
    /// </summary>
    /// <param name="primaryKey">The key that identifies a record.</param>
    /// <param name="keyFieldIsIndexed">Which of its fields are searchable.</param>
    /// <param name="keyFieldIndices">The resolved field path of each of its fields.</param>
    /// <param name="keyFieldTypes">The type of each of its fields.</param>
    public HollowOrdinalMapper(
        PrimaryKey primaryKey, bool[] keyFieldIsIndexed, int[][] keyFieldIndices, FieldType[] keyFieldTypes)
    {
        ArgumentNullException.ThrowIfNull(primaryKey);

        _primaryKey = primaryKey;
        _keyFieldIndices = keyFieldIndices;
        _keyFieldIsIndexed = keyFieldIsIndexed;
        _keyFieldTypes = keyFieldTypes;

        _hashToAssignedOrdinal = new int[StartingSize];
        _fieldHashToObjectOrdinal = new int[primaryKey.FieldCount][];
        _fieldHashToAssignedOrdinal = new IntList?[primaryKey.FieldCount][];
        _assignedOrdinalToIndex = new int[StartingSize];

        Array.Fill(_hashToAssignedOrdinal, HollowConstants.OrdinalNone);
        Array.Fill(_assignedOrdinalToIndex, HollowConstants.OrdinalNone);

        for (int field = 0; field < primaryKey.FieldCount; field++)
        {
            _fieldHashToObjectOrdinal[field] = new int[StartingSize];
            _fieldHashToAssignedOrdinal[field] = new IntList?[StartingSize];

            Array.Fill(_fieldHashToObjectOrdinal[field], HollowConstants.OrdinalNone);
        }
    }

    /// <summary>
    /// Makes everything written so far readable, and starts a new cycle.
    /// </summary>
    public void PrepareForRead() => _memoizedPool.PrepareForRead();

    /// <summary>
    /// Adds to <paramref name="results"/> every assigned ordinal whose field
    /// <paramref name="field"/> equals <paramref name="objectToMatch"/>.
    /// </summary>
    /// <remarks>
    /// The hash narrows the candidates; the equality check settles them, because different values can
    /// land in the same bucket.
    /// </remarks>
    public void AddMatches(int hashCode, object? objectToMatch, int field, FieldType type, IntList results)
    {
        ArgumentNullException.ThrowIfNull(results);

        IntList?[] fieldHashes = _fieldHashToAssignedOrdinal[field];

        if (fieldHashes[IndexFromHash(hashCode, fieldHashes.Length)] is not { } candidates)
        {
            return;
        }

        for (int i = 0; i < candidates.Count; i++)
        {
            int assignedOrdinal = candidates.Get(i);

            if (GetFieldObject(assignedOrdinal, field, type).Equals(objectToMatch))
            {
                results.Add(assignedOrdinal);
            }
        }
    }

    /// <summary>
    /// Records that <paramref name="assignedOrdinal"/>'s field <paramref name="fieldIndex"/> holds
    /// <paramref name="fieldObject"/>, so it can be searched for.
    /// </summary>
    /// <remarks>Only a field somebody asked to index is worth the entry.</remarks>
    public void WriteKeyFieldHash(object fieldObject, int assignedOrdinal, int fieldIndex)
    {
        if (!_keyFieldIsIndexed[fieldIndex])
        {
            return;
        }

        IntList?[] fieldHashes = _fieldHashToAssignedOrdinal[fieldIndex];
        int newIndex = IndexFromHash(HashObject(fieldObject), fieldHashes.Length);

        (fieldHashes[newIndex] ??= new IntList()).Add(assignedOrdinal);
    }

    /// <summary>
    /// The ordinal already assigned to the key of the record at <paramref name="keyOrdinal"/>, or
    /// <see cref="HollowConstants.OrdinalNone"/> if that key has not been seen.
    /// </summary>
    public int FindAssignedOrdinal(HollowObjectTypeReadState typeState, int keyOrdinal)
    {
        int scanIndex = IndexFromHash(HashKeyRecord(typeState, keyOrdinal), _hashToAssignedOrdinal.Length);

        while (_hashToAssignedOrdinal[scanIndex] != HollowConstants.OrdinalNone)
        {
            if (RecordsAreEqual(typeState, keyOrdinal, scanIndex))
            {
                return _hashToAssignedOrdinal[scanIndex];
            }

            scanIndex = (scanIndex + 1) % _hashToAssignedOrdinal.Length;
        }

        return HollowConstants.OrdinalNone;
    }

    /// <summary>
    /// Assigns <paramref name="assignedOrdinal"/> to the key of the record at
    /// <paramref name="ordinal"/>, unless that key is already known.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the key was new and the ordinal was used;
    /// <see langword="false"/> when the key was already there, and the ordinal is still free.
    /// </returns>
    public bool StoreNewRecord(HollowObjectTypeReadState typeState, int ordinal, int assignedOrdinal)
    {
        int hashedRecord = HashKeyRecord(typeState, ordinal);

        if ((double)_size / _hashToAssignedOrdinal.Length > LoadFactor)
        {
            ExpandAndRehashTable();
        }

        int newIndex = IndexFromHash(hashedRecord, _hashToAssignedOrdinal.Length);

        while (_hashToAssignedOrdinal[newIndex] != HollowConstants.OrdinalNone)
        {
            if (RecordsAreEqual(typeState, ordinal, newIndex))
            {
                _assignedOrdinalToIndex[assignedOrdinal] = newIndex;

                return false;
            }

            newIndex = (newIndex + 1) % _hashToAssignedOrdinal.Length;
        }

        for (int i = 0; i < _primaryKey.FieldCount; i++)
        {
            WriteKeyFieldHash(ReadValueInState(typeState, ordinal, i)!, assignedOrdinal, i);
        }

        StoreFieldObjects(typeState, ordinal, newIndex);

        _hashToAssignedOrdinal[newIndex] = assignedOrdinal;
        EnsureAssignedOrdinalCapacity(assignedOrdinal);
        _assignedOrdinalToIndex[assignedOrdinal] = newIndex;
        _size++;

        return true;
    }

    /// <summary>
    /// The value of field <paramref name="fieldIndex"/> of the key at
    /// <paramref name="assignedOrdinal"/>.
    /// </summary>
    public object GetFieldObject(int assignedOrdinal, int fieldIndex, FieldType type)
    {
        int index = _assignedOrdinalToIndex[assignedOrdinal];

        return _memoizedPool.GetObject(_fieldHashToObjectOrdinal[fieldIndex][index], type);
    }

    /// <summary>
    /// Reads field <paramref name="fieldIndex"/> of the record at <paramref name="ordinal"/>,
    /// following the key's field path to whichever type actually holds it.
    /// </summary>
    public object? ReadValueInState(HollowObjectTypeReadState typeState, int ordinal, int fieldIndex)
    {
        ArgumentNullException.ThrowIfNull(typeState);

        HollowObjectSchema schema = typeState.Schema;
        int[] path = _keyFieldIndices[fieldIndex];

        for (int i = 0; i < path.Length - 1; i++)
        {
            int fieldPosition = path[i];
            ordinal = typeState.ReadOrdinal(ordinal, fieldPosition);
            typeState = (HollowObjectTypeReadState)schema.GetReferencedTypeState(fieldPosition)!;
            schema = typeState.Schema;
        }

        return HollowReadFieldUtils.FieldValueObject(typeState, ordinal, path[^1]);
    }

    /// <summary>
    /// Whether the record at <paramref name="keyOrdinal"/> has the key sitting in bucket
    /// <paramref name="index"/>.
    /// </summary>
    /// <remarks>
    /// Only the indexed fields are compared, because only those were stored. A value written in the
    /// cycle in progress is treated as different on principle: two records of the same cycle are two
    /// records, whatever their keys read as.
    /// </remarks>
    private bool RecordsAreEqual(HollowObjectTypeReadState typeState, int keyOrdinal, int index)
    {
        for (int fieldIndex = 0; fieldIndex < _primaryKey.FieldCount; fieldIndex++)
        {
            if (!_keyFieldIsIndexed[fieldIndex])
            {
                continue;
            }

            object? newFieldValue = ReadValueInState(typeState, keyOrdinal, fieldIndex);
            int existingFieldOrdinalValue = _fieldHashToObjectOrdinal[fieldIndex][index];

            if (_memoizedPool.OrdinalInCurrentCycle(existingFieldOrdinalValue))
            {
                return false;
            }

            object existingFieldObjectValue =
                _memoizedPool.GetObject(existingFieldOrdinalValue, _keyFieldTypes[fieldIndex]);

            if (!existingFieldObjectValue.Equals(newFieldValue))
            {
                return false;
            }
        }

        return true;
    }

    private void StoreFieldObjects(HollowObjectTypeReadState typeState, int ordinal, int index)
    {
        for (int i = 0; i < _primaryKey.FieldCount; i++)
        {
            if (!_keyFieldIsIndexed[i])
            {
                continue;
            }

            object objectToStore = ReadValueInState(typeState, ordinal, i)
                ?? throw new ArgumentException(
                    $"Null value in primary key field for type='{_primaryKey.Type}', "
                    + $"fieldPath='{_primaryKey.GetFieldPath(i)}', recordOrdinal={ordinal}. "
                    + "Primary key fields cannot be null.",
                    nameof(ordinal));

            _fieldHashToObjectOrdinal[i][index] = _memoizedPool.WriteAndGetOrdinal(objectToStore);
        }
    }

    /// <summary>
    /// Doubles the table and puts everything back into it.
    /// </summary>
    /// <remarks>
    /// Every hash has to be recomputed from the stored values rather than carried over, because a
    /// bucket index says nothing about the hash that produced it once the table has been resized.
    /// </remarks>
    private void ExpandAndRehashTable()
    {
        PrepareForRead();

        int newLength = _hashToAssignedOrdinal.Length * 2;

        int[] newTable = new int[newLength];
        Array.Fill(newTable, HollowConstants.OrdinalNone);

        int[][] newFieldMappings = new int[_primaryKey.FieldCount][];
        IntList?[][] newFieldHashToOrdinal = new IntList?[_primaryKey.FieldCount][];

        for (int fieldIndex = 0; fieldIndex < _primaryKey.FieldCount; fieldIndex++)
        {
            newFieldMappings[fieldIndex] = new int[newLength];
            newFieldHashToOrdinal[fieldIndex] = new IntList?[newLength];
        }

        Array.Resize(ref _assignedOrdinalToIndex, newLength);

        for (int fieldIndex = 0; fieldIndex < _primaryKey.FieldCount; fieldIndex++)
        {
            foreach (IntList? ordinalList in _fieldHashToAssignedOrdinal[fieldIndex])
            {
                if (ordinalList is null || ordinalList.Count == 0)
                {
                    continue;
                }

                for (int i = 0; i < ordinalList.Count; i++)
                {
                    int ordinal = ordinalList.Get(i);
                    object originalFieldObject = GetFieldObject(ordinal, fieldIndex, _keyFieldTypes[fieldIndex]);
                    int newIndex = IndexFromHash(HashObject(originalFieldObject), newLength);

                    (newFieldHashToOrdinal[fieldIndex][newIndex] ??= new IntList()).Add(ordinal);
                }
            }
        }

        for (int i = 0; i < _hashToAssignedOrdinal.Length; i++)
        {
            if (_hashToAssignedOrdinal[i] == HollowConstants.OrdinalNone)
            {
                continue;
            }

            int newIndex = RehashExistingRecord(newTable, HashFromIndex(i), _hashToAssignedOrdinal[i]);

            for (int fieldIndex = 0; fieldIndex < _primaryKey.FieldCount; fieldIndex++)
            {
                newFieldMappings[fieldIndex][newIndex] = _fieldHashToObjectOrdinal[fieldIndex][i];
            }

            // The old table is reused to carry the old bucket to its new one, so that the
            // assigned-ordinal index can be moved over below.
            _hashToAssignedOrdinal[i] = newIndex;
        }

        for (int assignedOrdinal = 0; assignedOrdinal < _assignedOrdinalToIndex.Length; assignedOrdinal++)
        {
            int previousIndex = _assignedOrdinalToIndex[assignedOrdinal];

            // Ordinals are assigned in order, so the first unused one ends the run.
            if (previousIndex == HollowConstants.OrdinalNone)
            {
                break;
            }

            _assignedOrdinalToIndex[assignedOrdinal] = _hashToAssignedOrdinal[previousIndex];
        }

        _hashToAssignedOrdinal = newTable;
        _fieldHashToObjectOrdinal = newFieldMappings;
        _fieldHashToAssignedOrdinal = newFieldHashToOrdinal;
    }

    private static int RehashExistingRecord(int[] newTable, int originalHash, int assignedOrdinal)
    {
        int newIndex = IndexFromHash(originalHash, newTable.Length);

        while (newTable[newIndex] != HollowConstants.OrdinalNone)
        {
            newIndex = (newIndex + 1) % newTable.Length;
        }

        newTable[newIndex] = assignedOrdinal;

        return newIndex;
    }

    private int HashFromIndex(int index)
    {
        object?[] fieldObjects = new object?[_primaryKey.FieldCount];

        for (int fieldIndex = 0; fieldIndex < _primaryKey.FieldCount; fieldIndex++)
        {
            fieldObjects[fieldIndex] = _memoizedPool.GetObject(
                _fieldHashToObjectOrdinal[fieldIndex][index], _keyFieldTypes[fieldIndex]);
        }

        return HashKeyRecord(fieldObjects);
    }

    private int HashKeyRecord(HollowObjectTypeReadState typeState, int ordinal)
    {
        int hashCode = 0;

        for (int i = 0; i < _primaryKey.FieldCount; i++)
        {
            hashCode = (hashCode * 31) ^ HollowReadFieldUtils.HashObject(ReadValueInState(typeState, ordinal, i));
        }

        return HashCodes.HashInt(hashCode);
    }

    private static int HashKeyRecord(object?[] objects)
    {
        int hashCode = 0;

        foreach (object? fieldObject in objects)
        {
            hashCode = (hashCode * 31) ^ HollowReadFieldUtils.HashObject(fieldObject);
        }

        return HashCodes.HashInt(hashCode);
    }

    /// <summary>
    /// Grows the assigned-ordinal index if an ordinal has run past it.
    /// </summary>
    /// <remarks>
    /// Java sizes this alongside the hash table and relies on the two staying in step. They do not
    /// have to: the ordinals are handed out by the caller, and nothing bounds them by the table.
    /// </remarks>
    private void EnsureAssignedOrdinalCapacity(int assignedOrdinal)
    {
        if (assignedOrdinal < _assignedOrdinalToIndex.Length)
        {
            return;
        }

        int previousLength = _assignedOrdinalToIndex.Length;
        Array.Resize(ref _assignedOrdinalToIndex, Math.Max(assignedOrdinal + 1, previousLength * 2));
        Array.Fill(_assignedOrdinalToIndex, HollowConstants.OrdinalNone, previousLength,
            _assignedOrdinalToIndex.Length - previousLength);
    }

    /// <summary>
    /// A bucket index, which a remainder alone would not give: a hash can be negative.
    /// </summary>
    private static int IndexFromHash(int hashedValue, int length)
    {
        int modulus = hashedValue % length;

        return modulus < 0 ? modulus + length : modulus;
    }

    private static int HashObject(object? value) => HashCodes.HashInt(HollowReadFieldUtils.HashObject(value));
}
