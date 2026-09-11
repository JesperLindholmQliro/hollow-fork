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
using Hollow.Core.Read.Engine;
using Hollow.Core.Schema;
using Hollow.Core.Util;
using Hollow.Core.Write.Copy;

namespace Hollow.Core.Write;

/// <summary>
/// Accumulates the records of a single type over a producer cycle, assigning each a stable ordinal.
/// </summary>
/// <remarks>
/// <para>
/// Records are deduplicated by their serialised form in a <see cref="ByteArrayOrdinalMap"/>, so adding
/// an identical record twice yields the same ordinal. A bit set tracks which ordinals this cycle
/// populated, which is what the snapshot writes out.
/// </para>
/// <para>
/// A state may instead be <em>restored</em> from a published read state, which is how a producer
/// resumes a delta chain after a restart. See <see cref="RestoreFrom"/>.
/// </para>
/// <para>
/// <strong>Port note.</strong> The Java class also supports partitioned ordinal maps and dynamic
/// resharding across cycles. This port uses a single ordinal map and a fixed shard count — see
/// <c>PORTING.md</c>.
/// </para>
/// </remarks>
public abstract class HollowTypeWriteState
{
    private readonly ThreadLocal<ByteDataArray> _serializedScratchSpace =
        new(() => new ByteDataArray(WastefulRecycler.DefaultInstance));

    private int _numShards;

    /// <summary>
    /// The ordinals held by the state this one was restored from, keyed by each record's serialised
    /// form under <see cref="_restoredSchema"/>, or <see langword="null"/> when this is not a restored
    /// cycle. A record re-added this cycle is looked up here so that it keeps its published ordinal.
    /// </summary>
    private ByteArrayOrdinalMap? _restoredMap;

    /// <summary>
    /// The schema the restored records were serialised under, which is the intersection of this type's
    /// schema and the published one when they differ.
    /// </summary>
    private HollowSchema? _restoredSchema;

    private HollowTypeReadState? _restoredReadState;
    private bool _wroteData;

    /// <summary>
    /// Initialises a write state for <paramref name="schema"/>.
    /// </summary>
    /// <param name="schema">The schema of the type.</param>
    /// <param name="numShards">
    /// The number of shards to split the type's records across, which must be a power of two, or -1 to
    /// derive it from the data size at write time.
    /// </param>
    protected HollowTypeWriteState(HollowSchema schema, int numShards = -1)
    {
        ArgumentNullException.ThrowIfNull(schema);

        if (numShards != -1 && (numShards <= 0 || (numShards & (numShards - 1)) != 0))
        {
            throw new ArgumentException(
                $"Number of shards must be a power of 2! Check configuration for type {schema.Name}",
                nameof(numShards));
        }

        Schema = schema;
        _numShards = numShards;
        OrdinalMap = new ByteArrayOrdinalMap();
        CurrentCyclePopulated = new ThreadSafeBitSet();
        PreviousCyclePopulated = new ThreadSafeBitSet();
    }

    /// <summary>The schema of the type this state holds.</summary>
    public HollowSchema Schema { get; }

    /// <summary>The ordinals populated by the current cycle.</summary>
    public ThreadSafeBitSet CurrentCyclePopulated { get; private set; }

    /// <summary>The ordinals populated by the previous cycle.</summary>
    public ThreadSafeBitSet PreviousCyclePopulated { get; private set; }

    /// <summary>
    /// The number of shards the type's records are split across, or -1 before
    /// <see cref="PrepareForWrite"/> has derived it.
    /// </summary>
    public int NumShards => _numShards;

    /// <summary>
    /// Whether this state was restored from a read state and has not yet been through a full cycle.
    /// </summary>
    public bool IsRestored => OrdinalMap.UnusedPreviousOrdinals is not null;

    /// <summary>The engine this type belongs to.</summary>
    public HollowWriteStateEngine? StateEngine { get; internal set; }

    /// <summary>The highest ordinal assigned, or -1 when the type is empty.</summary>
    protected int MaxOrdinal { get; private set; } = -1;

    /// <summary>The highest ordinal within each shard.</summary>
    protected int[] MaxShardOrdinal { get; private set; } = [];

    /// <summary>The map assigning ordinals to distinct serialised records.</summary>
    protected ByteArrayOrdinalMap OrdinalMap { get; }

    /// <summary>
    /// Adds a record, returning the ordinal it was assigned. Adding an equal record returns the
    /// ordinal already assigned to it.
    /// </summary>
    public int Add(IHollowWriteRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (!OrdinalMap.IsReadyForAddingObjects)
        {
            throw new InvalidOperationException(
                "The HollowWriteStateEngine is not ready to add more Objects. "
                + $"Did you remember to call {nameof(HollowWriteStateEngine.PrepareForNextCycle)}()?");
        }

        int ordinal = _restoredMap is null ? AssignOrdinal(record) : ReuseOrdinalFromRestoredState(record);

        CurrentCyclePopulated.Set(ordinal);

        return ordinal;
    }

