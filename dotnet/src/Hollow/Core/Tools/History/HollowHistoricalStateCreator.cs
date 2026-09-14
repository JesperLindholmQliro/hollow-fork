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
using Hollow.Core.Read.Engine.List;
using Hollow.Core.Read.Engine.Map;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Read.Engine.Set;
using Hollow.Core.Schema;
using Hollow.Core.Tools.Diff.Exact;
using Hollow.Core.Util;
using Hollow.Core.Write;
using Hollow.Core.Write.Copy;

namespace Hollow.Core.Tools.History;

/// <summary>
/// Builds the state of a dataset as it stood at one version in the past.
/// </summary>
/// <remarks>
/// <para>
/// There are two ways a consumer can move between versions, and each needs a different answer here. A
/// delta says exactly which records went, so the previous state is those records and a forwarding
/// pointer to the current one. A double snapshot says nothing at all, so the two states have to be
/// compared record by record, and everything the new one does not have has to be copied out whole.
/// </para>
/// <para>
/// Named <c>HollowHistoricalStateCreator</c> in Java. The round trip through a blob runs through a
/// <see cref="MemoryStream"/> rather than the piped streams and background thread Java uses — this port
/// is single-threaded throughout, and a state small enough to be a history entry is small enough to
/// hold twice for a moment.
/// </para>
/// </remarks>
public sealed class HollowHistoricalStateCreator
{
    private static readonly IReadOnlyDictionary<string, HollowHistoricalSchemaChange> NoSchemaChanges =
        new Dictionary<string, HollowHistoricalSchemaChange>(StringComparer.Ordinal);

    /// <summary>
    /// The state <paramref name="stateEngine"/> was in before the delta just applied to it.
    /// </summary>
    /// <param name="version">The version that state was of.</param>
    /// <param name="stateEngine">The engine a delta has just been applied to.</param>
    /// <param name="reverse">
    /// Whether the transition applied was a reverse delta, in which case what the history has to keep
    /// is what the transition <em>added</em>.
    /// </param>
    public HollowHistoricalStateDataAccess CreateBasedOnNewDelta(
        long version, HollowReadStateEngine stateEngine, bool reverse = false)
    {
        ArgumentNullException.ThrowIfNull(stateEngine);

        IntMapOrdinalRemapper typeRemovedOrdinalMapping = new();

        // The historical type states need an engine of their own to belong to; Java leaves the field
        // null instead. Nothing is registered in it — the data access is handed the list directly —
        // so it exists to answer "which engine is this record from", and to carry the missing-data
        // handler across, which is the only thing Java uses its engine argument for here.
        HollowReadStateEngine removedRecordCopies = new() { MissingDataHandler = stateEngine.MissingDataHandler };

        List<HollowTypeReadState> historicalTypeStates = [];

        foreach (HollowTypeReadState typeState in stateEngine.TypeStates.Values)
        {
            HollowDeltaHistoricalStateCreator? creator = CreatorFor(typeState, reverse);

            if (creator is null)
            {
                continue;
            }

            creator.PopulateHistory();

            typeRemovedOrdinalMapping.AddOrdinalRemapping(typeState.Schema.Name, creator.OrdinalMapping);
            historicalTypeStates.Add(creator.CreateHistoricalTypeReadState(removedRecordCopies));

            // Let go of the live state, so that it can be collected once every type is done.
            creator.DereferenceTypeState();
        }

        return new HollowHistoricalStateDataAccess(
            version,
            removedRecordCopies,
            historicalTypeStates,
            typeRemovedOrdinalMapping,
            NoSchemaChanges)
        {
            NextState = stateEngine,
        };
    }

    /// <summary>
    /// The previous state of a history whose ordinals are already consistent with the current one.
    /// </summary>
    /// <remarks>
    /// A history that has remapped every earlier state into one ordinal space can keep the whole of
    /// <paramref name="previous"/> as it stands: no record needs copying, because no ordinal means
    /// anything different here than it does anywhere else in the history.
    /// </remarks>
    public HollowHistoricalStateDataAccess CreateConsistentOrdinalHistoricalStateFromDoubleSnapshot(
        long version, HollowReadStateEngine previous) =>
        new(version, previous, IdentityOrdinalRemapper.Instance, NoSchemaChanges);

