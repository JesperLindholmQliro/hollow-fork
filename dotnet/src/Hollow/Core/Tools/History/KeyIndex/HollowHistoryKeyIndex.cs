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
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.Object;

namespace Hollow.Core.Tools.History.KeyIndex;

/// <summary>
/// Every key the history has ever seen, across every type it was asked to follow.
/// </summary>
/// <remarks>
/// <para>
/// This is the thing that makes a history a history rather than a pile of diffs. An ordinal means
/// nothing across two states, so without a key there is no way to say that the record here is the
/// record that was there. The index assigns each distinct key an ordinal of its own and keeps it
/// forever, and every state's changes are recorded against those.
/// </para>
/// <para>
/// Named <c>HollowHistoryKeyIndex</c> in Java, where the per-type updates run on a
/// <c>SimultaneousExecutor</c>. This port runs them in turn, as it does elsewhere.
/// </para>
/// </remarks>
public sealed class HollowHistoryKeyIndex
{
    private readonly Dictionary<string, HollowHistoryTypeKeyIndex> _typeKeyIndexes = new(StringComparer.Ordinal);

    /// <summary>Whether the index has taken in a state yet.</summary>
    public bool IsInitialized { get; private set; }

    /// <summary>The per-type indexes, keyed by type name.</summary>
    public IReadOnlyDictionary<string, HollowHistoryTypeKeyIndex> TypeKeyIndexes => _typeKeyIndexes;

    /// <summary>How many distinct keys of <paramref name="type"/> have been seen.</summary>
    public int NumUniqueKeys(string type) => _typeKeyIndexes[type].MaxIndexedOrdinal;

    /// <summary>The key at <paramref name="keyOrdinal"/> of <paramref name="type"/>, as text.</summary>
    public string GetKeyDisplayString(string type, int keyOrdinal) =>
        _typeKeyIndexes[type].GetKeyDisplayString(keyOrdinal);

    /// <summary>The key ordinal of the record at <paramref name="ordinal"/>.</summary>
    public int GetRecordKeyOrdinal(HollowObjectTypeReadState typeState, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(typeState);

        return _typeKeyIndexes[typeState.Schema.Name].FindKeyIndexOrdinal(typeState, ordinal);
    }

    /// <summary>
    /// Starts following <paramref name="primaryKey"/>'s type, keeping whatever fields of a previous
    /// index of that type were searchable.
    /// </summary>
    public HollowHistoryTypeKeyIndex AddTypeIndex(PrimaryKey primaryKey, IHollowDataset dataModel)
    {
        ArgumentNullException.ThrowIfNull(primaryKey);

        HollowHistoryTypeKeyIndex? previous = _typeKeyIndexes.GetValueOrDefault(primaryKey.Type);
        HollowHistoryTypeKeyIndex keyIndex = new(primaryKey, dataModel);

        if (previous is not null)
        {
            for (int i = 0; i < previous.KeyFields.Count; i++)
            {
                if (previous.KeyFieldIsIndexed[i])
                {
                    keyIndex.AddFieldIndex(previous.KeyFields[i], dataModel);
                }
            }
        }

        _typeKeyIndexes[primaryKey.Type] = keyIndex;

        return keyIndex;
    }

    /// <summary>Makes one field of an already-followed type searchable.</summary>
    public void IndexTypeField(string type, string keyFieldPath, IHollowDataset dataModel) =>
        _typeKeyIndexes[type].AddFieldIndex(keyFieldPath, dataModel);

    /// <summary>
    /// Makes every field of <paramref name="primaryKey"/> searchable, starting to follow its type if
    /// it is not already followed.
    /// </summary>
    public void IndexTypeField(PrimaryKey primaryKey, IHollowDataset dataModel)
    {
        ArgumentNullException.ThrowIfNull(primaryKey);

        HollowHistoryTypeKeyIndex typeIndex =
            _typeKeyIndexes.GetValueOrDefault(primaryKey.Type) ?? AddTypeIndex(primaryKey, dataModel);

        foreach (string fieldPath in primaryKey.FieldPaths)
        {
            typeIndex.AddFieldIndex(fieldPath, dataModel);
        }
    }

    /// <summary>
    /// Takes in whatever <paramref name="latestStateEngine"/> has that the index does not.
    /// </summary>
    /// <param name="latestStateEngine">The newest state.</param>
    /// <param name="isDelta">
    /// Whether this arrived as a delta. A snapshot rebuilds every type index, because the ordinals it
    /// brings bear no relation to the ones already indexed.
    /// </param>
    public void Update(HollowReadStateEngine latestStateEngine, bool isDelta)
    {
        ArgumentNullException.ThrowIfNull(latestStateEngine);

        bool isInitialUpdate = !IsInitialized;

        // A type index cannot resolve its field types until a state actually holding the type shows up.
        foreach ((string type, HollowHistoryTypeKeyIndex index) in _typeKeyIndexes)
        {
            if (index.IsInitialized)
            {
                continue;
            }

            if (latestStateEngine.GetTypeState(type) is HollowObjectTypeReadState typeState)
            {
                index.InitializeKeySchema(typeState);
            }
        }

        foreach ((string type, HollowHistoryTypeKeyIndex index) in _typeKeyIndexes)
        {
            index.Update(
                latestStateEngine.GetTypeState(type) as HollowObjectTypeReadState,
                isDelta && !isInitialUpdate);
        }

        IsInitialized = true;
    }
}
