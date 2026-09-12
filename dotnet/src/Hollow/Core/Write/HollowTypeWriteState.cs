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
using Hollow.Core.Memory;
using Hollow.Core.Memory.Encoding;
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

    private readonly bool _isNumShardsPinned;

    private int _numShards;
    private int _resetToLastNumShards;

    /// <summary>
    /// The ordinals held by the state this one was restored from, keyed by each record's serialised
    /// form under <see cref="_restoredSchema"/>, or <see langword="null"/> when this is not a restored
    /// cycle. A record re-added this cycle is looked up here so that it keeps its published ordinal.
    /// </summary>
    /// <summary>
    /// How many low bits of a global ordinal name its map when the map is partitioned, giving the four
    /// partitions Java uses. More would spread the write lock further and cost more ordinal space.
    /// </summary>
    private const int PartitionIndexBits = 2;

    private readonly ByteArrayOrdinalMap[] _ordinalMaps;
    private readonly int _ordinalMapIndexMask;

    private ByteArrayOrdinalMap[]? _restoredMaps;

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
    /// <param name="usePartitionedOrdinalMap">
    /// Whether to spread this type's records across four ordinal maps rather than one, so that adding
    /// records contends on four write locks instead of one. The ordinals a partitioned type hands out
    /// are interleaved rather than consecutive, which costs a little ordinal space and a little
    /// locality in return.
    /// </param>
    protected HollowTypeWriteState(
        HollowSchema schema, int numShards = -1, bool usePartitionedOrdinalMap = false)
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
        _resetToLastNumShards = numShards;
        _isNumShardsPinned = numShards != -1;

        OrdinalMapIndexBits = usePartitionedOrdinalMap ? PartitionIndexBits : 0;
        _ordinalMapIndexMask = (1 << OrdinalMapIndexBits) - 1;
        _ordinalMaps = [.. Enumerable.Range(0, 1 << OrdinalMapIndexBits).Select(_ => new ByteArrayOrdinalMap())];

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
    /// The number of shards the previous cycle's records are laid out in, or 0 before this type has
    /// been written at all.
    /// </summary>
    /// <remarks>
    /// A reverse delta takes a consumer back to the previous cycle, so it is written at that cycle's
    /// shard count rather than this one's. The two differ only when this cycle resharded.
    /// </remarks>
    public int RevNumShards { get; private set; }

    /// <summary>
    /// Whether the shard count was fixed by the caller rather than derived from the data size.
    /// </summary>
    /// <remarks>
    /// Java pins a count through the <c>@HollowShardLargeType</c> annotation on the data model; this
    /// port takes it as a constructor argument.
    /// </remarks>
    public bool IsNumShardsPinned => _isNumShardsPinned;

    /// <summary>
    /// Whether this state was restored from a read state and has not yet been through a full cycle.
    /// </summary>
    public bool IsRestored => Array.Exists(_ordinalMaps, map => map.UnusedPreviousOrdinals is not null);

    /// <summary>The engine this type belongs to.</summary>
    public HollowWriteStateEngine? StateEngine { get; internal set; }

    /// <summary>The highest ordinal assigned, or -1 when the type is empty.</summary>
    protected int MaxOrdinal { get; private set; } = -1;

    /// <summary>The highest ordinal within each shard.</summary>
    protected int[] MaxShardOrdinal { get; private set; } = [];

    /// <summary>
    /// The highest ordinal within each shard at <see cref="RevNumShards"/>, which is what a reverse
    /// delta is written against.
    /// </summary>
    protected int[] RevMaxShardOrdinal { get; private set; } = [];

    /// <summary>
    /// The number of low bits a global ordinal gives up to name which map assigned it, which is zero
    /// unless this type's map is partitioned.
    /// </summary>
    public int OrdinalMapIndexBits { get; }

    /// <summary>The map that assigned <paramref name="globalOrdinal"/>.</summary>
    private ByteArrayOrdinalMap MapOf(int globalOrdinal) => _ordinalMaps[globalOrdinal & _ordinalMapIndexMask];

    /// <summary>The ordinal <paramref name="globalOrdinal"/> has within the map that assigned it.</summary>
    private int LocalOrdinal(int globalOrdinal) => globalOrdinal >>> OrdinalMapIndexBits;

    /// <summary>The global ordinal for <paramref name="localOrdinal"/> in map <paramref name="mapIndex"/>.</summary>
    private int GlobalOrdinal(int localOrdinal, int mapIndex) =>
        (localOrdinal << OrdinalMapIndexBits) | mapIndex;

    /// <summary>
    /// Looks <paramref name="scratch"/> up in every map, returning its global ordinal or -1.
    /// </summary>
    /// <remarks>
    /// A record is assigned to the map its hash routes it to, so the first map tried is almost always
    /// the one holding it. The others are still searched, because a restored cycle places a record in
    /// whichever map the published state had it in rather than where its hash would put it.
    /// </remarks>
    private int SearchAllMaps(ByteArrayOrdinalMap[] maps, ByteDataArray scratch, int hash)
    {
        int start = hash & _ordinalMapIndexMask;

        for (int i = 0; i < maps.Length; i++)
        {
            int mapIndex = (start + i) % maps.Length;
            int found = maps[mapIndex].Get(scratch, hash);

            if (found != -1)
            {
                return GlobalOrdinal(found, mapIndex);
            }
        }

        return -1;
    }

    /// <summary>
    /// Adds a record, returning the ordinal it was assigned. Adding an equal record returns the
    /// ordinal already assigned to it.
    /// </summary>
    public int Add(IHollowWriteRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        ThrowIfNotReadyForAddingObjects();

        int ordinal = _restoredMaps is null ? AssignOrdinal(record) : ReuseOrdinalFromRestoredState(record);

        CurrentCyclePopulated.Set(ordinal);

        return ordinal;
    }

    /// <summary>
    /// Throws unless this state is between <c>PrepareForNextCycle</c> and <c>PrepareForWrite</c>,
    /// which is the only window in which its populated ordinals may change.
    /// </summary>
    private void ThrowIfNotReadyForAddingObjects()
    {
        if (!_ordinalMaps[0].IsReadyForAddingObjects)
        {
            throw new InvalidOperationException(
                "The HollowWriteStateEngine is not ready to add more Objects. "
                + $"Did you remember to call {nameof(HollowWriteStateEngine.PrepareForNextCycle)}()?");
        }
    }

    /// <summary>
    /// Carries every record the previous cycle held into this one, unchanged.
    /// </summary>
    /// <remarks>
    /// This is what makes an incremental cycle possible: start from the previous state and describe
    /// only the difference, rather than re-adding everything. No record is re-serialised — the ordinals
    /// are simply marked populated again.
    /// </remarks>
    public void AddAllObjectsFromPreviousCycle()
    {
        ThrowIfNotReadyForAddingObjects();

        CurrentCyclePopulated = ThreadSafeBitSet.OrAll(PreviousCyclePopulated, CurrentCyclePopulated);
    }

    /// <summary>
    /// Carries one record the previous cycle held into this one, unchanged.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The previous cycle did not hold <paramref name="ordinal"/>.
    /// </exception>
    public void AddOrdinalFromPreviousCycle(int ordinal)
    {
        ThrowIfNotReadyForAddingObjects();

        if (!PreviousCyclePopulated.Get(ordinal))
        {
            throw new ArgumentException(
                $"Ordinal {ordinal.Invariant()} was not present in the previous cycle.", nameof(ordinal));
        }

        CurrentCyclePopulated.Set(ordinal);
    }

    /// <summary>
    /// Drops a record from this cycle. Its ordinal stays assigned, so re-adding an equal record later
    /// in the chain gets the same ordinal back.
    /// </summary>
    public void RemoveOrdinalFromThisCycle(int ordinalToRemove)
    {
        ThrowIfNotReadyForAddingObjects();

        CurrentCyclePopulated.Clear(ordinalToRemove);
    }

    /// <summary>
    /// Drops every record from this cycle, leaving the type empty.
    /// </summary>
    public void RemoveAllOrdinalsFromThisCycle()
    {
        ThrowIfNotReadyForAddingObjects();

        CurrentCyclePopulated.ClearAll();
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

        ThrowIfNotReadyForAddingObjects();

        ByteDataArray scratch = Scratch();
        record.WriteDataTo(scratch);
        MapOf(newOrdinal).Put(scratch, LocalOrdinal(newOrdinal));
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
    public void RecalculateFreeOrdinals()
    {
        foreach (ByteArrayOrdinalMap map in _ordinalMaps)
        {
            map.RecalculateFreeOrdinals();
        }
    }

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
            _resetToLastNumShards = numShards;
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
        _restoredSchema = null;
        _restoredReadState = null;

        (PreviousCyclePopulated, CurrentCyclePopulated) = (CurrentCyclePopulated, PreviousCyclePopulated);
        CurrentCyclePopulated.ClearAll();

        // Abandoning the next cycle has to put the shard count back to what the cycle just closed was
        // written at, not to whatever that cycle's resharding decision made of it.
        _resetToLastNumShards = _numShards;
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
        // The abandoned cycle may have decided to reshard. That decision goes with it.
        _numShards = _resetToLastNumShards;

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
    /// <param name="canReshard">
    /// Whether this write may change the shard count, which it does only when the engine also allows
    /// it and the count was not fixed by the caller.
    /// </param>
    public virtual void PrepareForWrite(bool canReshard)
    {
        // A record that the restored state held but this cycle did not re-add still has to be present
        // in the ordinal map: the delta describes it as removed, and the matching reverse delta has to
        // be able to add it back. Copy those ghosts across without marking them populated.
        if (IsRestored && !_wroteData && _restoredReadState is not null)
        {
            HollowRecordCopier copier = HollowRecordCopier.Create(_restoredReadState, Schema);

            for (int mapIndex = 0; mapIndex < _ordinalMaps.Length; mapIndex++)
            {
                if (_ordinalMaps[mapIndex].UnusedPreviousOrdinals is not { } unusedPreviousOrdinals)
                {
                    continue;
                }

                for (int local = unusedPreviousOrdinals.NextSetBit(0);
                    local != -1;
                    local = unusedPreviousOrdinals.NextSetBit(local + 1))
                {
                    RestoreOrdinal(
                        GlobalOrdinal(local, mapIndex),
                        copier,
                        _ordinalMaps[mapIndex],
                        HashBehavior.UnmixedHashes);
                }
            }
        }

        MaxOrdinal = -1;

        for (int mapIndex = 0; mapIndex < _ordinalMaps.Length; mapIndex++)
        {
            MaxOrdinal = Math.Max(MaxOrdinal, GlobalOrdinal(_ordinalMaps[mapIndex].PrepareForWrite(), mapIndex));
        }

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
        _restoredMaps =
            [.. Enumerable.Range(0, _ordinalMaps.Length).Select(_ => new ByteArrayOrdinalMap(size))];

        for (int ordinal = populatedOrdinals.NextSetBit(0);
            ordinal != -1;
            ordinal = populatedOrdinals.NextSetBit(ordinal + 1))
        {
            PreviousCyclePopulated.Set(ordinal);

            // Into the map the published ordinal belongs to, so that re-adding the record hands back
            // that same ordinal rather than one its hash would have chosen.
            RestoreOrdinal(ordinal, copier, _restoredMaps[ordinal & _ordinalMapIndexMask], HashBehavior.IgnoredHashes);
        }

        foreach (ByteArrayOrdinalMap map in _ordinalMaps)
        {
            map.Resize(size);
        }

        ReservePreviouslyPopulatedOrdinals(populatedOrdinals);
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
        destinationMap.Put(scratch, LocalOrdinal(ordinal));
        scratch.Reset();
    }

    /// <summary>
    /// Marks every ordinal <paramref name="populatedOrdinals"/> holds as taken, in the map that owns it.
    /// </summary>
    private void ReservePreviouslyPopulatedOrdinals(BitSet populatedOrdinals)
    {
        if (_ordinalMaps.Length == 1)
        {
            _ordinalMaps[0].ReservePreviouslyPopulatedOrdinals(populatedOrdinals);
            return;
        }

        // Each map is told about its own share, in its own local numbering.
        BitSet[] perMap = [.. _ordinalMaps.Select(_ => new BitSet(populatedOrdinals.Length))];

        for (int ordinal = populatedOrdinals.NextSetBit(0);
            ordinal != -1;
            ordinal = populatedOrdinals.NextSetBit(ordinal + 1))
        {
            perMap[ordinal & _ordinalMapIndexMask].Set(LocalOrdinal(ordinal));
        }

        for (int mapIndex = 0; mapIndex < _ordinalMaps.Length; mapIndex++)
        {
            _ordinalMaps[mapIndex].ReservePreviouslyPopulatedOrdinals(perMap[mapIndex]);
        }
    }

    /// <summary>
    /// Drops every record whose ordinal is not in <paramref name="keep"/>, returning those ordinals to
    /// the free pool.
    /// </summary>
    private void Compact(ThreadSafeBitSet keep)
    {
        for (int mapIndex = 0; mapIndex < _ordinalMaps.Length; mapIndex++)
        {
            _ordinalMaps[mapIndex].Compact(
                keep,
                Math.Max(_numShards, 1),
                StateEngine?.FocusHoleFillInFewestShards ?? false,
                mapIndex,
                OrdinalMapIndexBits);
        }

        // A restored cycle's lookup maps belong to that cycle only; from here on a re-added record is
        // deduplicated against the real maps like any other.
        _restoredMaps = null;
    }

    private int AssignOrdinal(IHollowWriteRecord record)
    {
        ByteDataArray scratch = Scratch();
        record.WriteDataTo(scratch);

        int hash = HashCodes.Compute(scratch);

        // Every map, because deduplication has to be across the whole type: two equal records must
        // share an ordinal wherever they were put.
        int existing = SearchAllMaps(_ordinalMaps, scratch, hash);

        if (existing != -1)
        {
            scratch.Reset();
            return existing;
        }

        // Not there, so its hash decides which map takes it. AssignOrdinal rather than
        // GetOrAssignOrdinal: the search above already established it is new.
        int mapIndex = hash & _ordinalMapIndexMask;
        int ordinal = GlobalOrdinal(_ordinalMaps[mapIndex].AssignOrdinal(scratch, hash, -1), mapIndex);

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

        int preferredOrdinal = SearchAllMaps(_restoredMaps!, scratch, HashCodes.Compute(scratch));

        // That form is only for matching; what goes into the real map is the record as it stands.
        scratch.Reset();
        record.WriteDataTo(scratch);

        // The published ordinal decides which map takes the record, so that reusing it is possible at
        // all; only a record the published state did not hold falls back to its hash.
        int hash = HashCodes.Compute(scratch);
        int mapIndex = preferredOrdinal != -1 ? preferredOrdinal & _ordinalMapIndexMask : hash & _ordinalMapIndexMask;
        int preferredLocal = preferredOrdinal != -1 ? LocalOrdinal(preferredOrdinal) : -1;

        int ordinal = GlobalOrdinal(
            _ordinalMaps[mapIndex].GetOrAssignOrdinal(scratch, hash, preferredLocal), mapIndex);

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
    public void CalculateDelta() =>
        CalculateDelta(PreviousCyclePopulated, CurrentCyclePopulated, isReverse: false);

    /// <summary>
    /// Builds the in-memory representation of the change from this cycle back to the previous one.
    /// </summary>
    public void CalculateReverseDelta() =>
        CalculateDelta(CurrentCyclePopulated, PreviousCyclePopulated, isReverse: true);

    /// <summary>
    /// Builds the in-memory representation of the change between two cycles' populated ordinals.
    /// </summary>
    /// <param name="fromCyclePopulated">The ordinals populated by the cycle being moved away from.</param>
    /// <param name="toCyclePopulated">The ordinals populated by the cycle being moved to.</param>
    /// <param name="isReverse">
    /// Whether this is a reverse delta, which is laid out at <see cref="RevNumShards"/> because that is
    /// the arrangement the consumer it takes back will be holding.
    /// </param>
    public abstract void CalculateDelta(
        ThreadSafeBitSet fromCyclePopulated, ThreadSafeBitSet toCyclePopulated, bool isReverse);

    /// <summary>
    /// Writes the change built by <see cref="CalculateDelta()"/> in the delta blob format.
    /// </summary>
    public void WriteDelta(HollowBlobOutput output) =>
        WriteCalculatedDelta(output, isReverse: false, MaxShardOrdinal);

    /// <summary>
    /// Writes the change built by <see cref="CalculateReverseDelta"/> in the delta blob format.
    /// </summary>
    public void WriteReverseDelta(HollowBlobOutput output) =>
        WriteCalculatedDelta(output, isReverse: true, RevMaxShardOrdinal);

    /// <summary>
    /// Writes a calculated change in the delta blob format.
    /// </summary>
    /// <param name="output">The blob to write to.</param>
    /// <param name="isReverse">Whether this is a reverse delta.</param>
    /// <param name="maxShardOrdinal">The highest ordinal in each shard at that direction's count.</param>
    public abstract void WriteCalculatedDelta(
        HollowBlobOutput output, bool isReverse, int[] maxShardOrdinal);

    /// <summary>
    /// The number of shards a delta in the given direction is laid out at.
    /// </summary>
    protected int NumShardsForDelta(bool isReverse) =>
        isReverse && _numShards != RevNumShards ? RevNumShards : _numShards;

    /// <summary>
    /// Whether this type has anything for a delta to say.
    /// </summary>
    /// <remarks>
    /// A change of shard count counts even when the records did not change, because the arrangement
    /// itself is what the delta is carrying — a consumer that skipped it would be left at the old count
    /// and would misread every ordinal in the delta after it.
    /// </remarks>
    public bool HasChangedSinceLastCycle() =>
        !CurrentCyclePopulated.Equals(PreviousCyclePopulated)
        || (_numShards != RevNumShards && RevNumShards != 0);

    /// <summary>
    /// Grows the ordinal map to hold <paramref name="size"/> records without rehashing.
    /// </summary>
    public void ResizeOrdinalMap(int size)
    {
        foreach (ByteArrayOrdinalMap map in _ordinalMaps)
        {
            map.Resize(size);
        }
    }

    /// <summary>
    /// Settles how many shards this cycle's records are laid out in and computes the per-shard ordinal
    /// bounds, for this cycle and for the previous one.
    /// </summary>
    /// <remarks>
    /// On the first write the count is simply derived from the data size. After that it stays where it
    /// is unless resharding is allowed, in which case the data size is measured again and the count
    /// moves one factor of two towards what that measurement calls for. Moving in single steps keeps
    /// each consumer's rearrangement to one split or one join per cycle.
    /// </remarks>
    /// <param name="maxOrdinal">The highest ordinal this cycle assigned.</param>
    /// <param name="canReshard">Whether this write may change the count.</param>
    protected void GatherShardingStats(int maxOrdinal, bool canReshard)
    {
        if (_numShards == -1)
        {
            _numShards = TypeStateNumShards(maxOrdinal);
            RevNumShards = _numShards;
        }
        else
        {
            RevNumShards = _numShards;

            if (canReshard && AllowTypeResharding())
            {
                int targetNumShards = TypeStateNumShards(maxOrdinal);

                if (targetNumShards != RevNumShards)
                {
                    _numShards = targetNumShards > RevNumShards ? RevNumShards * 2 : RevNumShards / 2;

                    AddReshardingHeader(RevNumShards, _numShards);
                }
            }
        }

        MaxShardOrdinal = CalcMaxShardOrdinal(maxOrdinal, _numShards);

        if (RevNumShards > 0)
        {
            RevMaxShardOrdinal = CalcMaxShardOrdinal(maxOrdinal, RevNumShards);
        }
    }

    /// <summary>
    /// Whether this type's shard count may change from one cycle to the next.
    /// </summary>
    /// <remarks>
    /// A pinned count loses to the engine-wide setting rather than overriding it, matching Java. The
    /// pin is then pointless and can be dropped from the data model.
    /// </remarks>
    protected bool AllowTypeResharding() => StateEngine?.AllowTypeResharding ?? false;

    /// <summary>
    /// Records a shard-count change in the header tags, so that a consumer — or someone reading the
    /// blob later — can see which types moved and by how much.
    /// </summary>
    /// <remarks>
    /// The tag names every type resharded this cycle, in the forward direction, as
    /// <c>Movie:(2,4) Actor:(8,4)</c>.
    /// </remarks>
    protected void AddReshardingHeader(int prevNumShards, int newNumShards)
    {
        if (StateEngine is not { } stateEngine)
        {
            return;
        }

        string existing = stateEngine.HeaderTags.TryGetValue(HollowHeaderTags.TypeReshardingInvoked, out string? tag)
            ? tag + " "
            : string.Empty;

        stateEngine.AddHeaderTag(
            HollowHeaderTags.TypeReshardingInvoked,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{existing}{Schema.Name}:({prevNumShards},{newNumShards})"));
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
    protected internal long GetPointerForData(int ordinal) =>
        MapOf(ordinal).GetPointerForData(LocalOrdinal(ordinal));

    /// <summary>The byte storage holding <paramref name="ordinal"/>'s serialised bytes.</summary>
    /// <remarks>
    /// Each map owns its own buffer, so which one holds a record follows from its ordinal — which is
    /// why this takes the ordinal rather than being a property.
    /// </remarks>
    protected internal SegmentedByteArray GetByteDataForOrdinal(int ordinal) =>
        MapOf(ordinal).ByteData.UnderlyingArray;

    /// <summary>
    /// A reusable per-thread buffer for serialising a record before it is hashed.
    /// </summary>
    protected ByteDataArray Scratch() => _serializedScratchSpace.Value!;
}
