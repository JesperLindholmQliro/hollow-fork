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

using System.Numerics;
using Hollow.Core.Memory;
using Hollow.Core.Memory.Encoding;
using Hollow.Core.Memory.Pool;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Iterator;
using Hollow.Core.Schema;

namespace Hollow.Core.Read.Engine.Map;

/// <summary>
/// The in-memory record storage of one shard of a map type: each record's open-addressed hash table,
/// with the key and value ordinals packed side by side in every bucket.
/// </summary>
public sealed partial class HollowMapTypeDataElements : HollowTypeDataElements
{

    /// <summary>
    /// Initialises empty storage.
    /// </summary>
    public HollowMapTypeDataElements(IArraySegmentRecycler memoryRecycler)
        : base(memoryRecycler)
    {
        ArgumentNullException.ThrowIfNull(memoryRecycler);
    }


    /// <summary>Each record's end-of-table pointer and entry count, packed together.</summary>
    public IFixedLengthData? MapPointerAndSizeData { get; internal set; }

    /// <summary>Every record's hash table buckets, back to back.</summary>
    public IFixedLengthData? EntryData { get; internal set; }

    /// <summary>The width of a pointer into <see cref="EntryData"/>, in bits.</summary>
    public int BitsPerMapPointer { get; internal set; }

    /// <summary>The width of a map's entry count, in bits.</summary>
    public int BitsPerMapSizeValue { get; internal set; }

    /// <summary>The combined width of the pointer and size stored per record.</summary>
    public int BitsPerFixedLengthMapPortion { get; internal set; }

    /// <summary>The width of a key ordinal, in bits.</summary>
    public int BitsPerKeyElement { get; internal set; }

    /// <summary>The width of a value ordinal, in bits.</summary>
    public int BitsPerValueElement { get; internal set; }

    /// <summary>The combined width of one bucket.</summary>
    public int BitsPerMapEntry { get; internal set; }

    /// <summary>The all-ones key value that marks a bucket as empty.</summary>
    public int EmptyBucketKeyValue { get; internal set; }

    /// <summary>The total number of buckets across every map in this shard.</summary>
    public long TotalNumberOfBuckets { get; internal set; }

    /// <summary>
    /// Reads one shard's records from <paramref name="input"/>.
    /// </summary>
    public void ReadSnapshot(HollowBlobInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        MaxOrdinal = VarInt.ReadVInt(input);
        BitsPerMapPointer = VarInt.ReadVInt(input);
        BitsPerMapSizeValue = VarInt.ReadVInt(input);
        BitsPerKeyElement = VarInt.ReadVInt(input);
        BitsPerValueElement = VarInt.ReadVInt(input);
        BitsPerFixedLengthMapPortion = BitsPerMapPointer + BitsPerMapSizeValue;
        BitsPerMapEntry = BitsPerKeyElement + BitsPerValueElement;
        EmptyBucketKeyValue = (1 << BitsPerKeyElement) - 1;
        TotalNumberOfBuckets = VarInt.ReadVLong(input);

        MapPointerAndSizeData = FixedLengthElementArray.NewFrom(input, MemoryRecycler);
        EntryData = FixedLengthElementArray.NewFrom(input, MemoryRecycler);
    }

    /// <summary>The number of entries in <paramref name="ordinal"/>'s map.</summary>
    public int GetSize(int ordinal) =>
        (int)MapPointerAndSizeData!.GetLargeElementValue(
            ((long)ordinal * BitsPerFixedLengthMapPortion) + BitsPerMapPointer, BitsPerMapSizeValue);

    /// <summary>The index of the first bucket of <paramref name="ordinal"/>'s hash table.</summary>
    public long GetStartBucket(int ordinal) =>
        ordinal == 0
            ? 0
            : MapPointerAndSizeData!.GetLargeElementValue(
                (long)(ordinal - 1) * BitsPerFixedLengthMapPortion, BitsPerMapPointer);

    /// <summary>The index one past the last bucket of <paramref name="ordinal"/>'s hash table.</summary>
    public long GetEndBucket(int ordinal) =>
        MapPointerAndSizeData!.GetLargeElementValue(
            (long)ordinal * BitsPerFixedLengthMapPortion, BitsPerMapPointer);

    /// <summary>The key ordinal stored in an absolute bucket index.</summary>
    public int GetBucketKeyValue(long absoluteBucketIndex) =>
        (int)EntryData!.GetLargeElementValue(absoluteBucketIndex * BitsPerMapEntry, BitsPerKeyElement);

    /// <summary>The value ordinal stored in an absolute bucket index.</summary>
    public int GetBucketValueValue(long absoluteBucketIndex) =>
        (int)EntryData!.GetLargeElementValue(
            (absoluteBucketIndex * BitsPerMapEntry) + BitsPerKeyElement, BitsPerValueElement);

