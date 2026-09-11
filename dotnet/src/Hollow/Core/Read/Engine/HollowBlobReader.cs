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

using Hollow.Core.Memory.Encoding;
using Hollow.Core.Read.Engine.List;
using Hollow.Core.Read.Engine.Map;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Read.Engine.Set;
using Hollow.Core.Read.Filter;
using Hollow.Core.Schema;

namespace Hollow.Core.Read.Engine;

/// <summary>
/// Populates a <see cref="HollowReadStateEngine"/> from a snapshot blob.
/// </summary>
/// <remarks>
/// <strong>Port note.</strong> Reverse-delta application and optional blob parts are not ported — see
/// <c>PORTING.md</c>.
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
    public void ReadSnapshot(HollowBlobInput input, ITypeFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(input);

        HollowBlobHeader header = _headerReader.ReadHeader(input);

        filter ??= TypeFilter.IncludeAll;
        filter = filter.Resolve(header.Schemas);

        _stateEngine.HeaderTags = header.HeaderTags;
        _stateEngine.RandomizedTag = header.DestinationRandomizedTag;

        int numStates = VarInt.ReadVInt(input);
        for (int i = 0; i < numStates; i++)
        {
            ReadTypeStateSnapshot(input, filter);
        }

        _stateEngine.WireSchemaReferences();
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
    /// A delta names the state it applies to by its randomized tag; applying it to any other state
    /// would silently corrupt the data, so a mismatch is rejected.
    /// </remarks>
    /// <exception cref="InvalidDataException">The delta does not apply to this state.</exception>
    /// <exception cref="NotSupportedException">The delta changes a collection type.</exception>
    public void ApplyDelta(HollowBlobInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        HollowBlobHeader header = _headerReader.ReadHeader(input);

        if (_stateEngine.RandomizedTag != 0 && header.OriginRandomizedTag != _stateEngine.RandomizedTag)
        {
            throw new InvalidDataException(
                $"This delta originates from a state with randomized tag {header.OriginRandomizedTag}, "
                + $"but the current state's tag is {_stateEngine.RandomizedTag}.");
        }

        _stateEngine.HeaderTags = header.HeaderTags;
        _stateEngine.RandomizedTag = header.DestinationRandomizedTag;

        _stateEngine.NotifyBeginUpdate();

        int numStates = VarInt.ReadVInt(input);
        for (int i = 0; i < numStates; i++)
        {
            ReadTypeStateDelta(input);
        }

        _stateEngine.NotifyEndUpdate();
    }

    private void ReadTypeStateDelta(HollowBlobInput input)
    {
        HollowSchema schema = HollowSchema.ReadFrom(input);
        int numShards = ReadNumShards(input);

        HollowTypeReadState? typeState = _stateEngine.GetTypeState(schema.Name);

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

            case null:
                throw new InvalidDataException(
                    $"The delta changes type {schema.Name}, which is not present in this state.");

            default:
                throw new InvalidDataException(
                    $"The delta declares type {schema.Name} as a {schema.SchemaType} type, which does not "
                    + $"match the {typeState.Schema.SchemaType} type held in this state.");
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
