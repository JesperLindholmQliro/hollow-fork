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

using Hollow.Api.Producer;

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
    public void WriteSnapshot(HollowBlobOutput output) => WriteSnapshot(output, optionalParts: null);

    /// <summary>
    /// Writes a snapshot of the whole dataset, sending the types assigned to an optional part to that
    /// part's output rather than into the blob.
    /// </summary>
    /// <param name="output">The main blob.</param>
    /// <param name="optionalParts">
    /// Where each part's types go, or <see langword="null"/> to write everything into the blob.
    /// </param>
    public void WriteSnapshot(HollowBlobOutput output, OptionalBlobPartOutputs? optionalParts)
    {
        ArgumentNullException.ThrowIfNull(output);

        _stateEngine.PrepareForWrite(canReshard: true);

        SchemasByPart split = SplitSchemas(_stateEngine.Schemas, optionalParts);

        HollowBlobHeader header = new()
        {
            Schemas = split.Main,
            HeaderTags = new Dictionary<string, string>(_stateEngine.HeaderTags, StringComparer.Ordinal),
            OriginRandomizedTag = 0,
            DestinationRandomizedTag = _stateEngine.RandomizedTag,
        };

        _headerWriter.WriteHeader(header, output);

        WritePartHeaders(optionalParts, split, originTag: 0, destinationTag: _stateEngine.RandomizedTag);

        // Each output gets its own count, so a part reads as a small blob of its own.
        WriteStateCounts(output, optionalParts, typeState => true);

        foreach (HollowTypeWriteState typeState in _stateEngine.OrderedTypeStates)
        {
            typeState.CalculateSnapshot();

            HollowBlobOutput destination = OutputFor(typeState, output, optionalParts);

            typeState.Schema.WriteTo(destination);
            WriteNumShards(destination, typeState.NumShards);
            typeState.WriteSnapshot(destination);
        }

        output.Flush();
        optionalParts?.Flush();
    }

    /// <summary>Where a type's records go: its part's output, or the main blob.</summary>
    private static HollowBlobOutput OutputFor(
        HollowTypeWriteState typeState, HollowBlobOutput output, OptionalBlobPartOutputs? optionalParts) =>
        optionalParts is not null
        && optionalParts.OutputByType.TryGetValue(typeState.Schema.Name, out HollowBlobOutput? part)
            ? part
            : output;

    /// <summary>How many type states each output is about to carry.</summary>
    private void WriteStateCounts(
        HollowBlobOutput output,
        OptionalBlobPartOutputs? optionalParts,
        Func<HollowTypeWriteState, bool> included)
    {
        List<HollowTypeWriteState> states = [.. _stateEngine.OrderedTypeStates.Where(included)];

        if (optionalParts is null)
        {
            VarInt.WriteVInt(output, states.Count);

            return;
        }

        Dictionary<HollowBlobOutput, int> counts = [];

        foreach (HollowTypeWriteState state in states)
        {
            HollowBlobOutput destination = OutputFor(state, output, optionalParts);

            counts[destination] = counts.GetValueOrDefault(destination) + 1;
        }

        VarInt.WriteVInt(output, counts.GetValueOrDefault(output));

        foreach (HollowBlobOutput part in optionalParts.Outputs.Values)
        {
            VarInt.WriteVInt(part, counts.GetValueOrDefault(part));
        }
    }

    /// <summary>The schemas the main blob declares, and the ones each part declares.</summary>
    private sealed record SchemasByPart(
        IReadOnlyList<HollowSchema> Main, IReadOnlyDictionary<string, List<HollowSchema>> ByPart);

    private static SchemasByPart SplitSchemas(
        IReadOnlyList<HollowSchema> schemas, OptionalBlobPartOutputs? optionalParts)
    {
        if (optionalParts is null)
        {
            return new SchemasByPart(schemas, new Dictionary<string, List<HollowSchema>>(StringComparer.Ordinal));
        }

        List<HollowSchema> main = [];
        Dictionary<string, List<HollowSchema>> byPart = new(StringComparer.Ordinal);

        foreach (HollowSchema schema in schemas)
        {
            if (optionalParts.PartNameByType.TryGetValue(schema.Name, out string? part))
            {
                if (!byPart.TryGetValue(part, out List<HollowSchema>? partSchemas))
                {
                    partSchemas = [];
                    byPart[part] = partSchemas;
                }

                partSchemas.Add(schema);
            }
            else
            {
                main.Add(schema);
            }
        }

        return new SchemasByPart(main, byPart);
    }

    /// <summary>
    /// Writes each part's own header, repeating the randomized tags so that a part cannot be applied
    /// against the wrong state.
    /// </summary>
    private void WritePartHeaders(
        OptionalBlobPartOutputs? optionalParts,
        SchemasByPart split,
        long originTag,
        long destinationTag)
    {
        if (optionalParts is null)
        {
            return;
        }

        foreach ((string partName, HollowBlobOutput partOutput) in optionalParts.Outputs)
        {
            HollowBlobOptionalPartHeader partHeader = new(partName)
            {
                OriginRandomizedTag = originTag,
                DestinationRandomizedTag = destinationTag,
                Schemas = split.ByPart.GetValueOrDefault(partName, []),
            };

            _headerWriter.WritePartHeader(partHeader, partOutput);
        }
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
    public void WriteDelta(HollowBlobOutput output) => WriteDelta(output, optionalParts: null);

    /// <summary>
    /// Writes a delta, sending the types assigned to an optional part to that part's output.
    /// </summary>
    /// <remarks>
    /// A part's delta carries only the types of that part that changed, so a part whose types were all
    /// untouched is an almost-empty file rather than an absent one — the consumer still has to be able
    /// to apply it to stay on the chain.
    /// </remarks>
    public void WriteDelta(HollowBlobOutput output, OptionalBlobPartOutputs? optionalParts)
    {
        ArgumentNullException.ThrowIfNull(output);

        _stateEngine.EnsureAllNecessaryStatesRestored();
        _stateEngine.PrepareForWrite(canReshard: true);

        List<HollowTypeWriteState> changedTypes =
            [.. _stateEngine.OrderedTypeStates.Where(state => state.HasChangedSinceLastCycle())];

        SchemasByPart split = SplitSchemas([.. changedTypes.Select(state => state.Schema)], optionalParts);

        HollowBlobHeader header = new()
        {
            Schemas = split.Main,
            HeaderTags = new Dictionary<string, string>(_stateEngine.HeaderTags, StringComparer.Ordinal),
            OriginRandomizedTag = _stateEngine.PreviousRandomizedTag,
            DestinationRandomizedTag = _stateEngine.RandomizedTag,
        };

        _headerWriter.WriteHeader(header, output);

        WritePartHeaders(
            optionalParts,
            split,
            _stateEngine.PreviousRandomizedTag,
            _stateEngine.RandomizedTag);

        WriteStateCounts(output, optionalParts, state => state.HasChangedSinceLastCycle());

        foreach (HollowTypeWriteState typeState in changedTypes)
        {
            typeState.CalculateDelta();

            HollowBlobOutput destination = OutputFor(typeState, output, optionalParts);

            typeState.Schema.WriteTo(destination);
            WriteNumShards(destination, typeState.NumShards);
            typeState.WriteDelta(destination);
        }

        output.Flush();
        optionalParts?.Flush();
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
    public void WriteReverseDelta(HollowBlobOutput output) =>
        WriteReverseDelta(output, optionalParts: null);

    /// <summary>
    /// Writes a reverse delta, sending the types assigned to an optional part to that part's output.
    /// </summary>
    public void WriteReverseDelta(HollowBlobOutput output, OptionalBlobPartOutputs? optionalParts)
    {
        ArgumentNullException.ThrowIfNull(output);

        _stateEngine.EnsureAllNecessaryStatesRestored();
        _stateEngine.PrepareForWrite(canReshard: true);

        List<HollowTypeWriteState> changedTypes =
            [.. _stateEngine.OrderedTypeStates.Where(state => state.HasChangedSinceLastCycle())];

        SchemasByPart split = SplitSchemas([.. changedTypes.Select(state => state.Schema)], optionalParts);

        HollowBlobHeader header = new()
        {
            Schemas = split.Main,
            HeaderTags = new Dictionary<string, string>(_stateEngine.PreviousHeaderTags, StringComparer.Ordinal),
            OriginRandomizedTag = _stateEngine.RandomizedTag,
            DestinationRandomizedTag = _stateEngine.PreviousRandomizedTag,
        };

        _headerWriter.WriteHeader(header, output);

        WritePartHeaders(
            optionalParts,
            split,
            _stateEngine.RandomizedTag,
            _stateEngine.PreviousRandomizedTag);

        WriteStateCounts(output, optionalParts, state => state.HasChangedSinceLastCycle());

        foreach (HollowTypeWriteState typeState in changedTypes)
        {
            typeState.CalculateReverseDelta();

            HollowBlobOutput destination = OutputFor(typeState, output, optionalParts);

            typeState.Schema.WriteTo(destination);

            // A reverse delta declares the previous cycle's shard count, because that is the
            // arrangement the consumer it takes back has to end up in.
            WriteNumShards(destination, typeState.RevNumShards);
            typeState.WriteReverseDelta(destination);
        }

        output.Flush();
        optionalParts?.Flush();
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
