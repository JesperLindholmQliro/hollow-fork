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

using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Util;
using Hollow.Core.Write.Copy;

namespace Hollow.Core.Tools.History.KeyIndex;

/// <summary>
/// What changed in one type on one transition, keyed so a record can be followed across states.
/// </summary>
/// <remarks>
/// <para>
/// Two tables, both from <em>key ordinal</em> — the index's stable identifier for a record's key — to
/// the ordinal that record had. One says where each added record went; the other, where each removed
/// record was copied to. A key in both means the record was modified rather than added and removed,
/// which is what lets the history report three counts instead of two.
/// </para>
/// <para>
/// Named <c>HollowHistoricalStateTypeKeyOrdinalMapping</c> in Java.
/// </para>
/// </remarks>
public sealed class HollowHistoricalStateTypeKeyOrdinalMapping
{
    private readonly string _typeName;

    private IntMap _addedOrdinalMap = new(0);
    private IntMap _removedOrdinalMap = new(0);

    /// <summary>Starts an empty mapping for <paramref name="typeName"/>.</summary>
    public HollowHistoricalStateTypeKeyOrdinalMapping(string typeName, HollowHistoryTypeKeyIndex keyIndex)
    {
        _typeName = typeName;
        KeyIndex = keyIndex;
    }

    private HollowHistoricalStateTypeKeyOrdinalMapping(
        string typeName,
        HollowHistoryTypeKeyIndex keyIndex,
        IntMap addedOrdinalMap,
        IntMap removedOrdinalMap)
    {
        _typeName = typeName;
        KeyIndex = keyIndex;
        _addedOrdinalMap = addedOrdinalMap;
        _removedOrdinalMap = removedOrdinalMap;

        Finish();
    }

    /// <summary>What turns a record into the key ordinal these tables are keyed by.</summary>
    public HollowHistoryTypeKeyIndex KeyIndex { get; }

    /// <summary>Records this transition added and did not previously have.</summary>
    public int NumberOfNewRecords { get; private set; }

    /// <summary>Records this transition removed and did not put back.</summary>
    public int NumberOfRemovedRecords { get; private set; }

    /// <summary>Records this transition both removed and added — the same key, changed.</summary>
    public int NumberOfModifiedRecords { get; private set; }

    /// <summary>
    /// Sizes the two tables, which do not grow — see <see cref="IntMap"/>.
    /// </summary>
    public void Prepare(int numAdditions, int numRemovals)
    {
        _addedOrdinalMap = new IntMap(numAdditions);
        _removedOrdinalMap = new IntMap(numRemovals);
    }

    /// <summary>Records that <paramref name="ordinal"/> was added.</summary>
    public void Added(HollowTypeReadState typeState, int ordinal)
    {
        int recordKeyOrdinal = KeyIndex.FindKeyIndexOrdinal((HollowObjectTypeReadState)typeState, ordinal);
        _addedOrdinalMap.Put(recordKeyOrdinal, ordinal);
    }

    /// <summary>Records that <paramref name="ordinal"/> was removed.</summary>
    public void Removed(HollowTypeReadState typeState, int ordinal) => Removed(typeState, ordinal, ordinal);

    /// <summary>
    /// Records that <paramref name="stateEngineOrdinal"/> was removed, and copied to
    /// <paramref name="mappedOrdinal"/>.
    /// </summary>
    public void Removed(HollowTypeReadState typeState, int stateEngineOrdinal, int mappedOrdinal)
    {
        int recordKeyOrdinal = KeyIndex.FindKeyIndexOrdinal((HollowObjectTypeReadState)typeState, stateEngineOrdinal);
        _removedOrdinalMap.Put(recordKeyOrdinal, mappedOrdinal);
    }

