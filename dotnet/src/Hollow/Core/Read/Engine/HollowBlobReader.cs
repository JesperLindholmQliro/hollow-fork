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

using Hollow.Core.Memory;
using Hollow.Core.Memory.Encoding;
using Hollow.Core.Read.Engine.List;
using Hollow.Core.Read.Engine.Map;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Read.Engine.Set;
using Hollow.Core.Read.Filter;
using Hollow.Core.Schema;
using Hollow.Core.Util;

namespace Hollow.Core.Read.Engine;

/// <summary>
/// Populates a <see cref="HollowReadStateEngine"/> from a snapshot blob, and moves it along the delta
/// chain in either direction.
/// </summary>
/// <remarks>
/// <strong>Port note.</strong> Optional blob parts are not ported — see <c>PORTING.md</c>.
/// </remarks>
public sealed class HollowBlobReader
{
    private readonly HollowReadStateEngine _stateEngine;
    private readonly HollowBlobHeaderReader _headerReader;

    /// <summary>
    /// Initialises a reader that populates <paramref name="stateEngine"/>.
    /// </summary>
    public HollowBlobReader(HollowReadStateEngine stateEngine, HollowBlobHeaderReader? headerReader = null)
    {
        ArgumentNullException.ThrowIfNull(stateEngine);

        _stateEngine = stateEngine;
        _headerReader = headerReader ?? new HollowBlobHeaderReader();
    }

    /// <summary>
    /// Reads a snapshot blob from <paramref name="stream"/>.
    /// </summary>
    public void ReadSnapshot(Stream stream, ITypeFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using HollowBlobInput input = HollowBlobInput.Serial(stream, leaveOpen: true);
        ReadSnapshot(input, filter);
    }

    /// <summary>
    /// Reads a snapshot blob from <paramref name="input"/>.
    /// </summary>
    /// <param name="input">The blob to read.</param>
    /// <param name="filter">
    /// The types and fields to retain, or <see langword="null"/> to retain everything.
    /// </param>
    public void ReadSnapshot(HollowBlobInput input, ITypeFilter? filter = null) =>
        ReadSnapshot(input, optionalParts: null, filter);

    /// <summary>
    /// Reads a snapshot blob, taking the types it does not carry from the optional parts alongside it.
    /// </summary>
    /// <param name="input">The main blob.</param>
    /// <param name="optionalParts">
    /// The parts fetched alongside it, or <see langword="null"/> where the blob carries everything.
    /// </param>
    /// <param name="filter">
    /// The types and fields to retain, or <see langword="null"/> to retain everything.
    /// </param>
    /// <exception cref="ArgumentException">
    /// A part is named something other than what its own header says, or belongs to a different state
    /// than the main blob.
    /// </exception>
    public void ReadSnapshot(
        HollowBlobInput input, OptionalBlobPartInput? optionalParts, ITypeFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(input);

        RequireMatchingMemoryMode(input);

        // A filtered read rewrites each record's layout as it is read, which means writing; a mapped
        // blob is only ever read. Java refuses this the same way.
        if (!_stateEngine.MemoryMode.SupportsFiltering() && filter is not null && !ReferenceEquals(filter, TypeFilter.IncludeAll))
        {
            throw new NotSupportedException(
                $"a type filter cannot be applied in {_stateEngine.MemoryMode} mode, which reads records "
                + "in place rather than copying them");
        }

        HollowBlobHeader header = _headerReader.ReadHeader(input);

        IReadOnlyDictionary<string, HollowBlobInput> partInputs = OpenParts(optionalParts);
        IReadOnlyList<HollowBlobOptionalPartHeader> partHeaders = ReadPartHeaders(header, partInputs);

        // A filter has to be resolved against every schema the transition carries, main and parts
        // alike, or a type that lives in a part reads as one the filter never heard of.
        filter ??= TypeFilter.IncludeAll;
        filter = filter.Resolve(CombineSchemas(header.Schemas, partHeaders));

        _stateEngine.HeaderTags = header.HeaderTags;
        _stateEngine.RandomizedTag = header.DestinationRandomizedTag;

        int numStates = VarInt.ReadVInt(input);
        for (int i = 0; i < numStates; i++)
        {
            ReadTypeStateSnapshot(input, filter);
        }

        foreach (HollowBlobInput part in partInputs.Values)
        {
            int numPartStates = VarInt.ReadVInt(part);

            for (int i = 0; i < numPartStates; i++)
            {
                ReadTypeStateSnapshot(part, filter);
            }
        }

        _stateEngine.WireSchemaReferences();
    }

