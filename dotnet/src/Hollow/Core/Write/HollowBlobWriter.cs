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
using Hollow.Core.Schema;

namespace Hollow.Core.Write;

/// <summary>
/// Serialises a <see cref="HollowWriteStateEngine"/> as a snapshot blob.
/// </summary>
public sealed class HollowBlobWriter
{
    private readonly HollowWriteStateEngine _stateEngine;
    private readonly HollowBlobHeaderWriter _headerWriter = new();

    /// <summary>
    /// Initialises a writer over <paramref name="stateEngine"/>.
    /// </summary>
    public HollowBlobWriter(HollowWriteStateEngine stateEngine)
    {
        ArgumentNullException.ThrowIfNull(stateEngine);
        _stateEngine = stateEngine;
    }

    /// <summary>
    /// Writes only the header of this state — its schemas and header tags, without any records — to
    /// <paramref name="stream"/>.
    /// </summary>
    public void WriteHeader(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using HollowBlobOutput output = HollowBlobOutput.Serial(stream, leaveOpen: true);
        WriteHeader(output);
    }

    /// <summary>
    /// Writes only the header of this state — its schemas and header tags, without any records.
    /// </summary>
    /// <remarks>
    /// A producer publishes one of these alongside each version so that a caller can read a version's
    /// data model without downloading its records.
    /// </remarks>
    public void WriteHeader(HollowBlobOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);

        _stateEngine.PrepareForWrite(canReshard: true);

        HollowBlobHeader header = new()
        {
            Schemas = _stateEngine.Schemas,
            HeaderTags = new Dictionary<string, string>(_stateEngine.HeaderTags, StringComparer.Ordinal),
            OriginRandomizedTag = 0,
            DestinationRandomizedTag = _stateEngine.RandomizedTag,
        };

        _headerWriter.WriteHeader(header, output);

