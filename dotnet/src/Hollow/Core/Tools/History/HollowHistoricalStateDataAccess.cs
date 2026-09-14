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

using Hollow.Api.Error;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Missing;
using Hollow.Core.Schema;
using Hollow.Core.Write.Copy;

namespace Hollow.Core.Tools.History;

/// <summary>
/// A whole dataset, read as it stood at one version in the past.
/// </summary>
/// <remarks>
/// <para>
/// This looks like any other <see cref="IHollowDataAccess"/> — a generated client or the generic
/// object API will read through it without knowing the difference — but it holds only the records the
/// next transition removed. Anything else is answered by walking forward along the chain of states to
/// whichever one still has it, ending at the live read state.
/// </para>
/// <para>
/// Named <c>HollowHistoricalStateDataAccess</c> in Java. The sampling and stack-trace recording the
/// Java class carries are not ported; what is left is the chain.
/// </para>
/// </remarks>
public sealed class HollowHistoricalStateDataAccess : IHollowDataAccess
{
    private readonly Dictionary<string, HollowHistoricalTypeDataAccess> _typeDataAccessMap =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Wraps the records <paramref name="removedRecordCopies"/> kept for <paramref name="version"/>.
    /// </summary>
    /// <param name="version">The version this state is of.</param>
    /// <param name="removedRecordCopies">The records the next transition removed.</param>
    /// <param name="removedCopyOrdinalMappings">Where each removed record was copied to.</param>
    /// <param name="schemaChanges">The types whose schema changed on the way out of this state.</param>
    public HollowHistoricalStateDataAccess(
        long version,
        HollowReadStateEngine removedRecordCopies,
        IOrdinalRemapper removedCopyOrdinalMappings,
        IReadOnlyDictionary<string, HollowHistoricalSchemaChange> schemaChanges)
        : this(
            version,
            removedRecordCopies,
            removedRecordCopies?.TypeStates.Values ?? throw new ArgumentNullException(nameof(removedRecordCopies)),
            removedCopyOrdinalMappings,
            schemaChanges)
    {
    }

    /// <summary>
    /// Wraps a chosen subset of <paramref name="removedRecordCopies"/>' types.
    /// </summary>
    /// <remarks>
    /// The history uses this when a type was dropped from the data model: its records still have to be
    /// readable in the states that had them, even though the current state has no such type.
    /// </remarks>
    public HollowHistoricalStateDataAccess(
        long version,
        HollowReadStateEngine removedRecordCopies,
        IEnumerable<HollowTypeReadState> typeStates,
        IOrdinalRemapper removedCopyOrdinalMappings,
        IReadOnlyDictionary<string, HollowHistoricalSchemaChange> schemaChanges)
    {
        ArgumentNullException.ThrowIfNull(removedRecordCopies);
        ArgumentNullException.ThrowIfNull(typeStates);
        ArgumentNullException.ThrowIfNull(removedCopyOrdinalMappings);
        ArgumentNullException.ThrowIfNull(schemaChanges);

        Version = version;
        OrdinalMapping = removedCopyOrdinalMappings;
        SchemaChanges = schemaChanges;
        MissingDataHandler = removedRecordCopies.MissingDataHandler;

        foreach (HollowTypeReadState typeState in typeStates)
        {
            _typeDataAccessMap[typeState.Schema.Name] = typeState.Schema.SchemaType switch
            {
                SchemaType.Object => new HollowHistoricalObjectDataAccess(this, typeState),
                SchemaType.List => new HollowHistoricalListDataAccess(this, typeState),
                SchemaType.Set => new HollowHistoricalSetDataAccess(this, typeState),
                SchemaType.Map => new HollowHistoricalMapDataAccess(this, typeState),
                _ => throw new ArgumentException(
                    $"unknown schema type {typeState.Schema.SchemaType}", nameof(typeStates)),
            };
        }

        // Second pass: a hash key's path may leave its own type, so every type has to be in place
        // before any of them can resolve one.
        foreach (HollowHistoricalTypeDataAccess typeDataAccess in _typeDataAccessMap.Values)
        {
            switch (typeDataAccess)
            {
                case HollowHistoricalSetDataAccess set:
                    set.BuildKeyMatcher();
                    break;
                case HollowHistoricalMapDataAccess map:
                    map.BuildKeyMatcher();
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>The version this state is of.</summary>
    public long Version { get; }

    /// <summary>Where each record this state kept was copied to.</summary>
    public IOrdinalRemapper OrdinalMapping { get; }

    /// <summary>The types whose schema changed on the way out of this state.</summary>
    public IReadOnlyDictionary<string, HollowHistoricalSchemaChange> SchemaChanges { get; }

    /// <summary>
    /// The state that came after this one, which holds everything this one did not keep.
    /// </summary>
    /// <remarks>
    /// Eventually the live read state, which is where a record still current is finally read from.
    /// </remarks>
    public IHollowDataAccess? NextState { get; set; }

    /// <inheritdoc />
    public IMissingDataHandler MissingDataHandler { get; }

    /// <summary>The types this state kept records of.</summary>
    internal IReadOnlyDictionary<string, HollowHistoricalTypeDataAccess> TypeDataAccessMap => _typeDataAccessMap;

    /// <inheritdoc />
    public IReadOnlyList<HollowSchema> Schemas => [.. _typeDataAccessMap.Values.Select(access => access.Schema)];

    /// <summary>The names of the types this state kept records of.</summary>
    public IEnumerable<string> AllTypes => _typeDataAccessMap.Keys;

    /// <inheritdoc />
    /// <remarks>
    /// Walks forward until a state claims the type. A type this state never kept anything of is still
    /// readable, because a later state has it.
    /// </remarks>
    public IHollowTypeDataAccess? GetTypeDataAccess(string type)
    {
        IHollowDataAccess? state = this;

        while (state is HollowHistoricalStateDataAccess historical)
        {
            if (historical._typeDataAccessMap.TryGetValue(type, out HollowHistoricalTypeDataAccess? typeDataAccess))
            {
                return typeDataAccess;
            }

            state = historical.NextState;
        }

        return state?.GetTypeDataAccess(type);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The ordinal is what decides here: the answer is the first state forward that actually kept this
    /// record, because that is the only one whose copy holds the right version of it.
    /// </remarks>
    public IHollowTypeDataAccess? GetTypeDataAccess(string type, int ordinal)
    {
        IHollowDataAccess? state = this;

        while (state is HollowHistoricalStateDataAccess historical)
        {
            if (historical.OrdinalMapping.OrdinalIsMapped(type, ordinal))
            {
                return state.GetTypeDataAccess(type);
            }

            state = historical.NextState;
        }

        return state?.GetTypeDataAccess(type, ordinal);
    }

    /// <inheritdoc />
    public HollowSchema? GetSchema(string typeName) => GetTypeDataAccess(typeName)?.Schema;

    /// <inheritdoc />
    public HollowSchema GetNonNullSchema(string typeName) =>
        GetSchema(typeName)
        ?? throw new SchemaNotFoundException(typeName, AllTypes);
}
