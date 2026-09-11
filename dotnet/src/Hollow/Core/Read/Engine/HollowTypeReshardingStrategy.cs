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

using System.Globalization;
using System.Numerics;
using Hollow.Core.Read.Engine.List;
using Hollow.Core.Read.Engine.Map;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Read.Engine.Set;

namespace Hollow.Core.Read.Engine;

/// <summary>
/// Changes the number of shards a type's records are split across, in place, while reads continue.
/// </summary>
/// <remarks>
/// <para>
/// A producer may decide a type has outgrown (or no longer needs) its shard count, and say so in the
/// next delta. The consumer then has to rearrange the records it already holds to the new count before
/// it can apply that delta, because a record's shard is the low bits of its ordinal and the delta is
/// written per shard.
/// </para>
/// <para>
/// The rearrangement proceeds one original shard at a time, so only one shard's worth of records is
/// duplicated at any moment rather than the whole type. Each step publishes a complete new shard array
/// before the storage it replaced is released, so a reader either sees the arrangement before the step
/// or the one after it.
/// </para>
/// </remarks>
public abstract class HollowTypeReshardingStrategy
{
    private static readonly HollowTypeReshardingStrategy ObjectStrategy = new ObjectReshardingStrategy();
    private static readonly HollowTypeReshardingStrategy ListStrategy = new ListReshardingStrategy();
    private static readonly HollowTypeReshardingStrategy SetStrategy = new SetReshardingStrategy();
    private static readonly HollowTypeReshardingStrategy MapStrategy = new MapReshardingStrategy();

    /// <summary>
    /// The strategy for <paramref name="typeState"/>'s record kind.
    /// </summary>
    /// <remarks>Named <c>getInstance</c> in Java.</remarks>
    /// <exception cref="ArgumentException">The record kind is not one that reshards.</exception>
    public static HollowTypeReshardingStrategy ForType(HollowTypeReadState typeState) =>
        typeState switch
        {
            HollowObjectTypeReadState => ObjectStrategy,
            HollowListTypeReadState => ListStrategy,
            HollowSetTypeReadState => SetStrategy,
            HollowMapTypeReadState => MapStrategy,
            null => throw new ArgumentNullException(nameof(typeState)),
            _ => throw new ArgumentException(
                $"{typeState.GetType().Name} is not a record kind that reshards.", nameof(typeState)),
        };

    /// <summary>
    /// How many shards each old shard becomes, or how many old shards make up each new one.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The counts are not both positive, or one is not a whole multiple of the other.
    /// </exception>
    public static int ShardingFactor(int oldNumShards, int newNumShards)
    {
        if (oldNumShards <= 0 || newNumShards <= 0 || oldNumShards == newNumShards)
        {
            throw new InvalidOperationException(
                $"A type cannot be resharded from {oldNumShards} to {newNumShards} shards.");
        }

        bool growing = newNumShards > oldNumShards;
        int dividend = growing ? newNumShards : oldNumShards;
        int divisor = growing ? oldNumShards : newNumShards;

        if (dividend % divisor != 0)
        {
            throw new InvalidOperationException(
                $"A type cannot be resharded from {oldNumShards} to {newNumShards} shards.");
        }

        return dividend / divisor;
    }