    /// <summary>
    /// The previous state of a double snapshot, worked out by comparing the two states.
    /// </summary>
    /// <param name="version">The version <paramref name="previous"/> is of.</param>
    /// <param name="previous">The state being left behind.</param>
    /// <param name="current">The state just read.</param>
    /// <param name="ordinalRemapper">
    /// The remapper over an equality mapping of the two states, which is what says whether a record in
    /// <paramref name="previous"/> is the same record as one in <paramref name="current"/>.
    /// </param>
    public HollowHistoricalStateDataAccess CreateHistoricalStateFromDoubleSnapshot(
        long version,
        HollowReadStateEngine previous,
        HollowReadStateEngine current,
        DiffEqualityMappingOrdinalRemapper ordinalRemapper)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(ordinalRemapper);

        HollowWriteStateEngine writeEngine =
            HollowWriteStateCreator.CreateWithSchemas(SchemasWithoutKeys(previous.Schemas));

        IntMapOrdinalRemapper typeRemovedOrdinalLookupMaps = new();

        // In dependency order, because a record cannot be copied before the records it references have
        // been given their places.
        foreach (HollowSchema previousSchema in HollowSchemaSorter.DependencyOrderedSchemaList(previous.Schemas))
        {
            string typeName = previousSchema.Name;
            HollowTypeReadState previousTypeState = previous.GetTypeState(typeName)!;

            IntMap ordinalLookupMap = current.GetTypeState(typeName) is { } currentTypeState
                ? CopyUnmatchedRecords(
                    previousTypeState, ordinalRemapper, currentTypeState.PopulatedOrdinals, writeEngine)

                // The type is gone from the data model, so nothing in it matched and all of it has to
                // be kept.
                : CopyAllRecords(previousTypeState, ordinalRemapper, writeEngine);

            typeRemovedOrdinalLookupMaps.AddOrdinalRemapping(typeName, ordinalLookupMap);
        }

