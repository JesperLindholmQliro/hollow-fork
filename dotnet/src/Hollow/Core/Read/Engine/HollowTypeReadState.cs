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
using Hollow.Core.Memory.Pool;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Schema;
using Hollow.Core.Tools.Checksum;
using Hollow.Core.Util;

namespace Hollow.Core.Read.Engine;

/// <summary>
/// Contains, and is the root handle to, all the records of a specific type in a
/// <see cref="HollowReadStateEngine"/>.
/// </summary>
public abstract class HollowTypeReadState : IHollowTypeDataAccess
{
    private IHollowTypeStateListener[] _stateListeners = [];

    /// <summary>
    /// Initialises a read state for <paramref name="schema"/> within <paramref name="stateEngine"/>.
    /// </summary>
    protected HollowTypeReadState(HollowReadStateEngine stateEngine, MemoryMode memoryMode, HollowSchema schema)
    {
        StateEngine = stateEngine;
        MemoryMode = memoryMode;
        Schema = schema;
    }

    /// <summary>The state engine this type belongs to.</summary>
    public HollowReadStateEngine StateEngine { get; }

    /// <summary>The memory mode this type's data is held in.</summary>
    public MemoryMode MemoryMode { get; }

    /// <summary>The schema of this type.</summary>
    public HollowSchema Schema { get; }

    /// <summary>The name of this type.</summary>
    public string TypeName => Schema.Name;

    /// <inheritdoc />
    public IHollowDataAccess DataAccess => StateEngine;

    /// <inheritdoc />
    public HollowTypeReadState TypeState => this;

    /// <summary>The maximum ordinal currently populated in this type state.</summary>
    public abstract int MaxOrdinal { get; }

    /// <summary>An approximation of the memory this type's records occupy, in bytes.</summary>
    public abstract long ApproxHeapFootprintInBytes { get; }

    /// <summary>
    /// An approximation of how much of <see cref="ApproxHeapFootprintInBytes"/> is spent on ordinals
    /// holding nothing, in bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A removed record leaves its ordinal behind, and the fixed-length storage is one flat run indexed
    /// by ordinal, so the space that ordinal took is still allocated until something reuses it. This
    /// counts that space, which is what says whether a type is worth compacting.
    /// </para>
    /// <para>
    /// Only fixed-length storage is counted: a hole's variable-length bytes are still there too, but
    /// finding out how many would mean reading a record that is no longer valid.
    /// </para>
    /// </remarks>
    public long ApproxHoleCostInBytes
    {
        get
        {
            HollowTypeReadStateShard[] shards = ShardsVolatile.Shards;

            if (shards.Length == 0)
            {
                return 0;
            }

            BitSet populatedOrdinals = PopulatedOrdinals;
            int shardNumberMask = shards.Length - 1;
            int maxOrdinal = MaxOrdinal;
            long holeBits = 0;

            for (int hole = populatedOrdinals.NextClearBit(0);
                hole <= maxOrdinal;
                hole = populatedOrdinals.NextClearBit(hole + 1))
            {
                holeBits += BitsPerRecord(shards[hole & shardNumberMask]);
            }

            return holeBits / 8;
        }
    }

    /// <summary>
    /// The fixed-length bits one record of <paramref name="shard"/> occupies.
    /// </summary>
    /// <remarks>
    /// Each record kind lays its fixed-length portion out differently — an object's is its fields, a
    /// collection's is the pointer ending its element run — so only the subclass can say. Java asks each
    /// shard for its own hole cost instead; asking for the width and doing the counting once keeps the
    /// four implementations from being the same loop four times.
    /// </remarks>
    private protected abstract int BitsPerRecord(HollowTypeReadStateShard shard);

    /// <summary>
    /// The number of shards this type's records are split across, or 0 before a blob has been read.
    /// </summary>
    /// <remarks>
    /// A producer restoring from this state has to adopt the same shard count, because a record's
    /// ordinal determines which shard holds it and a delta is written per shard.
    /// </remarks>
    public int NumShards => ShardsVolatile.Shards.Length;

