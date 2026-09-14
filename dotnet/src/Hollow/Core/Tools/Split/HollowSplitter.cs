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
using Hollow.Core.Util;
using Hollow.Core.Write;
using Hollow.Core.Write.Copy;

namespace Hollow.Core.Tools.Split;

/// <summary>
/// Divides one read state into several write states.
/// </summary>
/// <remarks>
/// <para>
/// The opposite of <see cref="Combine.HollowCombiner"/>, and the same problem in reverse: a record
/// copied into a shard needs everything it references copied with it, and every reference rewritten
/// to wherever those landed in that shard. A record several shards' roots reference is copied into
/// each of them, because a shard has to stand on its own.
/// </para>
/// <para>
/// Named <c>HollowSplitter</c> in Java, which copies the shards on a <c>SimultaneousExecutor</c>.
/// This port copies them in turn, as it does elsewhere.
/// </para>
/// </remarks>
public sealed class HollowSplitter
{
    private readonly HollowWriteStateEngine[] _outputStateEngines;
    private readonly IHollowSplitterCopyDirector _director;

    /// <summary>
    /// Splits <paramref name="inputStateEngine"/> as <paramref name="director"/> says.
    /// </summary>
    public HollowSplitter(IHollowSplitterCopyDirector director, HollowReadStateEngine inputStateEngine)
    {
        ArgumentNullException.ThrowIfNull(director);
        ArgumentNullException.ThrowIfNull(inputStateEngine);
        ArgumentOutOfRangeException.ThrowIfLessThan(director.NumShards, 1);

        InputStateEngine = inputStateEngine;
        _director = director;

        // Every shard gets the whole data model, whether or not it ends up with a record of each type.
        _outputStateEngines =
        [
            .. Enumerable.Range(0, director.NumShards)
                .Select(_ => HollowWriteStateCreator.CreateWithSchemas(inputStateEngine.Schemas)),
        ];
    }

    /// <summary>The state being split.</summary>
    public HollowReadStateEngine InputStateEngine { get; }

    /// <summary>How many shards the input is being split into.</summary>
    public int NumberOfShards => _outputStateEngines.Length;

    /// <summary>The write state holding shard <paramref name="shardNumber"/>.</summary>
    public HollowWriteStateEngine GetOutputShardStateEngine(int shardNumber) =>
        _outputStateEngines[shardNumber];

    /// <summary>
    /// Copies the input into the shards.
    /// </summary>
    /// <remarks>
    /// Can be called again to split a later state of the same input into the same shards, which is
    /// what the cycle roll at the start is for.
    /// </remarks>
    public void Split()
    {
        foreach (HollowWriteStateEngine output in _outputStateEngines)
        {
            output.PrepareForNextCycle();
        }

        for (int shardNumber = 0; shardNumber < NumberOfShards; shardNumber++)
        {
            new HollowSplitterShardCopier(
                InputStateEngine, _outputStateEngines[shardNumber], _director, shardNumber).Copy();
        }
    }
}

/// <summary>
/// Fills one shard.
/// </summary>
/// <remarks>
/// One of these per shard, each with a remapper of its own — the same record has a different ordinal
/// in each shard, so the tables cannot be shared.
/// </remarks>
public sealed class HollowSplitterShardCopier
{
    private readonly HollowReadStateEngine _input;
    private readonly HollowWriteStateEngine _output;
    private readonly IOrdinalRemapper _ordinalRemapper;
    private readonly IHollowSplitterCopyDirector _director;
    private readonly int _shardNumber;

    private readonly Dictionary<string, HollowRecordCopier> _copiersPerType = new(StringComparer.Ordinal);

    /// <summary>
    /// Copies whatever belongs in <paramref name="shardNumber"/> from <paramref name="input"/> into
    /// <paramref name="shardOutput"/>.
    /// </summary>
    public HollowSplitterShardCopier(
        HollowReadStateEngine input,
        HollowWriteStateEngine shardOutput,
        IHollowSplitterCopyDirector director,
        int shardNumber)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(shardOutput);
        ArgumentNullException.ThrowIfNull(director);