        output.Flush();
    }

    /// <summary>
    /// Writes a snapshot of the whole dataset to <paramref name="stream"/>.
    /// </summary>
    public void WriteSnapshot(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using HollowBlobOutput output = HollowBlobOutput.Serial(stream, leaveOpen: true);
        WriteSnapshot(output);
    }

    /// <summary>
    /// Writes a snapshot of the whole dataset to <paramref name="output"/>.
    /// </summary>
    public void WriteSnapshot(HollowBlobOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);

        _stateEngine.PrepareForWrite(canReshard: true);

        HollowBlobHeader header = new()
        {
            Schemas = _stateEngine.Schemas,
            HeaderTags = new Dictionary<string, string>(_stateEngine.HeaderTags, StringComparer.Ordinal),
            OriginRandomizedTag = 0,
            DestinationRandomizedTag = _stateEngine.RandomizedTag,
        };

        _headerWriter.WriteHeader(header, output);

        VarInt.WriteVInt(output, _stateEngine.OrderedTypeStates.Count);

        foreach (HollowTypeWriteState typeState in _stateEngine.OrderedTypeStates)
        {
            typeState.CalculateSnapshot();

            typeState.Schema.WriteTo(output);
            WriteNumShards(output, typeState.NumShards);
            typeState.WriteSnapshot(output);
        }

        output.Flush();
    }

    /// <summary>
    /// Writes a delta taking a consumer from the previous cycle's state to this one, to
    /// <paramref name="stream"/>.
    /// </summary>
    public void WriteDelta(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using HollowBlobOutput output = HollowBlobOutput.Serial(stream, leaveOpen: true);
        WriteDelta(output);
    }

    /// <summary>
    /// Writes a delta taking a consumer from the previous cycle's state to this one.
    /// </summary>
    /// <remarks>
    /// Only the types whose records changed appear in a delta, so a consumer leaves the rest alone.
    /// </remarks>
    public void WriteDelta(HollowBlobOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);

        _stateEngine.EnsureAllNecessaryStatesRestored();
        _stateEngine.PrepareForWrite(canReshard: true);

        List<HollowTypeWriteState> changedTypes =
            [.. _stateEngine.OrderedTypeStates.Where(state => state.HasChangedSinceLastCycle())];

        HollowBlobHeader header = new()
        {
            Schemas = [.. changedTypes.Select(state => state.Schema)],
            HeaderTags = new Dictionary<string, string>(_stateEngine.HeaderTags, StringComparer.Ordinal),
            OriginRandomizedTag = _stateEngine.PreviousRandomizedTag,
            DestinationRandomizedTag = _stateEngine.RandomizedTag,
        };

        _headerWriter.WriteHeader(header, output);

        VarInt.WriteVInt(output, changedTypes.Count);

        foreach (HollowTypeWriteState typeState in changedTypes)
        {
            typeState.CalculateDelta();

            typeState.Schema.WriteTo(output);
            WriteNumShards(output, typeState.NumShards);
            typeState.WriteDelta(output);
        }

        output.Flush();
    }

    /// <summary>
    /// Writes a reverse delta taking a consumer from this cycle's state back to the previous one, to
    /// <paramref name="stream"/>.
    /// </summary>
    public void WriteReverseDelta(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using HollowBlobOutput output = HollowBlobOutput.Serial(stream, leaveOpen: true);
        WriteReverseDelta(output);
    }

    /// <summary>
    /// Writes a reverse delta taking a consumer from this cycle's state back to the previous one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The blob is in the same format as a forward delta, and a consumer applies it the same way. Only
    /// the header differs: it names this cycle's state as the origin and the previous cycle's as the
    /// destination, and carries the header tags of the state being returned to.
    /// </para>
    /// <para>
    /// The records a reverse delta carries are the ones the last cycle dropped. They are still in the
    /// ordinal map — compaction only discards what was already gone a cycle earlier — so this has to be
    /// written before the next <see cref="HollowWriteStateEngine.PrepareForNextCycle"/>.
    /// </para>
    /// </remarks>
    public void WriteReverseDelta(HollowBlobOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);

        _stateEngine.EnsureAllNecessaryStatesRestored();
        _stateEngine.PrepareForWrite(canReshard: true);

        List<HollowTypeWriteState> changedTypes =
            [.. _stateEngine.OrderedTypeStates.Where(state => state.HasChangedSinceLastCycle())];

        HollowBlobHeader header = new()
        {
            Schemas = [.. changedTypes.Select(state => state.Schema)],
            HeaderTags = new Dictionary<string, string>(_stateEngine.PreviousHeaderTags, StringComparer.Ordinal),
            OriginRandomizedTag = _stateEngine.RandomizedTag,
            DestinationRandomizedTag = _stateEngine.PreviousRandomizedTag,
        };

        _headerWriter.WriteHeader(header, output);

        VarInt.WriteVInt(output, changedTypes.Count);

        foreach (HollowTypeWriteState typeState in changedTypes)
        {
            typeState.CalculateReverseDelta();

            typeState.Schema.WriteTo(output);

            // A reverse delta declares the previous cycle's shard count, because that is the
            // arrangement the consumer it takes back has to end up in.
            WriteNumShards(output, typeState.RevNumShards);
            typeState.WriteReverseDelta(output);
        }

        output.Flush();
    }

    /// <summary>
    /// Writes the shard count inside the forwards-compatibility envelope a pre-2.1.0 reader skips.
    /// </summary>
    private static void WriteNumShards(HollowBlobOutput output, int numShards)
    {
        // The byte count lets a pre-2.1.0 reader skip both the forwards-compatibility block and the
        // shard count it does not understand.
        VarInt.WriteVInt(output, 1 + VarInt.SizeOfVInt(numShards));

        // A 2.1.0 reader skips this block; more data can be added here for older readers to ignore.
        VarInt.WriteVInt(output, 0);

        VarInt.WriteVInt(output, numShards);
    }
}