    /// <summary>
    /// Adds a record at a caller-chosen ordinal rather than letting the ordinal map pick one.
    /// </summary>
    /// <param name="record">The record to add.</param>
    /// <param name="newOrdinal">The ordinal to place it at.</param>
    /// <param name="markPreviousCycle">Whether to mark the ordinal as populated by the previous cycle.</param>
    /// <param name="markCurrentCycle">Whether to mark the ordinal as populated by this cycle.</param>
    /// <remarks>
    /// <para>
    /// This bypasses deduplication and the free-ordinal pool, so it is only safe while populating an
    /// empty state from a known ordinal assignment. Every caller must follow the last call with
    /// <see cref="RecalculateFreeOrdinals"/>, or the pool will hand out ordinals that are already taken.
    /// </para>
    /// <para>
    /// Not thread-safe, unlike <see cref="Add"/>.
    /// </para>
    /// </remarks>
    public void MapOrdinal(IHollowWriteRecord record, int newOrdinal, bool markPreviousCycle, bool markCurrentCycle)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (!OrdinalMap.IsReadyForAddingObjects)
        {
            throw new InvalidOperationException(
                "The HollowWriteStateEngine is not ready to add more Objects. "
                + $"Did you remember to call {nameof(HollowWriteStateEngine.PrepareForNextCycle)}()?");
        }

        ByteDataArray scratch = Scratch();
        record.WriteDataTo(scratch);
        OrdinalMap.Put(scratch, newOrdinal);
        scratch.Reset();

        if (markPreviousCycle)
        {
            PreviousCyclePopulated.Set(newOrdinal);
        }

