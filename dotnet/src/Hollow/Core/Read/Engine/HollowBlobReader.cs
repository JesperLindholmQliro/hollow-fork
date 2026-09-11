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
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Read.Filter;
using Hollow.Core.Schema;

namespace Hollow.Core.Read.Engine;

/// <summary>
/// Populates a <see cref="HollowReadStateEngine"/> from a snapshot blob.
/// </summary>
/// <remarks>
/// <strong>Port note.</strong> Delta and reverse-delta application, and optional blob parts, are not
/// ported — see <c>PORTING.md</c>.
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

        int numStates = VarInt.ReadVInt(input);
        for (int i = 0; i < numStates; i++)
        {
            ReadTypeStateSnapshot(input, filter);
        }

        _stateEngine.WireSchemaReferences();
    }

    private void ReadTypeStateSnapshot(HollowBlobInput input, ITypeFilter filter)
    {
        HollowSchema schema = HollowSchema.ReadFrom(input);
        int numShards = ReadNumShards(input);

        switch (schema)
        {
            case HollowObjectSchema objectSchema when filter.Includes(objectSchema.Name):
                HollowObjectSchema filteredSchema = objectSchema.FilterSchema(filter);
                HollowObjectTypeReadState typeState = new(
                    _stateEngine, _stateEngine.MemoryMode, filteredSchema, objectSchema);
                PopulateTypeStateSnapshot(input, typeState, numShards);
                break;

            case HollowObjectSchema objectSchema:
                HollowObjectTypeDataElements.DiscardFromInput(input, objectSchema, numShards);
                SnapshotPopulatedOrdinalsReader.DiscardOrdinals(input);
                break;

            default:
                throw new NotSupportedException(
                    $"Type {schema.Name} is a {schema.SchemaType} type, which the .NET port does not yet "
                    + "read; see PORTING.md");
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
