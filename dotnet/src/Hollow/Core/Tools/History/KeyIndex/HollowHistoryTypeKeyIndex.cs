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

using System.Globalization;
using Hollow.Core.Index.Key;
using Hollow.Core.Memory.Encoding;
using Hollow.Core.Read;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Schema;
using Hollow.Core.Tools.Util;
using Hollow.Core.Util;

namespace Hollow.Core.Tools.History.KeyIndex;

/// <summary>
/// Every key of one type that the history has ever seen, each with an ordinal of its own.
/// </summary>
/// <remarks>
/// <para>
/// The index grows and never forgets: a key seen in the first state still resolves after hundreds of
/// transitions, which is what lets the history follow one record the whole way along. A delta adds
/// the keys that arrived; a snapshot rebuilds, because the ordinals it brings mean nothing against
/// what came before.
/// </para>
/// <para>
/// Named <c>HollowHistoryTypeKeyIndex</c> in Java.
/// </para>
/// </remarks>
public sealed class HollowHistoryTypeKeyIndex
{
    private readonly PrimaryKey _primaryKey;
    private readonly FieldType[] _fieldTypes;
    private readonly string[][] _keyFieldNames;
    private readonly int[][] _keyFieldIndices;
    private readonly bool[] _keyFieldIsIndexed;
    private readonly HollowOrdinalMapper _ordinalMapping;

    /// <summary>
    /// Prepares an index of <paramref name="primaryKey"/> over the model
    /// <paramref name="dataModel"/> describes.
    /// </summary>
    public HollowHistoryTypeKeyIndex(PrimaryKey primaryKey, IHollowDataset dataModel)
    {
        ArgumentNullException.ThrowIfNull(primaryKey);
        ArgumentNullException.ThrowIfNull(dataModel);

        _primaryKey = primaryKey;
        _fieldTypes = new FieldType[primaryKey.FieldCount];
        _keyFieldNames = new string[primaryKey.FieldCount][];
        _keyFieldIndices = new int[primaryKey.FieldCount][];
        _keyFieldIsIndexed = new bool[primaryKey.FieldCount];

        for (int i = 0; i < primaryKey.FieldCount; i++)
        {
            _keyFieldNames[i] = PrimaryKey.GetCompleteFieldPathParts(
                dataModel, primaryKey.Type, primaryKey.GetFieldPath(i));
            _keyFieldIndices[i] = PrimaryKey.GetFieldPathIndex(
                dataModel, primaryKey.Type, primaryKey.GetFieldPath(i));
        }

        _ordinalMapping = new HollowOrdinalMapper(primaryKey, _keyFieldIsIndexed, _keyFieldIndices, _fieldTypes);
    }

    /// <summary>Whether the key's field types have been resolved against a real state yet.</summary>
    public bool IsInitialized { get; private set; }

    /// <summary>One past the highest key ordinal assigned.</summary>
    public int MaxIndexedOrdinal { get; private set; }

    /// <summary>The field paths of the key this indexes.</summary>
    public IReadOnlyList<string> KeyFields => _primaryKey.FieldPaths;

    /// <summary>Which of those fields are searchable.</summary>
    public IReadOnlyList<bool> KeyFieldIsIndexed => _keyFieldIsIndexed;

    /// <summary>
    /// Marks <paramref name="fieldName"/> as searchable, if it is one of the key's fields.
    /// </summary>
    public void AddFieldIndex(string fieldName, IHollowDataset dataModel)
    {
        string[] fieldPathParts = PrimaryKey.GetCompleteFieldPathParts(dataModel, _primaryKey.Type, fieldName);

        for (int i = 0; i < _primaryKey.FieldCount; i++)
        {
            string[] keyFieldPathParts = PrimaryKey.GetCompleteFieldPathParts(
                dataModel, _primaryKey.Type, _primaryKey.GetFieldPath(i));

            if (keyFieldPathParts.AsSpan().SequenceEqual(fieldPathParts))
            {
                _keyFieldIsIndexed[i] = true;

                break;
            }
        }
    }

    /// <summary>
    /// Works out what type each key field actually is, from a state that has the type.
    /// </summary>
    public void InitializeKeySchema(HollowObjectTypeReadState initialTypeState)
    {
        ArgumentNullException.ThrowIfNull(initialTypeState);

        if (IsInitialized)
        {
            return;
        }

        for (int i = 0; i < _keyFieldNames.Length; i++)
        {
            _fieldTypes[i] = ResolveFieldType(initialTypeState.Schema, _keyFieldNames[i], 0);
        }

        IsInitialized = true;
    }