        return new HollowHistoricalStateDataAccess(
            version,
            RoundTripStateEngine(writeEngine),
            typeRemovedOrdinalLookupMaps,
            CalculateSchemaChanges(previous, current, ordinalRemapper.EqualityMapping));
    }

    /// <summary>
    /// <paramref name="previous"/> again, with every ordinal it mentions put through
    /// <paramref name="ordinalRemapper"/>.
    /// </summary>
    /// <remarks>
    /// A history keeps every state in one ordinal space. When a double snapshot forces that space to
    /// move, each state already in the history has to be rebuilt against the new one — which means
    /// copying its records out and back in, because an ordinal is baked into every reference.
    /// </remarks>
    public HollowHistoricalStateDataAccess CopyButRemapOrdinals(
        HollowHistoricalStateDataAccess previous, IOrdinalRemapper ordinalRemapper)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(ordinalRemapper);

        HollowWriteStateEngine writeEngine =
            HollowWriteStateCreator.CreateWithSchemas(SchemasWithoutKeys(previous.Schemas));

        IntMapOrdinalRemapper typeRemovedOrdinalRemapping = new();

        foreach (string typeName in previous.AllTypes)
        {
            HollowHistoricalTypeDataAccess typeDataAccess = previous.TypeDataAccessMap[typeName];

            CopyRemappedRecords(typeDataAccess.RemovedRecords, ordinalRemapper, writeEngine);

            typeRemovedOrdinalRemapping.AddOrdinalRemapping(
                typeName,
                RemapPreviousOrdinalMapping(typeDataAccess.OrdinalRemap, typeName, ordinalRemapper));
        }

        return new HollowHistoricalStateDataAccess(
            previous.Version,
            RoundTripStateEngine(writeEngine),
            typeRemovedOrdinalRemapping,
            previous.SchemaChanges);
    }

    /// <summary>
    /// The creator for <paramref name="typeState"/>'s record kind, or <see langword="null"/> for a kind
    /// there is none for.
    /// </summary>
    private static HollowDeltaHistoricalStateCreator? CreatorFor(HollowTypeReadState typeState, bool reverse) =>
        typeState switch
        {
            HollowObjectTypeReadState state => new HollowObjectDeltaHistoricalStateCreator(state, reverse),
            HollowListTypeReadState state => new HollowListDeltaHistoricalStateCreator(state, reverse),
            HollowSetTypeReadState state => new HollowSetDeltaHistoricalStateCreator(state, reverse),
            HollowMapTypeReadState state => new HollowMapDeltaHistoricalStateCreator(state, reverse),
            _ => null,
        };

    /// <summary>
    /// The types whose schema differs between the two states, either side of the transition.
    /// </summary>
    private static Dictionary<string, HollowHistoricalSchemaChange> CalculateSchemaChanges(
        HollowReadStateEngine previous, HollowReadStateEngine current, DiffEqualityMapping equalityMapping)
    {
        Dictionary<string, HollowHistoricalSchemaChange> schemaChanges = new(StringComparer.Ordinal);

        foreach (HollowTypeReadState previousTypeState in previous.TypeStates.Values)
        {
            string typeName = previousTypeState.Schema.Name;

            if (current.GetTypeState(typeName) is not { } currentTypeState)
            {
                schemaChanges[typeName] = new HollowHistoricalSchemaChange(previousTypeState.Schema, null);
            }
            else if (equalityMapping.RequiresMissingFieldTraversal(typeName))
            {
                // The equality mapping had to look at fields one state has and the other does not,
                // which is exactly what a schema change looks like from down there.
                schemaChanges[typeName] =
                    new HollowHistoricalSchemaChange(previousTypeState.Schema, currentTypeState.Schema);
            }
        }

        foreach (HollowTypeReadState currentTypeState in current.TypeStates.Values)
        {
            string typeName = currentTypeState.Schema.Name;

            if (previous.GetTypeState(typeName) is null)
            {
                schemaChanges[typeName] = new HollowHistoricalSchemaChange(null, currentTypeState.Schema);
            }
        }

        return schemaChanges;
    }

    /// <summary>
    /// Copies out every record of <paramref name="previousTypeState"/> that the current state does not
    /// have, and gives the matched ones the ordinals the current state uses.
    /// </summary>
    /// <remarks>
    /// Two ordinal spaces have to be reconciled here. Matched records take the current state's ordinal,
    /// which leaves holes where the previous state's ordinals used to be; every unmatched ordinal is
    /// then given a place past the end of both spaces, so that nothing collides. That is what the
    /// double <see cref="IOrdinalRemapper.RemapOrdinal"/> below is doing: one hop from the previous
    /// ordinal to the free one, and another from the free one to the hole it fills.
    /// </remarks>
    private static IntMap CopyUnmatchedRecords(
        HollowTypeReadState previousTypeState,
        DiffEqualityMappingOrdinalRemapper ordinalRemapper,
        BitSet currentlyPopulatedOrdinals,
        HollowWriteStateEngine writeEngine)
    {
        string typeName = previousTypeState.Schema.Name;

        // Note: copying this way invalidates any custom hash codes the records carried.
        HollowRecordCopier recordCopier = HollowRecordCopier.Create(
            previousTypeState, previousTypeState.Schema, ordinalRemapper, preserveHashPositions: false);

        DiffEqualOrdinalMap equalityMap = ordinalRemapper.EqualityMapping.GetEqualOrdinalMap(typeName);
        bool shouldCopyAllRecords = ordinalRemapper.EqualityMapping.RequiresMissingFieldTraversal(typeName);

        BitSet previouslyPopulatedOrdinals = previousTypeState.PopulatedOrdinals;

        int ordinalSpaceLength = Math.Max(currentlyPopulatedOrdinals.Length, previouslyPopulatedOrdinals.Length);
        int matchedRecordCount = CountRecords(previouslyPopulatedOrdinals, equalityMap, matched: true);
        int unmatchedRecordCount = CountRecords(previouslyPopulatedOrdinals, equalityMap, matched: false);
        int nextFreeOrdinal = ordinalSpaceLength;

        ordinalRemapper.HintUnmatchedOrdinalCount(typeName, (ordinalSpaceLength - matchedRecordCount) * 2);

        IntMap ordinalLookupMap = new(
            shouldCopyAllRecords ? previouslyPopulatedOrdinals.Cardinality() : unmatchedRecordCount);

        BitSet mappedFromOrdinals = new(ordinalSpaceLength);
        BitSet mappedToOrdinals = new(ordinalSpaceLength);

        foreach (int fromOrdinal in previouslyPopulatedOrdinals.EnumerateSetBits())
        {
            int matchedToOrdinal = equalityMap.GetIdentityFromOrdinal(fromOrdinal);

            if (matchedToOrdinal == HollowConstants.OrdinalNone)
            {
                continue;
            }

            mappedFromOrdinals.Set(fromOrdinal);
            mappedToOrdinals.Set(matchedToOrdinal);

            if (shouldCopyAllRecords)
            {
                // The two schemas differ, so even a matched record is not the same record any more.
                ordinalLookupMap.Put(matchedToOrdinal, writeEngine.Add(typeName, recordCopier.Copy(fromOrdinal)));
            }
        }

        int unmatchedFrom = mappedFromOrdinals.NextClearBit(0);
        int unmatchedTo = mappedToOrdinals.NextClearBit(0);

        while (unmatchedFrom < ordinalSpaceLength)
        {
            ordinalRemapper.RemapOrdinal(typeName, unmatchedFrom, nextFreeOrdinal);
            ordinalRemapper.RemapOrdinal(typeName, nextFreeOrdinal, unmatchedTo);

            if (previouslyPopulatedOrdinals.Get(unmatchedFrom))
            {
                ordinalLookupMap.Put(nextFreeOrdinal, writeEngine.Add(typeName, recordCopier.Copy(unmatchedFrom)));
            }

            unmatchedFrom = mappedFromOrdinals.NextClearBit(unmatchedFrom + 1);
            unmatchedTo = mappedToOrdinals.NextClearBit(unmatchedTo + 1);
            nextFreeOrdinal++;
        }

        return ordinalLookupMap;
    }

    private static int CountRecords(BitSet populatedOrdinals, DiffEqualOrdinalMap equalityMap, bool matched) =>
        populatedOrdinals
            .EnumerateSetBits()
            .Count(ordinal =>
                (equalityMap.GetIdentityFromOrdinal(ordinal) != HollowConstants.OrdinalNone) == matched);

    private static IntMap CopyAllRecords(
        HollowTypeReadState typeState,
        DiffEqualityMappingOrdinalRemapper ordinalRemapper,
        HollowWriteStateEngine writeEngine)
    {
        string typeName = typeState.Schema.Name;

        // Note: copying this way invalidates any custom hash codes the records carried.
        HollowRecordCopier recordCopier = HollowRecordCopier.Create(
            typeState, typeState.Schema, ordinalRemapper, preserveHashPositions: false);

        BitSet populatedOrdinals = typeState.PopulatedOrdinals;
        IntMap ordinalLookupMap = new(populatedOrdinals.Cardinality());

        foreach (int ordinal in populatedOrdinals.EnumerateSetBits())
        {
            ordinalLookupMap.Put(ordinal, writeEngine.Add(typeName, recordCopier.Copy(ordinal)));
        }

        return ordinalLookupMap;
    }

    private static void CopyRemappedRecords(
        HollowTypeReadState readTypeState, IOrdinalRemapper ordinalRemapper, HollowWriteStateEngine writeEngine)
    {
        string typeName = readTypeState.Schema.Name;
        HollowTypeWriteState typeState = writeEngine.GetTypeState(typeName)!;

        // Note: copying this way invalidates any custom hash codes the records carried.
        HollowRecordCopier copier = HollowRecordCopier.Create(
            readTypeState, readTypeState.Schema, ordinalRemapper, preserveHashPositions: false);

        // Every ordinal, not just the populated ones: this is a copy of a historical state, whose
        // records sit at 0..maxOrdinal with nothing tracking them.
        for (int ordinal = 0; ordinal <= readTypeState.MaxOrdinal; ordinal++)
        {
            typeState.Add(copier.Copy(ordinal));
        }
    }

    private static IntMap RemapPreviousOrdinalMapping(
        IntMap? previousOrdinalMapping, string typeName, IOrdinalRemapper ordinalRemapper)
    {
        if (previousOrdinalMapping is null)
        {
            return new IntMap(0);
        }

        IntMap ordinalLookupMap = new(previousOrdinalMapping.Count);

        foreach ((int key, int value) in previousOrdinalMapping.Entries())
        {
            ordinalLookupMap.Put(ordinalRemapper.GetMappedOrdinal(typeName, key), value);
        }

        return ordinalLookupMap;
    }

    /// <summary>
    /// Writes <paramref name="writeEngine"/> out as a snapshot and reads it straight back, which is
    /// what turns a set of copied records into something readable.
    /// </summary>
    private static HollowReadStateEngine RoundTripStateEngine(HollowWriteStateEngine writeEngine)
    {
        using MemoryStream blob = new();

        new HollowBlobWriter(writeEngine).WriteSnapshot(blob);
        blob.Position = 0;

        HollowReadStateEngine removedRecordCopies = new();
        new HollowBlobReader(removedRecordCopies).ReadSnapshot(blob);

        return removedRecordCopies;
    }

    /// <summary>
    /// The same schemas without their primary keys.
    /// </summary>
    /// <remarks>
    /// A historical state's records are copies sitting at whatever ordinals the copy gave them, and
    /// several states may hold copies of the same record. A declared primary key would promise
    /// uniqueness that nothing here can keep.
    /// </remarks>
    private static IEnumerable<HollowSchema> SchemasWithoutKeys(IEnumerable<HollowSchema> schemas) =>
        schemas.Select(HollowSchema.WithoutKeys);
}