    /// <summary>
    /// This type's shards as they stand, which resharding replaces wholesale.
    /// </summary>
    public abstract ShardsHolder ShardsVolatile { get; }

    /// <summary>
    /// Publishes <paramref name="shards"/> as this type's shards.
    /// </summary>
    internal abstract void UpdateShards(HollowTypeReadStateShard[] shards);

    /// <summary>
    /// Creates an array able to hold <paramref name="length"/> of this record kind's data elements.
    /// </summary>
    internal abstract HollowTypeDataElements[] CreateTypeDataElements(int length);

    /// <summary>
    /// Wraps <paramref name="elements"/> as a shard of this record kind.
    /// </summary>
    internal abstract HollowTypeReadStateShard CreateTypeReadStateShard(
        HollowTypeDataElements elements, int shardOrdinalShift);

    /// <summary>The listeners currently associated with this type.</summary>
    public IReadOnlyList<IHollowTypeStateListener> Listeners => _stateListeners;

    /// <summary>
    /// The ordinals currently populated in this type state.
    /// </summary>
    /// <remarks>Do not modify the returned bit set.</remarks>
    public BitSet PopulatedOrdinals =>
        GetListener<PopulatedOrdinalListener>()?.PopulatedOrdinals
        ?? throw new InvalidOperationException($"no {nameof(PopulatedOrdinalListener)} is attached to {TypeName}");

    /// <summary>
    /// The ordinals populated in this type state prior to the previous delta transition.
    /// </summary>
    /// <remarks>Do not modify the returned bit set.</remarks>
    public BitSet PreviousOrdinals =>
        GetListener<PopulatedOrdinalListener>()?.PreviousOrdinals
        ?? throw new InvalidOperationException($"no {nameof(PopulatedOrdinalListener)} is attached to {TypeName}");

    /// <summary>Adds a listener to this type.</summary>
    public void AddListener(IHollowTypeStateListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        _stateListeners = [.. _stateListeners, listener];
    }

    /// <summary>Removes a specific listener from this type.</summary>
    public void RemoveListener(IHollowTypeStateListener listener) =>
        _stateListeners = [.. _stateListeners.Where(existing => !ReferenceEquals(existing, listener))];

    /// <summary>
    /// Gets the listener of type <typeparamref name="T"/> attached to this type, or
    /// <see langword="null"/> when none is attached.
    /// </summary>
    /// <remarks>
    /// Java takes a <c>Class&lt;T&gt;</c> argument because it cannot recover the type parameter at
    /// runtime; C# generics make the argument unnecessary.
    /// </remarks>
    public T? GetListener<T>()
        where T : class, IHollowTypeStateListener =>
        _stateListeners.OfType<T>().FirstOrDefault();

    /// <summary>
    /// Reads a snapshot of this type's records from <paramref name="input"/>.
    /// </summary>
    /// <param name="input">The blob to read from.</param>
    /// <param name="memoryRecycler">The pool to draw record storage from.</param>
    /// <param name="numShards">The number of shards the type's records are split across.</param>
    public abstract void ReadSnapshot(HollowBlobInput input, IArraySegmentRecycler memoryRecycler, int numShards);

    /// <summary>
    /// Returns every segment of this type's record storage to <paramref name="memoryRecycler"/>.
    /// </summary>
    public abstract void Destroy(IArraySegmentRecycler memoryRecycler);

    /// <summary>
    /// Computes a checksum over this type's records, covering only the fields this type's schema shares
    /// with <paramref name="withSchema"/>.
    /// </summary>
    /// <remarks>
    /// The restriction to common fields is what lets two states either side of a schema change be
    /// compared at all. For a collection type there is nothing to intersect, so the schemas must match.
    /// </remarks>
    public HollowChecksum GetChecksum(HollowSchema withSchema)
    {
        ArgumentNullException.ThrowIfNull(withSchema);

        HollowChecksum checksum = new();
        ApplyToChecksum(checksum, withSchema);

        return checksum;
    }

