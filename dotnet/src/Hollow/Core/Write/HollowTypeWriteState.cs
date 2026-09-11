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
using Hollow.Core.Schema;

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
/// <strong>Port note.</strong> The Java class also supports partitioned ordinal maps, restoring from a
/// read state, delta and reverse-delta calculation, and dynamic resharding across cycles. This port is
/// snapshot-only and uses a single ordinal map — see <c>PORTING.md</c>.
/// </para>
/// </remarks>
public abstract class HollowTypeWriteState
{
    private readonly ThreadLocal<ByteDataArray> _serializedScratchSpace =
        new(() => new ByteDataArray(WastefulRecycler.DefaultInstance));

    private int _numShards;

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

        ByteDataArray scratch = Scratch();
        record.WriteDataTo(scratch);
        int ordinal = OrdinalMap.GetOrAssignOrdinal(scratch);
        scratch.Reset();

        CurrentCyclePopulated.Set(ordinal);

        return ordinal;
    }

    /// <summary>
    /// Rolls the cycle forward: the current cycle's ordinals become the previous cycle's, and records
    /// no longer referenced are compacted out.
    /// </summary>
    public virtual void PrepareForNextCycle()
    {
        OrdinalMap.Compact(
            CurrentCyclePopulated,
            Math.Max(_numShards, 1),
            StateEngine?.FocusHoleFillInFewestShards ?? false,
            mapIndex: 0,
            mapIndexBits: 0);

        (PreviousCyclePopulated, CurrentCyclePopulated) = (CurrentCyclePopulated, PreviousCyclePopulated);
        CurrentCyclePopulated.ClearAll();
    }

    /// <summary>
    /// Finalises the ordinal assignment and derives the shard layout, after which no more records may
    /// be added until the next cycle.
    /// </summary>
    public virtual void PrepareForWrite()
    {
        MaxOrdinal = OrdinalMap.PrepareForWrite();
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