    /// <summary>
    /// The ordinal assigned to the key of the record at <paramref name="ordinal"/>.
    /// </summary>
    public int FindKeyIndexOrdinal(HollowObjectTypeReadState typeState, int ordinal) =>
        _ordinalMapping.FindAssignedOrdinal(typeState, ordinal);

    /// <summary>
    /// Takes in whatever <paramref name="latestTypeState"/> has that the index does not.
    /// </summary>
    /// <param name="latestTypeState">The type as the newest state holds it.</param>
    /// <param name="isDeltaAndIndexInitialized">
    /// Whether this was a delta onto an index that already has content. A snapshot starts again,
    /// because its ordinals bear no relation to the ones already indexed.
    /// </param>
    public void Update(HollowObjectTypeReadState? latestTypeState, bool isDeltaAndIndexInitialized)
    {
        if (latestTypeState is null)
        {
            return;
        }

        if (isDeltaAndIndexInitialized)
        {
            IndexNewRecords(latestTypeState);
        }
        else
        {
            MaxIndexedOrdinal = 0;
            IndexAllRecords(latestTypeState);
        }

        _ordinalMapping.PrepareForRead();
    }

    /// <summary>The key at <paramref name="keyOrdinal"/>, as the pages show it.</summary>
    public string GetKeyDisplayString(int keyOrdinal)
    {
        return string.Join(
            SearchUtils.MultiFieldKeyDelimiter,
            Enumerable.Range(0, _primaryKey.FieldCount)
                .Select(i => Convert.ToString(
                    _ordinalMapping.GetFieldObject(keyOrdinal, i, _fieldTypes[i]), CultureInfo.InvariantCulture)));
    }

    /// <summary>The value of one field of the key at <paramref name="keyOrdinal"/>.</summary>
    public object GetKeyFieldValue(int keyFieldIndex, int keyOrdinal) =>
        _ordinalMapping.GetFieldObject(keyOrdinal, keyFieldIndex, _fieldTypes[keyFieldIndex]);

    /// <summary>
    /// The key ordinals matching <paramref name="query"/>.
    /// </summary>
    /// <remarks>
    /// A query holding the delimiter is read as a whole composite key, and every component has to
    /// match the field in its position. Anything else is tried against every field in turn, so that
    /// searching for an id finds it without the reader knowing which field it is.
    /// </remarks>
    public IntList QueryIndexedFields(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!IsInitialized)
        {
            return new IntList();
        }

        string[] keyComponents = SearchUtils.SplitOnUnescapedDelimiters(query, _primaryKey.FieldCount);