    /// <summary>
    /// Rearranges <paramref name="typeState"/>'s records from <paramref name="prevNumShards"/> shards
    /// into <paramref name="newNumShards"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The rearrangement failed part way through, which leaves the type in an unusable state that only a
    /// fresh snapshot can recover from.
    /// </exception>
    public void Reshard(HollowTypeReadState typeState, int prevNumShards, int newNumShards)
    {
        ArgumentNullException.ThrowIfNull(typeState);

        int shardingFactor = ShardingFactor(prevNumShards, newNumShards);

        try
        {
            if (newNumShards > prevNumShards)
            {
                SplitShards(typeState, prevNumShards, shardingFactor);
            }
            else
            {
                JoinShards(typeState, newNumShards, shardingFactor);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Failed to reshard {typeState.Schema.SchemaType} type {typeState.Schema.Name} from "
                    + $"{prevNumShards} to {newNumShards} shards. The read state is now corrupt and only a "
                    + $"snapshot update can recover it. Its maximum ordinal was {typeState.MaxOrdinal} and its "
                    + $"schema is {typeState.Schema}."),
                e);
        }
    }

    /// <summary>Divides one shard's records into <paramref name="shardingFactor"/> shards' worth.</summary>
    protected abstract HollowTypeDataElements[] SplitDataElements(
        HollowTypeDataElements from, int shardingFactor);

    /// <summary>Merges several shards' records into one shard's worth.</summary>
    protected abstract HollowTypeDataElements JoinDataElements(HollowTypeDataElements[] from);

    /// <summary>
    /// Grows the shard count, which sends a fraction of each old shard's records to a new shard.
    /// </summary>
    private void SplitShards(HollowTypeReadState typeState, int prevNumShards, int shardingFactor)
    {
        // Step one widens the array without touching any records: each old shard is repeated across the
        // new slots that will eventually take its records. Every ordinal still reaches storage that
        // holds it — through the old, wider, shard ordinal shift — so reads stay correct meanwhile, they
        // just look at a shard holding more than it will once step two has been through it.
        typeState.UpdateShards(Repeat(typeState.ShardsVolatile.Shards, shardingFactor));

        // Step two replaces each repeated old shard with the split of its records that belongs there.
        // Only once every slot derived from an old shard has its own records is that shard released.
        for (int i = 0; i < prevNumShards; i++)
        {
            HollowTypeDataElements original = typeState.ShardsVolatile.Shards[i].Elements;

            typeState.UpdateShards(SplitOneShard(typeState, i, prevNumShards, shardingFactor));

            DestroyDataElements(original);
        }
    }

    /// <summary>
    /// Shrinks the shard count, which gathers several old shards' records into one.
    /// </summary>
    private void JoinShards(HollowTypeReadState typeState, int newNumShards, int shardingFactor)
    {
        // Step one leaves the array the size it is and points each group of old shards at their joined
        // records, under a narrower shard ordinal shift. Reads still select the shard they always did,
        // and the new shift lands them at the right ordinal within the joined storage.
        for (int i = 0; i < newNumShards; i++)
        {
            HollowTypeDataElements[] replaced = JoinCandidates(typeState, i, shardingFactor);

            typeState.UpdateShards(JoinOneShard(typeState, i, shardingFactor));

            foreach (HollowTypeDataElements original in replaced)
            {
                DestroyDataElements(original);
            }
        }

        // Step two drops the trailing slots, which by now are duplicates of the first newNumShards.
        typeState.UpdateShards([.. typeState.ShardsVolatile.Shards.Take(newNumShards)]);
    }

    /// <summary>
    /// Repeats each shard across the wider array, so that every new slot starts out pointing at the old
    /// shard whose records it will be given.
    /// </summary>
    private static HollowTypeReadStateShard[] Repeat(HollowTypeReadStateShard[] shards, int shardingFactor)
    {
        int prevNumShards = shards.Length;
        HollowTypeReadStateShard[] widened = new HollowTypeReadStateShard[prevNumShards * shardingFactor];

        for (int i = 0; i < prevNumShards; i++)
        {
            for (int j = 0; j < shardingFactor; j++)
            {
                widened[i + (prevNumShards * j)] = shards[i];
            }
        }

        return widened;
    }

    /// <summary>
    /// Builds the shard array in which shard <paramref name="currentIndex"/>'s records have been divided
    /// among the slots derived from it.
    /// </summary>
    private HollowTypeReadStateShard[] SplitOneShard(
        HollowTypeReadState typeState, int currentIndex, int prevNumShards, int shardingFactor)
    {
        HollowTypeReadStateShard[] shards = typeState.ShardsVolatile.Shards;
        int newShardOrdinalShift = BitOperations.TrailingZeroCount((uint)shards.Length);

        HollowTypeDataElements[] splits = SplitDataElements(shards[currentIndex].Elements, shardingFactor);

        HollowTypeReadStateShard[] newShards = [.. shards];
        for (int i = 0; i < shardingFactor; i++)
        {
            newShards[currentIndex + (prevNumShards * i)] =
                typeState.CreateTypeReadStateShard(splits[i], newShardOrdinalShift);
        }

        return newShards;
    }

    /// <summary>
    /// Builds the shard array in which the group of shards feeding new shard
    /// <paramref name="currentIndex"/> has been replaced by their joined records.
    /// </summary>
    private HollowTypeReadStateShard[] JoinOneShard(
        HollowTypeReadState typeState, int currentIndex, int shardingFactor)
    {
        HollowTypeReadStateShard[] shards = typeState.ShardsVolatile.Shards;
        int newNumShards = shards.Length / shardingFactor;
        int newShardOrdinalShift = BitOperations.TrailingZeroCount((uint)newNumShards);

        HollowTypeDataElements joined = JoinDataElements(JoinCandidates(typeState, currentIndex, shardingFactor));

        HollowTypeReadStateShard[] newShards = [.. shards];
        for (int i = 0; i < shardingFactor; i++)
        {
            newShards[currentIndex + (newNumShards * i)] =
                typeState.CreateTypeReadStateShard(joined, newShardOrdinalShift);
        }

        return newShards;
    }

    /// <summary>
    /// The shards whose records make up new shard <paramref name="indexIntoShards"/>, in the order their
    /// ordinals interleave.
    /// </summary>
    private static HollowTypeDataElements[] JoinCandidates(
        HollowTypeReadState typeState, int indexIntoShards, int shardingFactor)
    {
        HollowTypeReadStateShard[] shards = typeState.ShardsVolatile.Shards;
        int newNumShards = shards.Length / shardingFactor;

        HollowTypeDataElements[] candidates = typeState.CreateTypeDataElements(shardingFactor);
        for (int i = 0; i < shardingFactor; i++)
        {
            candidates[i] = shards[indexIntoShards + (newNumShards * i)].Elements;
        }

        return candidates;
    }

    /// <summary>
    /// Releases a shard's storage, including the removal list a delta may have left attached to it.
    /// </summary>
    private static void DestroyDataElements(HollowTypeDataElements elements)
    {
        elements.Destroy();
        elements.EncodedRemovals?.Destroy();
    }

    private sealed class ObjectReshardingStrategy : HollowTypeReshardingStrategy
    {
        protected override HollowTypeDataElements[] SplitDataElements(
            HollowTypeDataElements from, int shardingFactor) =>
            new HollowObjectTypeDataElementsSplitter((HollowObjectTypeDataElements)from, shardingFactor).Split();

        protected override HollowTypeDataElements JoinDataElements(HollowTypeDataElements[] from) =>
            new HollowObjectTypeDataElementsJoiner([.. from.Cast<HollowObjectTypeDataElements>()]).Join();
    }

    private sealed class ListReshardingStrategy : HollowTypeReshardingStrategy
    {
        protected override HollowTypeDataElements[] SplitDataElements(
            HollowTypeDataElements from, int shardingFactor) =>
            new HollowListTypeDataElementsSplitter((HollowListTypeDataElements)from, shardingFactor).Split();

        protected override HollowTypeDataElements JoinDataElements(HollowTypeDataElements[] from) =>
            new HollowListTypeDataElementsJoiner([.. from.Cast<HollowListTypeDataElements>()]).Join();
    }

    private sealed class SetReshardingStrategy : HollowTypeReshardingStrategy
    {
        protected override HollowTypeDataElements[] SplitDataElements(
            HollowTypeDataElements from, int shardingFactor) =>
            new HollowSetTypeDataElementsSplitter((HollowSetTypeDataElements)from, shardingFactor).Split();

        protected override HollowTypeDataElements JoinDataElements(HollowTypeDataElements[] from) =>
            new HollowSetTypeDataElementsJoiner([.. from.Cast<HollowSetTypeDataElements>()]).Join();
    }

    private sealed class MapReshardingStrategy : HollowTypeReshardingStrategy
    {
        protected override HollowTypeDataElements[] SplitDataElements(
            HollowTypeDataElements from, int shardingFactor) =>
            new HollowMapTypeDataElementsSplitter((HollowMapTypeDataElements)from, shardingFactor).Split();

        protected override HollowTypeDataElements JoinDataElements(HollowTypeDataElements[] from) =>
            new HollowMapTypeDataElementsJoiner([.. from.Cast<HollowMapTypeDataElements>()]).Join();
    }
}