    /// <summary>
    /// The same mapping with every ordinal sent through <paramref name="remapper"/>.
    /// </summary>
    /// <remarks>
    /// Only a double snapshot needs this: the ordinals were assigned against a state that has been
    /// replaced wholesale, so they have to be moved to where the records now live.
    /// </remarks>
    public HollowHistoricalStateTypeKeyOrdinalMapping Remap(IOrdinalRemapper remapper)
    {
        ArgumentNullException.ThrowIfNull(remapper);

        IntMap newAdded = new(_addedOrdinalMap.Count);

        foreach ((int key, int value) in _addedOrdinalMap.Entries())
        {
            newAdded.Put(key, remapper.GetMappedOrdinal(_typeName, value));
        }

        IntMap newRemoved = new(_removedOrdinalMap.Count);

        foreach ((int key, int value) in _removedOrdinalMap.Entries())
        {
            newRemoved.Put(key, remapper.GetMappedOrdinal(_typeName, value));
        }

        return new HollowHistoricalStateTypeKeyOrdinalMapping(_typeName, KeyIndex, newAdded, newRemoved);
    }

    /// <summary>
    /// Works out the three counts, once every addition and removal has been recorded.
    /// </summary>
    /// <remarks>
    /// A key that was both removed and added is one record that changed, so it is counted once as
    /// modified and taken out of both the added and removed totals.
    /// </remarks>
    public void Finish()
    {
        NumberOfModifiedRecords = _addedOrdinalMap.Entries()
            .Count(entry => _removedOrdinalMap.Get(entry.Key) != -1);

        NumberOfNewRecords = _addedOrdinalMap.Count - NumberOfModifiedRecords;
        NumberOfRemovedRecords = _removedOrdinalMap.Count - NumberOfModifiedRecords;
    }

    /// <summary>Every removed record, as key ordinal to the ordinal it was copied to.</summary>
    public IEnumerable<(int KeyOrdinal, int Ordinal)> RemovedOrdinalMappings() => _removedOrdinalMap.Entries();

    /// <summary>Every added record, as key ordinal to the ordinal it was given.</summary>
    public IEnumerable<(int KeyOrdinal, int Ordinal)> AddedOrdinalMappings() => _addedOrdinalMap.Entries();

    /// <summary>
    /// Where the record with <paramref name="keyOrdinal"/> was copied to when removed, or -1 if it was
    /// not removed here.
    /// </summary>
    public int FindRemovedOrdinal(int keyOrdinal) => _removedOrdinalMap.Get(keyOrdinal);

    /// <summary>
    /// The ordinal the record with <paramref name="keyOrdinal"/> was given when added, or -1 if it was
    /// not added here.
    /// </summary>
    public int FindAddedOrdinal(int keyOrdinal) => _addedOrdinalMap.Get(keyOrdinal);
}

/// <summary>
/// What changed on one transition, across every indexed type.
/// </summary>
/// <remarks>Named <c>HollowHistoricalStateKeyOrdinalMapping</c> in Java.</remarks>
public sealed class HollowHistoricalStateKeyOrdinalMapping
{
    private readonly Dictionary<string, HollowHistoricalStateTypeKeyOrdinalMapping> _typeMappings;

    /// <summary>Starts an empty mapping for each type <paramref name="keyIndex"/> covers.</summary>
    public HollowHistoricalStateKeyOrdinalMapping(HollowHistoryKeyIndex keyIndex)
    {
        ArgumentNullException.ThrowIfNull(keyIndex);

        _typeMappings = keyIndex.TypeKeyIndexes.ToDictionary(
            entry => entry.Key,
            entry => new HollowHistoricalStateTypeKeyOrdinalMapping(entry.Key, entry.Value),
            StringComparer.Ordinal);
    }

    private HollowHistoricalStateKeyOrdinalMapping(
        Dictionary<string, HollowHistoricalStateTypeKeyOrdinalMapping> typeMappings) =>
        _typeMappings = typeMappings;

    /// <summary>The per-type mappings, keyed by type name.</summary>
    public IReadOnlyDictionary<string, HollowHistoricalStateTypeKeyOrdinalMapping> TypeMappings => _typeMappings;

    /// <summary>The same mapping with every ordinal sent through <paramref name="remapper"/>.</summary>
    public HollowHistoricalStateKeyOrdinalMapping Remap(IOrdinalRemapper remapper) =>
        new(_typeMappings.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.Remap(remapper),
            StringComparer.Ordinal));

    /// <summary>
    /// The mapping for <paramref name="typeName"/>, or <see langword="null"/> if it is not indexed.
    /// </summary>
    public HollowHistoricalStateTypeKeyOrdinalMapping? GetTypeMapping(string typeName) =>
        _typeMappings.GetValueOrDefault(typeName);
}
