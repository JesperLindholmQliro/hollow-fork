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
using Hollow.Core.Schema;
using Hollow.Core.Tools.Diff.Exact;
using Hollow.Core.Tools.History.KeyIndex;
using Hollow.Core.Util;

namespace Hollow.Core.Tools.History;

/// <summary>
/// How a dataset changed over many versions, kept in memory and indexed by key.
/// </summary>
/// <remarks>
/// <para>
/// Only the changes are retained — each state holds the records the next transition removed, and
/// everything else is answered by walking forward — so a long run of versions usually fits in memory,
/// and any record can be followed through all of them quickly.
/// </para>
/// <para>
/// A history can be built in both directions at once, from a pair of read states sitting at the same
/// version: one takes forward deltas as they arrive, the other walks backwards through reverse deltas
/// to fill in what happened before the history started. Once enough backward states exist the backward
/// state engine is dropped.
/// </para>
/// <para>
/// Named <c>HollowHistory</c> in Java, which likewise documents itself as not thread safe. The
/// remapping of existing states after a double snapshot runs in turn here rather than on a
/// <c>SimultaneousExecutor</c>, as elsewhere in this port.
/// </para>
/// </remarks>
public sealed class HollowHistory
{
    private readonly HollowHistoricalStateCreator _creator = new();
    private readonly int _maxHistoricalStatesToKeep;
    private readonly long _forwardInitialVersion;

    /// <summary>
    /// The states, newest first — the reverse of the order their <c>NextState</c> links run in.
    /// </summary>
    private readonly List<HollowHistoricalState> _historicalStates = [];

    private readonly Dictionary<long, HollowHistoricalState> _historicalStateLookup = [];

    private IReadOnlyDictionary<string, string> _latestHeaderEntries;

    /// <summary>
    /// Starts a history at <paramref name="initialStateEngine"/>, going forwards only.
    /// </summary>
    /// <param name="initialStateEngine">The read state the history starts from.</param>
    /// <param name="initialVersion">The version that state is of.</param>
    /// <param name="maxHistoricalStatesToKeep">How many states to hold before dropping the oldest.</param>
    /// <param name="autoDiscoverTypeIndex">
    /// Whether to follow every type whose schema declares a primary key. Without it, nothing is
    /// followed until <see cref="KeyIndex"/> is told what to follow.
    /// </param>
    public HollowHistory(
        HollowReadStateEngine initialStateEngine,
        long initialVersion,
        int maxHistoricalStatesToKeep,
        bool autoDiscoverTypeIndex = true)
        : this(
            initialStateEngine,
            null,
            initialVersion,
            HollowConstants.VersionNone,
            maxHistoricalStatesToKeep,
            autoDiscoverTypeIndex)
    {
    }

    /// <summary>
    /// Starts a history that can be built in both directions.
    /// </summary>
    /// <param name="forwardMovingStateEngine">The read state forward deltas will be applied to.</param>
    /// <param name="reverseMovingStateEngine">
    /// The read state reverse deltas will be applied to, or <see langword="null"/> to supply it later
    /// through <see cref="InitializeReverseStateEngine"/>. It must sit at the same version as
    /// <paramref name="forwardMovingStateEngine"/>, or the history would not be contiguous.
    /// </param>
    /// <param name="forwardInitialVersion">The version both states start at.</param>
    /// <param name="reverseInitialVersion">
    /// The same version again, or <see cref="HollowConstants.VersionNone"/> when there is no reverse
    /// state yet.
    /// </param>
    /// <param name="maxHistoricalStatesToKeep">How many states to hold before dropping the oldest.</param>
    /// <param name="autoDiscoverTypeIndex">
    /// Whether to follow every type whose schema declares a primary key.
    /// </param>
    public HollowHistory(
        HollowReadStateEngine forwardMovingStateEngine,
        HollowReadStateEngine? reverseMovingStateEngine,
        long forwardInitialVersion,
        long reverseInitialVersion,
        int maxHistoricalStatesToKeep,
        bool autoDiscoverTypeIndex = true)
    {
        ArgumentNullException.ThrowIfNull(forwardMovingStateEngine);

        if (forwardInitialVersion == HollowConstants.VersionNone)
        {
            throw new ArgumentException(
                "a history has to start at a known version", nameof(forwardInitialVersion));
        }

        _maxHistoricalStatesToKeep = maxHistoricalStatesToKeep;
        _forwardInitialVersion = forwardInitialVersion;

        LatestState = forwardMovingStateEngine;
        LatestVersion = forwardInitialVersion;
        _latestHeaderEntries = forwardMovingStateEngine.HeaderTags;

        if (reverseMovingStateEngine is not null || reverseInitialVersion != HollowConstants.VersionNone)
        {
            InitializeReverseStateEngine(reverseMovingStateEngine!, reverseInitialVersion);
        }

        if (!autoDiscoverTypeIndex)
        {
            return;
        }

        foreach (HollowSchema schema in forwardMovingStateEngine.Schemas)
        {
            if (schema is HollowObjectSchema { PrimaryKey: { } primaryKey })
            {
                KeyIndex.AddTypeIndex(primaryKey, forwardMovingStateEngine);
                KeyIndex.IndexTypeField(primaryKey, forwardMovingStateEngine);
            }
        }
    }