        return keyComponents.Length > 1 && keyComponents.Length == _primaryKey.FieldCount
            ? QueryCompositeKey(keyComponents)
            : QuerySingleValue(Unescape(query));
    }

    private IntList QueryCompositeKey(string[] compositeKeyComponents)
    {
        IntList matchingKeys = new();
        HashSet<int>? resultSet = null;

        for (int i = 0; i < compositeKeyComponents.Length; i++)
        {
            matchingKeys.Clear();

            if (!TryAddMatchesForField(i, Unescape(compositeKeyComponents[i]), matchingKeys))
            {
                return new IntList();
            }

            HashSet<int> keySet = [.. matchingKeys.AsSpan()];

            // A component that matches nothing settles it: no record can match the whole key.
            if (keySet.Count == 0)
            {
                return new IntList();
            }

            if (resultSet is null)
            {
                resultSet = keySet;
            }
            else
            {
                resultSet.IntersectWith(keySet);
            }
        }

        IntList results = new();

        foreach (int keyOrdinal in resultSet ?? [])
        {
            results.Add(keyOrdinal);
        }

        return results;
    }

    private IntList QuerySingleValue(string query)
    {
        IntList matchingKeys = new();

        for (int i = 0; i < _primaryKey.FieldCount; i++)
        {
            TryAddMatchesForField(i, query, matchingKeys);
        }

        return matchingKeys;
    }

    /// <summary>
    /// Adds the keys whose field <paramref name="fieldIndex"/> reads as <paramref name="value"/>.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the text is not a value of that field's type at all — which is
    /// not an error, just a field this query cannot be about.
    /// </returns>
    private bool TryAddMatchesForField(int fieldIndex, string value, IntList matchingKeys)
    {
        FieldType fieldType = _fieldTypes[fieldIndex];
        int hashCode;
        object? objectToFind;

        switch (fieldType)
        {
            case FieldType.Int when int.TryParse(value, CultureInfo.InvariantCulture, out int queryInt):
                hashCode = HollowReadFieldUtils.IntHashCode(queryInt);
                objectToFind = queryInt;
                break;
            case FieldType.Long when long.TryParse(value, CultureInfo.InvariantCulture, out long queryLong):
                hashCode = HollowReadFieldUtils.LongHashCode(queryLong);
                objectToFind = queryLong;
                break;
            case FieldType.Double when double.TryParse(value, CultureInfo.InvariantCulture, out double queryDouble):
                hashCode = HollowReadFieldUtils.DoubleHashCode(queryDouble);
                objectToFind = queryDouble;
                break;
            case FieldType.Float when float.TryParse(value, CultureInfo.InvariantCulture, out float queryFloat):
                hashCode = HollowReadFieldUtils.FloatHashCode(queryFloat);
                objectToFind = queryFloat;
                break;
            case FieldType.String:
                hashCode = HollowReadFieldUtils.StringHashCode(value);
                objectToFind = value;
                break;
            default:
                return false;
        }

        _ordinalMapping.AddMatches(HashCodes.HashInt(hashCode), objectToFind, fieldIndex, fieldType, matchingKeys);

        return true;
    }

    private void IndexNewRecords(HollowObjectTypeReadState typeState)
    {
        PopulatedOrdinalListener listener = typeState.GetListener<PopulatedOrdinalListener>()!;

        // Flipped: what is populated now and was not before is what arrived.
        RemovedOrdinals arrivals = new(listener.PopulatedOrdinals, listener.PreviousOrdinals);

        foreach (int ordinal in arrivals)
        {
            IndexKey(typeState, ordinal);
        }
    }

    /// <summary>
    /// Indexes every record the state holds now and every record it held before.
    /// </summary>
    /// <remarks>
    /// <strong>A Java bug is not reproduced here.</strong> The mapper refuses to call two records equal
    /// when the second was interned in the same cycle as the first, because a value written this cycle
    /// is not readable yet and so cannot be compared. Java runs both the before and the now ordinals
    /// through in one cycle, which means a record that changed in this very transition — present at one
    /// ordinal before and another after — is seen twice and given two key ordinals for the one key. The
    /// index then answers a search for that key twice, and the pages show the record twice. Closing the
    /// cycle between the two passes makes the first pass readable, so the second pass recognises the key
    /// and reuses its ordinal.
    /// </remarks>
    private void IndexAllRecords(HollowObjectTypeReadState typeState)
    {
        PopulatedOrdinalListener listener = typeState.GetListener<PopulatedOrdinalListener>()!;

        IndexKeys(typeState, listener.PreviousOrdinals);

        _ordinalMapping.PrepareForRead();

        IndexKeys(typeState, listener.PopulatedOrdinals);
    }

    private void IndexKeys(HollowObjectTypeReadState typeState, BitSet ordinals)
    {
        foreach (int ordinal in ordinals.EnumerateSetBits())
        {
            IndexKey(typeState, ordinal);
        }
    }

    /// <summary>
    /// Gives the record's key an ordinal, unless that key already has one.
    /// </summary>
    private void IndexKey(HollowObjectTypeReadState typeState, int ordinal)
    {
        if (_ordinalMapping.StoreNewRecord(typeState, ordinal, MaxIndexedOrdinal))
        {
            MaxIndexedOrdinal++;
        }
    }

    /// <summary>
    /// Follows a field path down the schemas to the type actually holding the value, and reports what
    /// type that value is.
    /// </summary>
    private static FieldType ResolveFieldType(HollowObjectSchema schema, string[] keyFieldNames, int position)
    {
        int schemaPosition = schema.GetPosition(keyFieldNames[position]);

        if (position < keyFieldNames.Length - 1)
        {
            HollowObjectSchema nextSchema =
                (HollowObjectSchema)schema.GetReferencedTypeState(schemaPosition)!.Schema;

            return ResolveFieldType(nextSchema, keyFieldNames, position + 1);
        }

        return schema.GetFieldType(schemaPosition);
    }

    private static string Unescape(string value) =>
        value.Replace(
            SearchUtils.EscapedMultiFieldKeyDelimiter,
            SearchUtils.MultiFieldKeyDelimiter,
            StringComparison.Ordinal);
}
