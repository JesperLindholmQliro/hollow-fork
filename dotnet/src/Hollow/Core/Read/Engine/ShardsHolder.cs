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
using Hollow.Core.Memory.Pool;

namespace Hollow.Core.Read.Engine;

/// <summary>
/// The in-memory record storage of one shard of a type.
/// </summary>
/// <remarks>
/// A type's records are split across a power-of-two number of shards by the low bits of their ordinal,
/// so that no single allocation has to hold the whole type. Each shard's storage is one of these.
/// </remarks>
public abstract class HollowTypeDataElements
{
    /// <summary>
    /// Initialises empty storage drawing from <paramref name="memoryRecycler"/>.
    /// </summary>
    protected HollowTypeDataElements(IArraySegmentRecycler memoryRecycler)
    {
        ArgumentNullException.ThrowIfNull(memoryRecycler);

        MemoryRecycler = memoryRecycler;
    }

    /// <summary>The highest ordinal this shard holds, or -1 when it holds none.</summary>
    public int MaxOrdinal { get; internal set; } = -1;

    /// <summary>The pool this shard's storage is drawn from and returned to.</summary>
    public IArraySegmentRecycler MemoryRecycler { get; }

    /// <summary>
    /// The ordinals a delta adds, set only on the data elements read from a delta blob.
    /// </summary>
    public GapEncodedVariableLengthIntegerReader? EncodedAdditions { get; internal set; }

    /// <summary>
    /// The ordinals a delta removes, set only on the data elements read from a delta blob.
    /// </summary>
    public GapEncodedVariableLengthIntegerReader? EncodedRemovals { get; internal set; }

    /// <summary>
    /// Returns every segment of this shard's storage to the recycler.
    /// </summary>
    public abstract void Destroy();
}

/// <summary>
/// One shard of a type's records, together with the shift that turns a global ordinal into an ordinal
/// within the shard.
/// </summary>
/// <remarks>
/// The shard a record lives in is the low bits of its ordinal, and its position within that shard is
/// the remaining high bits — so with <c>n</c> shards, global ordinal <c>o</c> is shard
/// <c>o &amp; (n - 1)</c> at shard ordinal <c>o &gt;&gt; log2(n)</c>.
/// </remarks>
public abstract class HollowTypeReadStateShard
{
    /// <summary>
    /// Initialises a shard over <paramref name="shardOrdinalShift"/>.
    /// </summary>
    protected HollowTypeReadStateShard(int shardOrdinalShift) => ShardOrdinalShift = shardOrdinalShift;

    /// <summary>How far right to shift a global ordinal to get an ordinal within this shard.</summary>
    public int ShardOrdinalShift { get; }

    /// <summary>This shard's records.</summary>
    public abstract HollowTypeDataElements Elements { get; }
}

/// <summary>
/// A type's shards and the mask that selects one, held together so that both are published at once.
/// </summary>
/// <remarks>
/// Resharding replaces a type's shard array while readers are using it. Holding the array and its mask
/// in one object means a reader either sees the old pair or the new pair, never a new array with an old
/// mask — which would send it to the wrong shard.
/// </remarks>
public abstract class ShardsHolder
{
    /// <summary>
    /// Initialises a holder over <paramref name="shardCount"/> shards.
    /// </summary>
    /// <exception cref="ArgumentException">The shard count is not a power of two.</exception>
    protected ShardsHolder(int shardCount)
    {
        if (shardCount != 0 && (shardCount & (shardCount - 1)) != 0)
        {
            throw new ArgumentException(
                $"A type's shard count must be a power of two, not {shardCount}.", nameof(shardCount));
        }

        ShardNumberMask = shardCount - 1;
    }

    /// <summary>The shards, indexed by the low bits of a global ordinal.</summary>
    public abstract HollowTypeReadStateShard[] Shards { get; }

    /// <summary>The mask that turns a global ordinal into a shard index.</summary>
    public int ShardNumberMask { get; }
}

/// <summary>
/// A type's shards, typed to the shard class of the record kind holding them.
/// </summary>
/// <remarks>
/// Java gives each record kind its own holder class so that <c>getShards()</c> can return a covariant
/// array; a type parameter does the same job here.
/// </remarks>
/// <typeparam name="TShard">The shard class of this record kind.</typeparam>
public sealed class ShardsHolder<TShard> : ShardsHolder
    where TShard : HollowTypeReadStateShard
{
    /// <summary>
    /// Holds <paramref name="shards"/> and the mask that selects one of them.
    /// </summary>
    public ShardsHolder(TShard[] shards)
        : base(Argument(shards).Length) => TypedShards = shards;

    /// <summary>The shards, typed to this record kind.</summary>
    public TShard[] TypedShards { get; }

    /// <inheritdoc />
    public override HollowTypeReadStateShard[] Shards => TypedShards;

    /// <summary>A holder over no shards, for a type that has not read a blob yet.</summary>
    public static ShardsHolder<TShard> Empty { get; } = new([]);

    private static TShard[] Argument(TShard[] shards)
    {
        ArgumentNullException.ThrowIfNull(shards);

        return shards;
    }
}