        _input = input;
        _output = shardOutput;
        _director = director;
        _shardNumber = shardNumber;
        _ordinalRemapper = new HollowSplitterOrdinalRemapper(input, this);
    }

    /// <summary>Copies every top-level record the director puts in this shard.</summary>
    /// <exception cref="ArgumentException">
    /// The director names a top-level type the input does not have. Java logs a warning and carries
    /// on, which produces a shard quietly missing a type the caller asked for.
    /// </exception>
    public void Copy()
    {
        foreach (string topLevelType in _director.TopLevelTypes)
        {
            if (_input.GetTypeState(topLevelType) is not { } inputTypeState)
            {
                throw new ArgumentException(
                    $"the director names {topLevelType} as a top-level type, which the input does not have");
            }

            foreach (int ordinal in inputTypeState.PopulatedOrdinals.EnumerateSetBits())
            {
                int directedShard = _director.GetShard(inputTypeState, ordinal);

                if (directedShard == _shardNumber || directedShard == IHollowSplitterCopyDirector.Replicated)
                {
                    CopyRecord(topLevelType, ordinal);
                }
            }
        }
    }

    /// <summary>
    /// Copies one record into this shard, and says where it landed.
    /// </summary>
    /// <remarks>
    /// Reached from the remapper as well as from <see cref="Copy"/>: copying a record remaps the
    /// references it holds, and remapping one copies the record it points at if this shard has not
    /// got it yet.
    /// </remarks>
    internal int CopyRecord(string typeName, int ordinal)
    {
        if (!_copiersPerType.TryGetValue(typeName, out HollowRecordCopier? copier))
        {
            HollowTypeReadState typeState = _input.GetTypeState(typeName)!;

            // Hash positions are never preserved, because preserving them only matters with
            // HollowObjectHashCodeFinder, which this port does not have.
            copier = HollowRecordCopier.Create(
                typeState, typeState.Schema, _ordinalRemapper, preserveHashPositions: false);

            _copiersPerType[typeName] = copier;
        }

        return _output.Add(typeName, copier.Copy(ordinal));
    }
}

/// <summary>
/// Where each of the input's ordinals ended up in one shard.
/// </summary>
/// <remarks>
/// Asking about an ordinal this shard has not got yet copies it, which is what pulls a record's
/// references in behind it.
/// </remarks>
public sealed class HollowSplitterOrdinalRemapper : IOrdinalRemapper
{
    private readonly HollowSplitterShardCopier _shardCopier;
    private readonly Dictionary<string, int[]> _typeMappings;

    /// <summary>
    /// Tracks <paramref name="stateEngine"/>'s ordinals into the shard
    /// <paramref name="shardCopier"/> is filling.
    /// </summary>
    public HollowSplitterOrdinalRemapper(
        HollowReadStateEngine stateEngine, HollowSplitterShardCopier shardCopier)
    {
        ArgumentNullException.ThrowIfNull(stateEngine);
        ArgumentNullException.ThrowIfNull(shardCopier);

        _shardCopier = shardCopier;
        _typeMappings = stateEngine.TypeStates.Values.ToDictionary(
            typeState => typeState.Schema.Name,
            typeState => Unmapped(typeState.MaxOrdinal + 1),
            StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public int GetMappedOrdinal(string type, int originalOrdinal)
    {
        int[] ordinalRemapping = _typeMappings[type];

        if (ordinalRemapping[originalOrdinal] == HollowConstants.OrdinalNone)
        {
            ordinalRemapping[originalOrdinal] = _shardCopier.CopyRecord(type, originalOrdinal);
        }

        return ordinalRemapping[originalOrdinal];
    }

    /// <inheritdoc />
    public void RemapOrdinal(string type, int originalOrdinal, int mappedOrdinal) =>
        _typeMappings[type][originalOrdinal] = mappedOrdinal;

    /// <inheritdoc />
    public bool OrdinalIsMapped(string type, int originalOrdinal) =>
        _typeMappings[type][originalOrdinal] != HollowConstants.OrdinalNone;

    private static int[] Unmapped(int length)
    {
        int[] mapping = new int[length];

        Array.Fill(mapping, HollowConstants.OrdinalNone);

        return mapping;
    }
}