    /// <summary>
    /// Records where <paramref name="ordinal"/>'s hash table ends and how many entries it holds.
    /// </summary>
    /// <remarks>
    /// A record's table starts where the previous one ended, so writing the end pointer for every
    /// ordinal in turn — including the ones holding nothing — is what delimits them all.
    /// </remarks>
    internal void WritePointerAndSize(int ordinal, long endBucket, int size)
    {
        long fixedLengthOffset = (long)ordinal * BitsPerFixedLengthMapPortion;

        MapPointerAndSizeData!.SetElementValue(fixedLengthOffset, BitsPerMapPointer, endBucket);
        MapPointerAndSizeData.SetElementValue(
            fixedLengthOffset + BitsPerMapPointer, BitsPerMapSizeValue, size);
    }

    /// <summary>Returns every segment of this shard's storage to the recycler.</summary>
    public override void Destroy()
    {
        (MapPointerAndSizeData as FixedLengthElementArray)?.Destroy(MemoryRecycler);
        (EntryData as FixedLengthElementArray)?.Destroy(MemoryRecycler);
        MapPointerAndSizeData = null;
        EntryData = null;
    }

    /// <summary>
    /// Skips a type's records without materialising them.
    /// </summary>
    public static void DiscardFromInput(HollowBlobInput input, int numShards, bool isDelta = false)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (numShards > 1)
        {
            VarInt.ReadVInt(input);
        }

        for (int i = 0; i < numShards; i++)
        {
            VarInt.ReadVInt(input); // The shard's max ordinal.

            if (isDelta)
            {
                // A delta shard begins with the ordinals it adds and removes.
                GapEncodedVariableLengthIntegerReader.DiscardEncodedDeltaOrdinals(input);
                GapEncodedVariableLengthIntegerReader.DiscardEncodedDeltaOrdinals(input);
            }

            VarInt.ReadVInt(input); // bitsPerMapPointer
            VarInt.ReadVInt(input); // bitsPerMapSizeValue
            VarInt.ReadVInt(input); // bitsPerKeyElement
            VarInt.ReadVInt(input); // bitsPerValueElement
            VarInt.ReadVLong(input); // totalNumberOfBuckets

            IFixedLengthData.DiscardFrom(input);
            IFixedLengthData.DiscardFrom(input);
        }
    }
}

/// <summary>
/// Holds the records of a map type.
/// </summary>
public sealed partial class HollowMapTypeReadState : HollowTypeReadState, IHollowMapTypeDataAccess
{
    private volatile ShardsHolder<Shard> _shardsVolatile = ShardsHolder<Shard>.Empty;
    private int _maxOrdinal = -1;

    /// <summary>
    /// Initialises a read state for <paramref name="schema"/>.
    /// </summary>
    public HollowMapTypeReadState(
        HollowReadStateEngine stateEngine, MemoryMode memoryMode, HollowMapSchema schema)
        : base(stateEngine, memoryMode, schema)
    {
    }

    /// <summary>
    /// Initialises a read state over records that are already in memory, held as a single shard.
    /// </summary>
    /// <remarks>
    /// This is how a historical state is built: its records were copied out of a live state rather
    /// than read from a blob, so there is nothing to read and no reason to shard them.
    /// </remarks>
    internal HollowMapTypeReadState(
        HollowReadStateEngine stateEngine, HollowMapSchema schema, HollowMapTypeDataElements dataElements)
        : base(stateEngine, MemoryMode.OnHeap, schema)
    {
        ArgumentNullException.ThrowIfNull(dataElements);

        _shardsVolatile = new ShardsHolder<Shard>([new Shard(dataElements, 0)]);
        _maxOrdinal = dataElements.MaxOrdinal;
    }

    /// <summary>The schema of this type.</summary>
    public new HollowMapSchema Schema => (HollowMapSchema)base.Schema;

    /// <inheritdoc />
    public override int MaxOrdinal => _maxOrdinal;

    /// <inheritdoc />
    public override ShardsHolder ShardsVolatile => _shardsVolatile;

    /// <inheritdoc />
    internal override void UpdateShards(HollowTypeReadStateShard[] shards) =>
        _shardsVolatile = new ShardsHolder<Shard>([.. shards.Cast<Shard>()]);

    /// <inheritdoc />
    internal override HollowTypeDataElements[] CreateTypeDataElements(int length) =>
        new HollowMapTypeDataElements[length];

    /// <inheritdoc />
    internal override HollowTypeReadStateShard CreateTypeReadStateShard(
        HollowTypeDataElements elements, int shardOrdinalShift) =>
        new Shard((HollowMapTypeDataElements)elements, shardOrdinalShift);

    /// <inheritdoc />
    public override long ApproxHeapFootprintInBytes
    {
        get
        {
            long bits = 0;
            foreach (Shard shard in _shardsVolatile.TypedShards)
            {
                bits += ((long)shard.DataElements.MaxOrdinal + 1) * shard.DataElements.BitsPerFixedLengthMapPortion;
                bits += shard.DataElements.TotalNumberOfBuckets * shard.DataElements.BitsPerMapEntry;
            }

            return bits / 8;
        }
    }