    /// <summary>Opens each part, or nothing where there are none.</summary>
    private static IReadOnlyDictionary<string, HollowBlobInput> OpenParts(OptionalBlobPartInput? optionalParts) =>
        optionalParts?.OpenByPartName(MemoryMode.OnHeap)
        ?? new Dictionary<string, HollowBlobInput>(StringComparer.Ordinal);

    /// <summary>
    /// Reads each part's header, refusing one that is not what it was asked for or not of this state.
    /// </summary>
    private IReadOnlyList<HollowBlobOptionalPartHeader> ReadPartHeaders(
        HollowBlobHeader header, IReadOnlyDictionary<string, HollowBlobInput> partInputs)
    {
        List<HollowBlobOptionalPartHeader> headers = new(partInputs.Count);

        foreach ((string partName, HollowBlobInput part) in partInputs)
        {
            HollowBlobOptionalPartHeader partHeader = _headerReader.ReadPartHeader(part);

            if (!string.Equals(partHeader.PartName, partName, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"the optional blob part given as '{partName}' says it is '{partHeader.PartName}'",
                    nameof(partInputs));
            }

            // The tags are the only thing that ties a part to a main blob. A part of the wrong state
            // would read as records at ordinals that mean something else entirely.
            if (partHeader.OriginRandomizedTag != header.OriginRandomizedTag
                || partHeader.DestinationRandomizedTag != header.DestinationRandomizedTag)
            {
                throw new ArgumentException(
                    $"the optional blob part '{partName}' belongs to a different state than the blob it "
                    + "was given with",
                    nameof(partInputs));
            }

            headers.Add(partHeader);
        }

        return headers;
    }

    /// <summary>Every schema the transition declares, wherever it was declared.</summary>
    private static IReadOnlyList<HollowSchema> CombineSchemas(
        IReadOnlyList<HollowSchema> main, IReadOnlyList<HollowBlobOptionalPartHeader> partHeaders)
    {
        if (partHeaders.Count == 0)
        {
            return main;
        }

        return [.. main, .. partHeaders.SelectMany(partHeader => partHeader.Schemas)];
    }

    /// <summary>
    /// Applies a delta blob from <paramref name="stream"/>, moving this state forward one transition.
    /// </summary>
    public void ApplyDelta(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using HollowBlobInput input = HollowBlobInput.Serial(stream, leaveOpen: true);
        ApplyDelta(input);
    }

    /// <summary>
    /// Applies a delta blob, moving this state forward one transition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A delta names the state it applies to by its randomized tag; applying it to any other state
    /// would silently corrupt the data, so a mismatch is rejected.
    /// </para>
    /// <para>
    /// A reverse delta is applied through this same method: the two differ only in which state each
    /// names as its origin.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidDataException">The delta does not apply to this state.</exception>
    public void ApplyDelta(HollowBlobInput input) => ApplyDelta(input, optionalParts: null);

    /// <summary>
    /// Applies a delta blob together with the optional parts of the same transition.
    /// </summary>
    public void ApplyDelta(HollowBlobInput input, OptionalBlobPartInput? optionalParts)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Applying a delta edits the records in place, and a mapped blob cannot be edited. A consumer
        // in shared-memory mode moves along the chain by mapping the next snapshot instead.
        if (_stateEngine.MemoryMode != MemoryMode.OnHeap)
        {
            throw new NotSupportedException(
                $"a delta cannot be applied in {_stateEngine.MemoryMode} mode; read a snapshot instead");
        }

        HollowBlobHeader header = _headerReader.ReadHeader(input);