    /// <summary>Every key this history has seen, and the ordinal it was given.</summary>
    public HollowHistoryKeyIndex KeyIndex { get; } = new();

    /// <summary>The read state the newest version is held in.</summary>
    public HollowReadStateEngine LatestState { get; private set; }

    /// <summary>
    /// The read state the oldest version is held in, while the history is still being built backwards.
    /// </summary>
    public HollowReadStateEngine? OldestState { get; private set; }

    /// <summary>The newest version in the history.</summary>
    public long LatestVersion { get; private set; }

    /// <summary>The oldest version the history has walked back to.</summary>
    public long OldestVersion { get; private set; } = HollowConstants.VersionNone;

    /// <summary>
    /// Whether two lists holding the same elements in a different order count as the same list when a
    /// double snapshot is compared.
    /// </summary>
    /// <remarks>
    /// Java exposes this as a one-way <c>ignoreListOrderingOnDoubleSnapshot()</c> switch, which reads
    /// as the opposite of what it sets: calling it makes reordering count <em>as</em> a change.
    /// </remarks>
    public bool ListOrderingIsImportantOnDoubleSnapshot { get; set; }

    /// <summary>The states, newest first.</summary>
    public IReadOnlyList<HollowHistoricalState> HistoricalStates => _historicalStates;

    /// <summary>How many states the history is holding.</summary>
    public int NumberOfHistoricalStates => _historicalStates.Count;

    /// <summary>
    /// Supplies the read state reverse deltas will be applied to, after construction.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The version does not match the one the forward state started at, or a reverse state is already
    /// in place.
    /// </exception>
    public void InitializeReverseStateEngine(HollowReadStateEngine reverseStateEngine, long version)
    {
        ArgumentNullException.ThrowIfNull(reverseStateEngine);

        if (version == HollowConstants.VersionNone)
        {
            throw new ArgumentException("a reverse state has to be at a known version", nameof(version));
        }

        if (version != _forwardInitialVersion)
        {
            throw new InvalidOperationException(
                $"the reverse state is at version {version}, but the history starts at "
                + $"{_forwardInitialVersion}; they have to meet for the history to be contiguous");
        }

        if (OldestState is not null || OldestVersion != HollowConstants.VersionNone)
        {
            throw new InvalidOperationException("the reverse state engine is already in place");
        }

        OldestState = reverseStateEngine;
        OldestVersion = version;
    }

    /// <summary>
    /// The state of <paramref name="version"/>, or <see langword="null"/> if the history does not hold
    /// it.
    /// </summary>
    public HollowHistoricalState? GetHistoricalState(long version) =>
        LatestVersion == version && _historicalStates.Count > 0
            ? _historicalStates[0]
            : _historicalStateLookup.GetValueOrDefault(version);

