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
using Hollow.Core.Read;
using Hollow.Core.Read.Engine;

namespace Hollow.Core.Tools.Split;

/// <summary>
/// Says which shard each top-level record of a split belongs in.
/// </summary>
/// <remarks>
/// Only the top-level types are directed. Everything else follows the records that reference it, into
/// however many shards those landed in.
/// <para>
/// Named <c>HollowSplitterCopyDirector</c> in Java; the leading I is the .NET convention for an
/// interface.
/// </para>
/// </remarks>
public interface IHollowSplitterCopyDirector
{
    /// <summary>A record that belongs in every shard rather than one.</summary>
    const int Replicated = -1;

    /// <summary>The types whose records are divided; everything else follows them.</summary>
    string[] TopLevelTypes { get; }

    /// <summary>How many shards to divide into.</summary>
    int NumShards { get; }

    /// <summary>
    /// The shard <paramref name="ordinal"/> of <paramref name="topLevelType"/> belongs in, or
    /// <see cref="Replicated"/> for a record every shard should hold.
    /// </summary>
    int GetShard(HollowTypeReadState topLevelType, int ordinal);
}

/// <summary>
/// Divides records by ordinal.
/// </summary>
/// <remarks>
/// Simple and even, but not reproducible: an ordinal means nothing across two states, so the same
/// record can land in a different shard next cycle. Use
/// <see cref="HollowSplitterPrimaryKeyCopyDirector"/> where the shards have to be stable.
/// </remarks>
public sealed class HollowSplitterOrdinalCopyDirector(int numShards, params string[] topLevelTypes)
    : IHollowSplitterCopyDirector
{
    /// <inheritdoc />
    public string[] TopLevelTypes { get; } = topLevelTypes;

    /// <inheritdoc />
    public int NumShards { get; } = numShards;

    /// <inheritdoc />
    public int GetShard(HollowTypeReadState topLevelType, int ordinal) => ordinal % NumShards;
}

/// <summary>
/// Divides records by primary key, so that a record always lands in the same shard.
/// </summary>
public sealed class HollowSplitterPrimaryKeyCopyDirector : IHollowSplitterCopyDirector
{
    private readonly List<string> _topLevelTypes;
    private readonly Dictionary<string, HollowPrimaryKeyValueDeriver> _deriversByType;

    /// <summary>
    /// Divides <paramref name="stateEngine"/> into <paramref name="numShards"/> by
    /// <paramref name="keys"/>, one key per type to divide.
    /// </summary>
    public HollowSplitterPrimaryKeyCopyDirector(
        HollowReadStateEngine stateEngine, int numShards, params PrimaryKey[] keys)
    {
        ArgumentNullException.ThrowIfNull(stateEngine);
        ArgumentNullException.ThrowIfNull(keys);

        NumShards = numShards;
        _topLevelTypes = [.. keys.Select(key => key.Type)];
        _deriversByType = keys.ToDictionary(
            key => key.Type,
            key => new HollowPrimaryKeyValueDeriver(key, stateEngine),
            StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public string[] TopLevelTypes => [.. _topLevelTypes];

    /// <inheritdoc />
    public int NumShards { get; }

    /// <summary>
    /// Puts every record of these types into every shard.
    /// </summary>
    /// <remarks>
    /// For the small reference data a shard needs whole — a lookup table every record consults — rather
    /// than a slice of. They are top-level types with no key, which is what makes
    /// <see cref="GetShard"/> answer <see cref="IHollowSplitterCopyDirector.Replicated"/> for them.
    /// </remarks>
    public void AddReplicatedTypes(params string[] replicatedTypes)
    {
        ArgumentNullException.ThrowIfNull(replicatedTypes);

        _topLevelTypes.AddRange(replicatedTypes);
    }

    /// <inheritdoc />
    public int GetShard(HollowTypeReadState topLevelType, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(topLevelType);

        if (!_deriversByType.TryGetValue(topLevelType.Schema.Name, out HollowPrimaryKeyValueDeriver? deriver))
        {
            return IHollowSplitterCopyDirector.Replicated;
        }

        // Taken unsigned, so that the answer is a shard number rather than occasionally the sentinel
        // that means every shard. Java takes a signed hash modulo the shard count, so about half of
        // all keys come out negative and get replicated into every shard instead of placed in one.
        return (int)((uint)HashKey(deriver.GetRecordKey(ordinal)) % (uint)NumShards);
    }

    /// <summary>
    /// The hash a key is divided by, which is Java's <c>Arrays.hashCode</c> over the key's values.
    /// </summary>
    public static int HashKey(object?[] key)
    {
        ArgumentNullException.ThrowIfNull(key);

        int hash = 1;

        foreach (object? value in key)
        {
            hash = (31 * hash) + HollowReadFieldUtils.HashObject(value);
        }

        return hash;
    }
}