    /// <inheritdoc />
    private protected override int BitsPerRecord(HollowTypeReadStateShard shard) =>
        ((Shard)shard).DataElements.BitsPerFixedLengthMapPortion;

    /// <inheritdoc />
    public override void ReadSnapshot(HollowBlobInput input, IArraySegmentRecycler memoryRecycler, int numShards)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(memoryRecycler);

        if (numShards > 1)
        {
            _maxOrdinal = VarInt.ReadVInt(input);
        }

        int shardOrdinalShift = BitOperations.TrailingZeroCount((uint)numShards);

        Shard[] shards = new Shard[numShards];
        for (int i = 0; i < numShards; i++)
        {
            HollowMapTypeDataElements dataElements = new(memoryRecycler);
            dataElements.ReadSnapshot(input);
            shards[i] = new Shard(dataElements, shardOrdinalShift);
        }

        _shardsVolatile = new ShardsHolder<Shard>(shards);

        if (numShards == 1)
        {
            _maxOrdinal = shards[0].DataElements.MaxOrdinal;
        }

        SnapshotPopulatedOrdinalsReader.ReadOrdinals(input, Listeners);
    }

    /// <inheritdoc />
    public override void Destroy(IArraySegmentRecycler memoryRecycler)
    {
        foreach (Shard shard in _shardsVolatile.TypedShards)
        {
            shard.DataElements.Destroy();
        }

        _shardsVolatile = ShardsHolder<Shard>.Empty;
    }

    /// <inheritdoc />
    public int Size(int ordinal)
    {
        Shard shard = ShardFor(ordinal);
        return shard.DataElements.GetSize(ordinal >> shard.ShardOrdinalShift);
    }

    /// <inheritdoc />
    public int Get(int ordinal, int keyOrdinal) => Get(ordinal, keyOrdinal, keyOrdinal);

    /// <inheritdoc />
    public int Get(int ordinal, int keyOrdinal, int hashCode)
    {
        Shard shard = ShardFor(ordinal);
        int shardOrdinal = ordinal >> shard.ShardOrdinalShift;

        long startBucket = shard.DataElements.GetStartBucket(shardOrdinal);
        long endBucket = shard.DataElements.GetEndBucket(shardOrdinal);

        if (startBucket == endBucket)
        {
            return HollowConstants.OrdinalNone;
        }

        long bucket = startBucket + (HashCodes.HashInt(hashCode) & (endBucket - startBucket - 1));

        int bucketKey = shard.DataElements.GetBucketKeyValue(bucket);
        while (bucketKey != shard.DataElements.EmptyBucketKeyValue)
        {
            if (bucketKey == keyOrdinal)
            {
                return shard.DataElements.GetBucketValueValue(bucket);
            }

            bucket++;
            if (bucket == endBucket)
            {
                bucket = startBucket;
            }

            bucketKey = shard.DataElements.GetBucketKeyValue(bucket);
        }

        return HollowConstants.OrdinalNone;
    }

    /// <inheritdoc />
    public long RelativeBucket(int ordinal, int bucketIndex)
    {
        Shard shard = ShardFor(ordinal);
        int shardOrdinal = ordinal >> shard.ShardOrdinalShift;

        long absoluteBucketIndex = shard.DataElements.GetStartBucket(shardOrdinal) + bucketIndex;
        int key = shard.DataElements.GetBucketKeyValue(absoluteBucketIndex);

        if (key == shard.DataElements.EmptyBucketKeyValue)
        {
            return -1L;
        }

        return ((long)key << 32) | (uint)shard.DataElements.GetBucketValueValue(absoluteBucketIndex);
    }

    /// <inheritdoc />
    public IHollowMapEntryOrdinalIterator OrdinalIterator(int ordinal) =>
        new HollowMapEntryOrdinalIteratorImpl(ordinal, this);

    /// <inheritdoc />
    public IHollowMapEntryOrdinalIterator PotentialMatchOrdinalIterator(int ordinal, int hashCode) =>
        new PotentialMatchHollowMapEntryOrdinalIteratorImpl(ordinal, this, hashCode);

    /// <summary>
    /// The shard holding <paramref name="ordinal"/>, read through one load of the shards holder so
    /// that a concurrent reshard cannot change the mask between selecting a shard and using it.
    /// </summary>
    private Shard ShardFor(int ordinal)
    {
        ShardsHolder<Shard> shards = _shardsVolatile;

        return shards.TypedShards[ordinal & shards.ShardNumberMask];
    }

    internal sealed class Shard(HollowMapTypeDataElements dataElements, int shardOrdinalShift)
        : HollowTypeReadStateShard(shardOrdinalShift)
    {
        internal HollowMapTypeDataElements DataElements { get; } = dataElements;

        /// <inheritdoc />
        public override HollowTypeDataElements Elements => DataElements;
    }
}