    /// <summary>
    /// Takes in a forward delta that has just been applied to <see cref="LatestState"/>.
    /// </summary>
    /// <param name="newVersion">The version the state has moved to.</param>
    /// <remarks>
    /// The state engine has already moved; what is recorded here is the version it was at before,
    /// together with the records that transition dropped.
    /// </remarks>
    public void DeltaOccurred(long newVersion)
    {
        // Every key ever seen, growing as each transition brings new ones.
        KeyIndex.Update(LatestState, isDelta: true);

        HollowHistoricalStateDataAccess dataAccess = _creator.CreateBasedOnNewDelta(LatestVersion, LatestState);
        dataAccess.NextState = LatestState;

        AddHistoricalState(new HollowHistoricalState(
            newVersion,
            CreateKeyOrdinalMappingFromDelta(LatestState, reverse: false),
            dataAccess,
            _latestHeaderEntries));

        LatestVersion = newVersion;
        _latestHeaderEntries = LatestState.HeaderTags;
    }

    /// <summary>
    /// Takes in a reverse delta that has just been applied to <see cref="OldestState"/>.
    /// </summary>
    /// <param name="newVersion">The version the reverse state has moved back to.</param>
    /// <remarks>
    /// Backwards, the records worth keeping are the ones the transition <em>added</em>: those are what
    /// the older state will not have, and the older state is the one that survives.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// There is no reverse state engine, or the history is already full — going further back would
    /// evict the states at the other end and leave a gap.
    /// </exception>
    public void ReverseDeltaOccurred(long newVersion)
    {
        if (OldestState is null)
        {
            throw new InvalidOperationException(
                "there is no reverse-direction read state engine; either it was never initialized, or it "
                + "was dropped once the history filled up");
        }

        if (_historicalStates.Count >= _maxHistoricalStatesToKeep)
        {
            throw new InvalidOperationException(
                $"the history is already holding its maximum of {_maxHistoricalStatesToKeep} states; going "
                + "further back would evict the newest ones and leave the history discontiguous");
        }

        KeyIndex.Update(OldestState, isDelta: true);

        AddReverseHistoricalState(new HollowHistoricalState(
            OldestVersion,
            CreateKeyOrdinalMappingFromDelta(OldestState, reverse: true),
            _creator.CreateBasedOnNewDelta(OldestVersion, OldestState, reverse: true),
            OldestState.HeaderTags));

        OldestVersion = newVersion;
    }

    /// <summary>
    /// Takes in a double snapshot, which replaces the backing read state outright.
    /// </summary>
    /// <param name="newStateEngine">The state just read.</param>
    /// <param name="newVersion">The version it is of.</param>
    /// <remarks>
    /// A snapshot brings an ordinal space of its own, unrelated to the one the history has been built
    /// in. So every state already held has to be copied into the new space before the new state can be
    /// stitched on to the front.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The new version is not ahead of the latest; a double snapshot can only move the history
    /// forwards.
    /// </exception>
    public void DoubleSnapshotOccurred(HollowReadStateEngine newStateEngine, long newVersion)
    {
        ArgumentNullException.ThrowIfNull(newStateEngine);

        if (newVersion <= LatestVersion)
        {
            throw new ArgumentException(
                $"a double snapshot can only advance the history, and {newVersion} is not past "
                + $"{LatestVersion}",
                nameof(newVersion));
        }

        if (!KeyIndex.IsInitialized)
        {
            KeyIndex.Update(LatestState, isDelta: false);
        }

        KeyIndex.Update(newStateEngine, isDelta: false);

        DiffEqualityMapping mapping = new(
            LatestState,
            newStateEngine,
            oneToOne: true,
            listOrderingIsImportant: ListOrderingIsImportantOnDoubleSnapshot);

        DiffEqualityMappingOrdinalRemapper remapper = new(mapping);

        HollowHistoricalStateDataAccess dataAccess = _creator.CreateHistoricalStateFromDoubleSnapshot(
            LatestVersion, LatestState, newStateEngine, remapper);

        RemapHistoricalStates(remapper, dataAccess);

        dataAccess.NextState = newStateEngine;

        AddHistoricalState(new HollowHistoricalState(
            newVersion,
            CreateKeyOrdinalMappingFromDoubleSnapshot(newStateEngine, remapper),
            dataAccess,
            _latestHeaderEntries));

        LatestVersion = newVersion;
        LatestState = newStateEngine;
        _latestHeaderEntries = newStateEngine.HeaderTags;
    }