        if (markCurrentCycle)
        {
            CurrentCyclePopulated.Set(newOrdinal);
        }
    }

    /// <summary>
    /// Rebuilds the free-ordinal pool from the ordinals actually in the map, which
    /// <see cref="MapOrdinal"/> leaves stale.
    /// </summary>
    public void RecalculateFreeOrdinals() => OrdinalMap.RecalculateFreeOrdinals();

    /// <summary>
    /// Fixes the number of shards this type's records are split across.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A different shard count is already fixed for this type.
    /// </exception>
    public void SetNumShards(int numShards)
    {
        if (_numShards == -1)
        {
            _numShards = numShards;
        }
        else if (_numShards != numShards)
        {
            throw new InvalidOperationException(
                $"The number of shards for type {Schema.Name} is already fixed to {_numShards.Invariant()}. "
                + $"Cannot reset to {numShards.Invariant()}.");
        }
    }

    /// <summary>
    /// Rolls the cycle forward: the current cycle's ordinals become the previous cycle's, and records
    /// no longer referenced are compacted out.
    /// </summary>
    public virtual void PrepareForNextCycle()
    {
        Compact(CurrentCyclePopulated);

        // The restored state only governs the first cycle after a restore: from here on the ordinal map
        // holds every record itself, so lookups go through it directly.
        _restoredMap = null;
        _restoredSchema = null;
        _restoredReadState = null;

        (PreviousCyclePopulated, CurrentCyclePopulated) = (CurrentCyclePopulated, PreviousCyclePopulated);
        CurrentCyclePopulated.ClearAll();
    }

    /// <summary>
    /// Discards everything added since the last <see cref="PrepareForNextCycle"/>.
    /// </summary>
    /// <remarks>
    /// Compacting against the previous cycle's ordinals is what does the work: every record this cycle
    /// added and the previous one did not hold is dropped and its ordinal returned to the pool. A
    /// restored cycle has no previous ordinals of its own, so it is rebuilt from the read state it was
    /// restored from instead.
    /// </remarks>
    public virtual void ResetToLastPrepareForNextCycle()
    {
        CurrentCyclePopulated.ClearAll();

        if (_restoredReadState is not { } restoredReadState)
        {
            Compact(PreviousCyclePopulated);
            return;
        }

        PreviousCyclePopulated.ClearAll();
        Compact(PreviousCyclePopulated);

        RestoreFrom(restoredReadState);
        _wroteData = false;
    }

    /// <summary>
    /// Finalises the ordinal assignment and derives the shard layout, after which no more records may
    /// be added until the next cycle.
    /// </summary>
    public virtual void PrepareForWrite()
    {
        // A record that the restored state held but this cycle did not re-add still has to be present
        // in the ordinal map: the delta describes it as removed, and the matching reverse delta has to
        // be able to add it back. Copy those ghosts across without marking them populated.
        if (IsRestored && !_wroteData && _restoredReadState is not null)
        {
            HollowRecordCopier copier = HollowRecordCopier.Create(_restoredReadState, Schema);
            BitSet unusedPreviousOrdinals = OrdinalMap.UnusedPreviousOrdinals!;

            for (int ordinal = unusedPreviousOrdinals.NextSetBit(0);
                ordinal != -1;
                ordinal = unusedPreviousOrdinals.NextSetBit(ordinal + 1))
            {
                RestoreOrdinal(ordinal, copier, OrdinalMap, HashBehavior.UnmixedHashes);
            }
        }

        MaxOrdinal = OrdinalMap.PrepareForWrite();
        _wroteData = true;
    }

    /// <summary>
    /// Populates this state with the records of <paramref name="readState"/>, so that the producer can
    /// continue the delta chain that read state belongs to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every record keeps the ordinal it holds in the published state, and those ordinals are recorded
    /// as the previous cycle's. A record re-added during the restored cycle is matched against the
    /// published one and gets its ordinal back, so the delta that follows contains only genuine
    /// changes.
    /// </para>
    /// <para>
    /// Matching is done on the record's serialised form under the <em>common</em> schema — the fields
    /// this type and the published type both declare — so adding or removing a field does not make
    /// every record look new. It also ignores the hash positions inside set and map records, since
    /// those depend on bucket counts rather than on the record's identity.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">This state already holds records.</exception>
    protected internal virtual void RestoreFrom(HollowTypeReadState readState)
    {
        ArgumentNullException.ThrowIfNull(readState);

        if (PreviousCyclePopulated.Cardinality() != 0 || CurrentCyclePopulated.Cardinality() != 0)
        {
            throw new InvalidOperationException(
                $"Attempting to restore into a non-empty state (type {Schema.Name})");
        }

        BitSet populatedOrdinals = readState.PopulatedOrdinals;

        _restoredReadState = readState;
        _restoredSchema = Schema is HollowObjectSchema objectSchema
            ? objectSchema.FindCommonSchema((HollowObjectSchema)readState.Schema)
            : readState.Schema;

        HollowRecordCopier copier = HollowRecordCopier.Create(readState, _restoredSchema);

        int size = populatedOrdinals.Cardinality();
        _restoredMap = new ByteArrayOrdinalMap(size);

        for (int ordinal = populatedOrdinals.NextSetBit(0);
            ordinal != -1;
            ordinal = populatedOrdinals.NextSetBit(ordinal + 1))
        {
            PreviousCyclePopulated.Set(ordinal);
            RestoreOrdinal(ordinal, copier, _restoredMap, HashBehavior.IgnoredHashes);
        }

        OrdinalMap.Resize(size);
        OrdinalMap.ReservePreviouslyPopulatedOrdinals(populatedOrdinals);
    }

    /// <summary>
    /// Copies the record at <paramref name="ordinal"/> out of the restored read state and puts it into
    /// <paramref name="destinationMap"/> at that same ordinal.
    /// </summary>
    private void RestoreOrdinal(
        int ordinal, HollowRecordCopier copier, ByteArrayOrdinalMap destinationMap, HashBehavior hashBehavior)
    {
        IHollowWriteRecord record = copier.Copy(ordinal);

        ByteDataArray scratch = Scratch();
        WriteRecord(record, scratch, hashBehavior);
        destinationMap.Put(scratch, ordinal);
        scratch.Reset();
    }

    /// <summary>
    /// Drops every record whose ordinal is not in <paramref name="keep"/>, returning those ordinals to
    /// the free pool.
    /// </summary>
    private void Compact(ThreadSafeBitSet keep) =>
        OrdinalMap.Compact(
            keep,
            Math.Max(_numShards, 1),
            StateEngine?.FocusHoleFillInFewestShards ?? false,
            mapIndex: 0,
            mapIndexBits: 0);

    private int AssignOrdinal(IHollowWriteRecord record)
    {
        ByteDataArray scratch = Scratch();
        record.WriteDataTo(scratch);
        int ordinal = OrdinalMap.GetOrAssignOrdinal(scratch);
        scratch.Reset();

        return ordinal;
    }

    /// <summary>
    /// Assigns an ordinal during a restored cycle, preferring the one the published state gave an
    /// equal record.
    /// </summary>
    private int ReuseOrdinalFromRestoredState(IHollowWriteRecord record)
    {
        ByteDataArray scratch = Scratch();

        // Serialise the way the restored map was built so that the lookup can match, which is under the
        // common schema for an object record and without hash positions for a hashable one.
        if (_restoredSchema is HollowObjectSchema restoredObjectSchema)
        {
            ((HollowObjectWriteRecord)record).WriteDataTo(scratch, restoredObjectSchema);
        }
        else
        {
            WriteRecord(record, scratch, HashBehavior.IgnoredHashes);
        }

        int preferredOrdinal = _restoredMap!.Get(scratch);

        // That form is only for matching; what goes into the real map is the record as it stands.
        scratch.Reset();
        record.WriteDataTo(scratch);

        int ordinal = OrdinalMap.GetOrAssignOrdinal(scratch, preferredOrdinal);
        scratch.Reset();

        return ordinal;
    }

    private static void WriteRecord(IHollowWriteRecord record, ByteDataArray buffer, HashBehavior hashBehavior)
    {
        if (record is IHollowHashableWriteRecord hashable)
        {
            hashable.WriteDataTo(buffer, hashBehavior);
        }
        else
        {
            record.WriteDataTo(buffer);
        }
    }

    /// <summary>
    /// Builds the in-memory representation that <see cref="WriteSnapshot"/> serialises.
    /// </summary>
    public abstract void CalculateSnapshot();

    /// <summary>
    /// Writes this type's records in the snapshot blob format.
    /// </summary>
    public abstract void WriteSnapshot(HollowBlobOutput output);

    /// <summary>
    /// Builds the in-memory representation of the change from the previous cycle to this one.
    /// </summary>
    public void CalculateDelta() => CalculateDelta(PreviousCyclePopulated, CurrentCyclePopulated);

    /// <summary>
    /// Builds the in-memory representation of the change from this cycle back to the previous one.
    /// </summary>
    public void CalculateReverseDelta() => CalculateDelta(CurrentCyclePopulated, PreviousCyclePopulated);

    /// <summary>
    /// Builds the in-memory representation of the change between two cycles' populated ordinals.
    /// </summary>
    /// <param name="fromCyclePopulated">The ordinals populated by the cycle being moved away from.</param>
    /// <param name="toCyclePopulated">The ordinals populated by the cycle being moved to.</param>
    public abstract void CalculateDelta(ThreadSafeBitSet fromCyclePopulated, ThreadSafeBitSet toCyclePopulated);

    /// <summary>
    /// Writes the change built by <see cref="CalculateDelta()"/> in the delta blob format.
    /// </summary>
    public abstract void WriteCalculatedDelta(HollowBlobOutput output);

    /// <summary>
    /// Whether this type's populated ordinals differ from the previous cycle's.
    /// </summary>
    public bool HasChangedSinceLastCycle() => !CurrentCyclePopulated.Equals(PreviousCyclePopulated);

    /// <summary>
    /// Grows the ordinal map to hold <paramref name="size"/> records without rehashing.
    /// </summary>
    public void ResizeOrdinalMap(int size) => OrdinalMap.Resize(size);

    /// <summary>
    /// Derives the number of shards from the measured data size, if it was not fixed up front, and
    /// computes the per-shard ordinal bounds.
    /// </summary>
    protected void GatherShardingStats(int maxOrdinal)
    {
        if (_numShards == -1)
        {
            _numShards = TypeStateNumShards(maxOrdinal);
        }

        MaxShardOrdinal = CalcMaxShardOrdinal(maxOrdinal, _numShards);
    }

    /// <summary>
    /// The number of shards this type's measured data size calls for; always a power of two.
    /// </summary>
    protected abstract int TypeStateNumShards(int maxOrdinal);

    /// <summary>
    /// Distributes ordinals <c>0..maxOrdinal</c> round-robin across <paramref name="numShards"/> shards
    /// and returns the highest ordinal each shard holds.
    /// </summary>
    protected static int[] CalcMaxShardOrdinal(int maxOrdinal, int numShards)
    {
        int[] maxShardOrdinal = new int[numShards];
        int minRecordLocationsPerShard = (maxOrdinal + 1) / numShards;

        for (int i = 0; i < numShards; i++)
        {
            maxShardOrdinal[i] = i < ((maxOrdinal + 1) & (numShards - 1))
                ? minRecordLocationsPerShard
                : minRecordLocationsPerShard - 1;
        }

        return maxShardOrdinal;
    }

    /// <summary>
    /// The position of <paramref name="ordinal"/>'s serialised bytes within the ordinal map.
    /// </summary>
    protected internal long GetPointerForData(int ordinal) => OrdinalMap.GetPointerForData(ordinal);

    /// <summary>The byte storage holding every record's serialised bytes.</summary>
    protected internal SegmentedByteArray GetByteDataForOrdinal(int ordinal) =>
        OrdinalMap.ByteData.UnderlyingArray;

    /// <summary>
    /// A reusable per-thread buffer for serialising a record before it is hashed.
    /// </summary>
    protected ByteDataArray Scratch() => _serializedScratchSpace.Value!;
}