    /// <summary>
    /// Folds this type's records into <paramref name="checksum"/>.
    /// </summary>
    protected abstract void ApplyToChecksum(HollowChecksum checksum, HollowSchema withSchema);

    /// <summary>
    /// Notifies the attached listeners that a delta update is beginning.
    /// </summary>
    internal void BeginUpdate() => NotifyBeginUpdate();

    /// <summary>
    /// Notifies the attached listeners that a delta update has finished.
    /// </summary>
    internal void EndUpdate() => NotifyEndUpdate();

    /// <summary>
    /// Notifies the attached listeners that a delta update is beginning.
    /// </summary>
    protected void NotifyBeginUpdate()
    {
        foreach (IHollowTypeStateListener listener in _stateListeners)
        {
            listener.BeginUpdate();
        }
    }

    /// <summary>
    /// Notifies the attached listeners that a delta update has finished.
    /// </summary>
    protected void NotifyEndUpdate()
    {
        foreach (IHollowTypeStateListener listener in _stateListeners)
        {
            listener.EndUpdate();
        }
    }

    /// <summary>
    /// Notifies the attached listeners that <paramref name="ordinal"/> was added.
    /// </summary>
    protected void NotifyAddedOrdinal(int ordinal)
    {
        foreach (IHollowTypeStateListener listener in _stateListeners)
        {
            listener.AddedOrdinal(ordinal);
        }
    }

    /// <summary>
    /// Notifies the attached listeners that <paramref name="ordinal"/> was removed.
    /// </summary>
    protected void NotifyRemovedOrdinal(int ordinal)
    {
        foreach (IHollowTypeStateListener listener in _stateListeners)
        {
            listener.RemovedOrdinal(ordinal);
        }
    }
}

/// <summary>
/// Receives callbacks when deltas are applied to the type it is registered with.
/// </summary>
/// <remarks>
/// Named <c>HollowTypeStateListener</c> in Java; the <c>I</c> prefix follows the .NET interface naming
/// convention.
/// </remarks>
public interface IHollowTypeStateListener
{
    /// <summary>Called immediately before a delta update is applied to the state engine.</summary>
    void BeginUpdate();

    /// <summary>Called once for each record added to the registered type.</summary>
    void AddedOrdinal(int ordinal);

    /// <summary>Called once for each record removed from the registered type.</summary>
    void RemovedOrdinal(int ordinal);

    /// <summary>Called immediately after a delta update is applied to the state engine.</summary>
    void EndUpdate();
}

/// <summary>
/// Tracks the populated and previously populated ordinals of a type.
/// </summary>
/// <remarks>
/// Unless explicitly suppressed, one of these is registered automatically with each type in a
/// <see cref="HollowReadStateEngine"/>.
/// </remarks>
public sealed class PopulatedOrdinalListener : IHollowTypeStateListener
{
    /// <summary>The ordinals currently populated.</summary>
    public BitSet PopulatedOrdinals { get; } = new();

    /// <summary>The ordinals populated before the last delta transition began.</summary>
    public BitSet PreviousOrdinals { get; } = new();

    /// <summary>Whether the populated ordinals changed in the last cycle.</summary>
    public bool UpdatedLastCycle() => !PopulatedOrdinals.Equals(PreviousOrdinals);

    /// <inheritdoc />
    public void BeginUpdate()
    {
        PreviousOrdinals.Clear();
        PreviousOrdinals.Or(PopulatedOrdinals);
    }

    /// <inheritdoc />
    public void AddedOrdinal(int ordinal) => PopulatedOrdinals.Set(ordinal);

    /// <inheritdoc />
    public void RemovedOrdinal(int ordinal) => PopulatedOrdinals.Clear(ordinal);

    /// <inheritdoc />
    public void EndUpdate()
    {
        // Nothing to do; the bit sets are already up to date.
    }
}