    /// <summary>
    /// Drops the <paramref name="count"/> oldest states.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The count is negative, or larger than the number of states held.
    /// </exception>
    public void RemoveHistoricalStates(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _historicalStates.Count);

        // Nothing will be built backwards after states start being dropped, so the state engine that
        // was there for it can go.
        OldestState = null;

        for (int i = 0; i < count; i++)
        {
            HollowHistoricalState removed = _historicalStates[^1];

            _historicalStates.RemoveAt(_historicalStates.Count - 1);
            _historicalStateLookup.Remove(removed.Version);
        }
    }

    /// <summary>
    /// Rebuilds every state already held against the ordinal space the new snapshot brought, and links
    /// the rebuilt chain up to <paramref name="newestDataAccess"/>.
    /// </summary>
    private void RemapHistoricalStates(
        DiffEqualityMappingOrdinalRemapper remapper, HollowHistoricalStateDataAccess newestDataAccess)
    {
        HollowHistoricalStateDataAccess nextDataAccess = newestDataAccess;
        HollowHistoricalState? nextState = null;

        // Newest first, which is the order the list is in — each state's NextState is the one before it
        // in the list, so the links have to be made in this direction.
        for (int i = 0; i < _historicalStates.Count; i++)
        {
            HollowHistoricalState original = _historicalStates[i];

            HollowHistoricalStateDataAccess remappedDataAccess =
                _creator.CopyButRemapOrdinals(original.DataAccess, remapper);

            remappedDataAccess.NextState = nextDataAccess;
            nextDataAccess = remappedDataAccess;

            HollowHistoricalState remapped = new(
                original.Version,
                original.KeyOrdinalMapping.Remap(remapper),
                remappedDataAccess,
                original.HeaderEntries)
            {
                NextState = nextState,
            };

            nextState = remapped;

            _historicalStates[i] = remapped;
            _historicalStateLookup[remapped.Version] = remapped;
        }
    }

    /// <summary>
    /// Which keys of each followed type a delta added and removed.
    /// </summary>
    /// <param name="stateEngine">The state the transition was applied to.</param>
    /// <param name="reverse">
    /// Whether the transition was a reverse delta, in which case added and removed swap meanings.
    /// </param>
    private HollowHistoricalStateKeyOrdinalMapping CreateKeyOrdinalMappingFromDelta(
        HollowReadStateEngine stateEngine, bool reverse)
    {
        HollowHistoricalStateKeyOrdinalMapping keyOrdinalMapping = new(KeyIndex);

        foreach (string keyType in KeyIndex.TypeKeyIndexes.Keys)
        {
            HollowHistoricalStateTypeKeyOrdinalMapping typeMapping = keyOrdinalMapping.GetTypeMapping(keyType)!;

            if (stateEngine.GetTypeState(keyType) is not HollowObjectTypeReadState typeState)
            {
                // The history follows the type, but this state has no such type. An empty mapping still
                // has to be finished, so that the state reads as "nothing changed here".
                typeMapping.Prepare(0, 0);
                typeMapping.Finish();

                continue;
            }

            PopulatedOrdinalListener listener = typeState.GetListener<PopulatedOrdinalListener>()!;

            RemovedOrdinals removals = new(listener, flip: reverse);
            RemovedOrdinals additions = new(listener, flip: !reverse);

            typeMapping.Prepare(additions.Count(), removals.Count());

            foreach (int ordinal in removals)
            {
                typeMapping.Removed(typeState, ordinal);
            }

            foreach (int ordinal in additions)
            {
                typeMapping.Added(typeState, ordinal);
            }

            typeMapping.Finish();
        }

        return keyOrdinalMapping;
    }

    /// <summary>
    /// Which keys of each followed type the double snapshot added and removed, which here means the
    /// ones the equality mapping could not pair off.
    /// </summary>
    private HollowHistoricalStateKeyOrdinalMapping CreateKeyOrdinalMappingFromDoubleSnapshot(
        HollowReadStateEngine newStateEngine, DiffEqualityMappingOrdinalRemapper ordinalRemapper)
    {
        HollowHistoricalStateKeyOrdinalMapping keyOrdinalMapping = new(KeyIndex);
        DiffEqualityMapping mapping = ordinalRemapper.EqualityMapping;

        foreach (string keyType in KeyIndex.TypeKeyIndexes.Keys)
        {
            HollowHistoricalStateTypeKeyOrdinalMapping typeMapping = keyOrdinalMapping.GetTypeMapping(keyType)!;

            HollowObjectTypeReadState? fromTypeState = LatestState.GetTypeState(keyType) as HollowObjectTypeReadState;
            HollowObjectTypeReadState? toTypeState =
                newStateEngine.GetTypeState(keyType) as HollowObjectTypeReadState;

            DiffEqualOrdinalMap equalOrdinalMap = mapping.GetEqualOrdinalMap(keyType);

            BitSet fromOrdinals = fromTypeState?.PopulatedOrdinals ?? new BitSet();
            BitSet toOrdinals = toTypeState?.PopulatedOrdinals ?? new BitSet();

            int[] removed =
            [
                .. fromOrdinals.EnumerateSetBits()
                    .Where(ordinal =>
                        equalOrdinalMap.GetIdentityFromOrdinal(ordinal) == HollowConstants.OrdinalNone),
            ];

            int[] added =
            [
                .. toOrdinals.EnumerateSetBits()
                    .Where(ordinal =>
                        equalOrdinalMap.GetIdentityToOrdinal(ordinal) == HollowConstants.OrdinalNone),
            ];

            typeMapping.Prepare(added.Length, removed.Length);

            foreach (int ordinal in removed)
            {
                // The removed record has moved: the copy sits wherever the shared ordinal space put it.
                typeMapping.Removed(fromTypeState!, ordinal, ordinalRemapper.GetMappedOrdinal(keyType, ordinal));
            }

            foreach (int ordinal in added)
            {
                typeMapping.Added(toTypeState!, ordinal);
            }

            typeMapping.Finish();
        }

        return keyOrdinalMapping;
    }

    /// <summary>Puts <paramref name="historicalState"/> at the front, as the newest state.</summary>
    private void AddHistoricalState(HollowHistoricalState historicalState)
    {
        if (_historicalStates.Count > 0)
        {
            _historicalStates[0].DataAccess.NextState = historicalState.DataAccess;
            _historicalStates[0].NextState = historicalState;
        }

        _historicalStates.Insert(0, historicalState);
        _historicalStateLookup[historicalState.Version] = historicalState;

        if (_historicalStates.Count > _maxHistoricalStatesToKeep)
        {
            RemoveHistoricalStates(1);
        }
    }

    /// <summary>Puts <paramref name="historicalState"/> at the back, as the oldest state.</summary>
    private void AddReverseHistoricalState(HollowHistoricalState historicalState)
    {
        if (_historicalStates.Count > 0)
        {
            historicalState.DataAccess.NextState = _historicalStates[^1].DataAccess;
            historicalState.NextState = _historicalStates[^1];
        }
        else
        {
            // Walking backwards before anything has been recorded forwards, so what follows this state
            // is the live one.
            historicalState.DataAccess.NextState = LatestState;
        }

        _historicalStates.Add(historicalState);
        _historicalStateLookup[historicalState.Version] = historicalState;

        if (_historicalStates.Count >= _maxHistoricalStatesToKeep)
        {
            // Nothing more will be built backwards, so the state engine that was there for it can go.
            OldestState = null;
        }
    }
}
