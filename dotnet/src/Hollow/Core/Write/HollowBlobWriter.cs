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

        _stateEngine.PrepareForWrite();

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
    /// <exception cref="NotSupportedException">
    /// A changed type is not an object type. The .NET port can produce collection-type deltas but
    /// cannot yet apply them, so writing one would produce a blob no consumer here could read.
    /// </exception>
    public void WriteDelta(HollowBlobOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);

        _stateEngine.PrepareForWrite();

        List<HollowTypeWriteState> changedTypes =
            [.. _stateEngine.OrderedTypeStates.Where(state => state.HasChangedSinceLastCycle())];

        foreach (HollowTypeWriteState typeState in changedTypes)
        {
            if (typeState.Schema.SchemaType != SchemaType.Object)
            {
                throw new NotSupportedException(
                    $"Type {typeState.Schema.Name} is a {typeState.Schema.SchemaType} type. The .NET port "
                    + "cannot yet apply a delta for collection types, so it will not write one; see "
                    + "PORTING.md.");
            }
        }

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
            typeState.WriteCalculatedDelta(output);
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