        if (_stateEngine.RandomizedTag != 0 && header.OriginRandomizedTag != _stateEngine.RandomizedTag)
        {
            throw new InvalidDataException(
                $"This delta originates from a state with randomized tag {header.OriginRandomizedTag.Invariant()}, "
                + $"but the current state's tag is {_stateEngine.RandomizedTag.Invariant()}.");
        }

        IReadOnlyDictionary<string, HollowBlobInput> partInputs = OpenParts(optionalParts);

        ReadPartHeaders(header, partInputs);

        _stateEngine.HeaderTags = header.HeaderTags;
        _stateEngine.RandomizedTag = header.DestinationRandomizedTag;

        _stateEngine.NotifyBeginUpdate();

        int numStates = VarInt.ReadVInt(input);
        for (int i = 0; i < numStates; i++)
        {
            ReadTypeStateDelta(input);
        }

        foreach (HollowBlobInput part in partInputs.Values)
        {
            int numPartStates = VarInt.ReadVInt(part);

            for (int i = 0; i < numPartStates; i++)
            {
                ReadTypeStateDelta(part);
            }
        }

        _stateEngine.NotifyEndUpdate();
    }

    /// <summary>
    /// Checks that the input was opened the way the state engine expects to read.
    /// </summary>
    /// <remarks>
    /// The two have to agree: a state engine in shared-memory mode builds data elements that read
    /// through a mapping, and there is no mapping behind a serial input.
    /// </remarks>
    private void RequireMatchingMemoryMode(HollowBlobInput input)
    {
        if (input.MemoryMode != _stateEngine.MemoryMode)
        {
            throw new ArgumentException(
                $"this state engine reads in {_stateEngine.MemoryMode} mode, but the input was opened in "
                + $"{input.MemoryMode} mode",
                nameof(input));
        }
    }

    private void ReadTypeStateDelta(HollowBlobInput input)
    {
        HollowSchema schema = HollowSchema.ReadFrom(input);
        int numShards = ReadNumShards(input);

        HollowTypeReadState? typeState = _stateEngine.GetTypeState(schema.Name);

        if (typeState is not null && ShouldReshard(typeState.NumShards, numShards))
        {
            // The producer changed how many shards this type is written in, so the records already held
            // have to be rearranged to match before the delta — which is written per shard — can apply.
            HollowTypeReshardingStrategy.ForType(typeState).Reshard(typeState, typeState.NumShards, numShards);
        }

        switch (typeState)
        {
            case HollowObjectTypeReadState objectState when schema is HollowObjectSchema objectSchema:
                objectState.ApplyDelta(input, objectSchema, _stateEngine.MemoryRecycler);
                break;

            case HollowListTypeReadState listState when schema is HollowListSchema:
                listState.ApplyDelta(input, _stateEngine.MemoryRecycler);
                break;

            case HollowSetTypeReadState setState when schema is HollowSetSchema:
                setState.ApplyDelta(input, _stateEngine.MemoryRecycler);
                break;

            case HollowMapTypeReadState mapState when schema is HollowMapSchema:
                mapState.ApplyDelta(input, _stateEngine.MemoryRecycler);
                break;

            // A type this state does not hold — because the producer has just introduced it, or because
            // a filter excluded it. Skipping past its bytes leaves the rest of the delta readable; the
            // type arrives with the next snapshot.
            case null:
                DiscardTypeStateDelta(input, schema, numShards);
                break;

            default:
                throw new InvalidDataException(
                    $"The delta declares type {schema.Name} as a {schema.SchemaType} type, which does not "
                    + $"match the {typeState.Schema.SchemaType} type held in this state.");
        }
    }

    /// <summary>
    /// Whether the shard count the delta was written at differs from the one the records are held at.
    /// </summary>
    /// <remarks>
    /// A count of zero means "not stated" — a blob written before shard counts were recorded, or a type
    /// that has not read one yet — and never triggers a rearrangement.
    /// </remarks>
    private static bool ShouldReshard(int currentNumShards, int deltaNumShards) =>
        currentNumShards != 0 && deltaNumShards != 0 && currentNumShards != deltaNumShards;

    private static void DiscardTypeStateDelta(HollowBlobInput input, HollowSchema schema, int numShards)
    {
        switch (schema)
        {
            case HollowObjectSchema objectSchema:
                HollowObjectTypeDataElements.DiscardFromInput(input, objectSchema, numShards, isDelta: true);
                break;

            case HollowListSchema:
                HollowListTypeDataElements.DiscardFromInput(input, numShards, isDelta: true);
                break;

            case HollowSetSchema:
                HollowSetTypeDataElements.DiscardFromInput(input, numShards, isDelta: true);
                break;

            case HollowMapSchema:
                HollowMapTypeDataElements.DiscardFromInput(input, numShards, isDelta: true);
                break;

            default:
                throw new UnrecognizedSchemaTypeException(schema.Name, schema.SchemaType);
        }
    }

    private void ReadTypeStateSnapshot(HollowBlobInput input, ITypeFilter filter)
    {
        HollowSchema schema = HollowSchema.ReadFrom(input);
        int numShards = ReadNumShards(input);

        bool included = filter.Includes(schema.Name);

        switch (schema)
        {
            case HollowObjectSchema objectSchema when included:
                PopulateTypeStateSnapshot(
                    input,
                    new HollowObjectTypeReadState(
                        _stateEngine, _stateEngine.MemoryMode, objectSchema.FilterSchema(filter), objectSchema),
                    numShards);
                break;

            case HollowObjectSchema objectSchema:
                HollowObjectTypeDataElements.DiscardFromInput(input, objectSchema, numShards);
                SnapshotPopulatedOrdinalsReader.DiscardOrdinals(input);
                break;

            case HollowListSchema listSchema when included:
                PopulateTypeStateSnapshot(
                    input, new HollowListTypeReadState(_stateEngine, _stateEngine.MemoryMode, listSchema), numShards);
                break;

            case HollowListSchema:
                HollowListTypeDataElements.DiscardFromInput(input, numShards);
                SnapshotPopulatedOrdinalsReader.DiscardOrdinals(input);
                break;

            case HollowSetSchema setSchema when included:
                PopulateTypeStateSnapshot(
                    input, new HollowSetTypeReadState(_stateEngine, _stateEngine.MemoryMode, setSchema), numShards);
                break;

            case HollowSetSchema:
                HollowSetTypeDataElements.DiscardFromInput(input, numShards);
                SnapshotPopulatedOrdinalsReader.DiscardOrdinals(input);
                break;

            case HollowMapSchema mapSchema when included:
                PopulateTypeStateSnapshot(
                    input, new HollowMapTypeReadState(_stateEngine, _stateEngine.MemoryMode, mapSchema), numShards);
                break;

            case HollowMapSchema:
                HollowMapTypeDataElements.DiscardFromInput(input, numShards);
                SnapshotPopulatedOrdinalsReader.DiscardOrdinals(input);
                break;

            default:
                throw new UnrecognizedSchemaTypeException(schema.Name, schema.SchemaType);
        }
    }

    private void PopulateTypeStateSnapshot(HollowBlobInput input, HollowTypeReadState typeState, int numShards)
    {
        if (numShards <= 0 || (numShards & (numShards - 1)) != 0)
        {
            throw new InvalidDataException("Number of shards must be a power of 2!");
        }

        _stateEngine.AddTypeState(typeState);
        typeState.ReadSnapshot(input, _stateEngine.MemoryRecycler, numShards);
    }

    /// <summary>
    /// Reads the shard count from inside its forwards-compatibility envelope.
    /// </summary>
    private static int ReadNumShards(HollowBlobInput input)
    {
        int backwardsCompatibilityBytes = VarInt.ReadVInt(input);

        if (backwardsCompatibilityBytes == 0)
        {
            // Written by a version of Hollow prior to 2.1.0, which always used a single shard.
            return 1;
        }

        HollowBlobHeaderReader.SkipForwardCompatibilityBytes(input);

        return VarInt.ReadVInt(input);
    }
}
