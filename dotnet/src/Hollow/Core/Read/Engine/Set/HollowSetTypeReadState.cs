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

namespace Hollow.Core.Read.Engine.Set;

/// <summary>
/// The in-memory record storage of one shard of a set type: each record's open-addressed hash table,
/// concatenated into one bit-packed array.
/// </summary>
public sealed partial class HollowSetTypeDataElements
{
    private readonly IArraySegmentRecycler _memoryRecycler;

    /// <summary>
    /// Initialises empty storage.
    /// </summary>
    public HollowSetTypeDataElements(IArraySegmentRecycler memoryRecycler)
    {
        ArgumentNullException.ThrowIfNull(memoryRecycler);
        _memoryRecycler = memoryRecycler;
    }

    /// <summary>The highest ordinal this shard holds.</summary>
    public int MaxOrdinal { get; internal set; }

    /// <summary>Each record's end-of-table pointer and element count, packed together.</summary>
    public IFixedLengthData? SetPointerAndSizeData { get; internal set; }

    /// <summary>Every record's hash table buckets, back to back.</summary>
    public IFixedLengthData? ElementData { get; internal set; }

    /// <summary>The width of a pointer into <see cref="ElementData"/>, in bits.</summary>
    public int BitsPerSetPointer { get; internal set; }

    /// <summary>The width of a set's element count, in bits.</summary>
    public int BitsPerSetSizeValue { get; internal set; }

    /// <summary>The combined width of the pointer and size stored per record.</summary>
    public int BitsPerFixedLengthSetPortion { get; internal set; }

    /// <summary>The width of an element ordinal, in bits.</summary>
    public int BitsPerElement { get; internal set; }

    /// <summary>The all-ones element value that marks a bucket as empty.</summary>
    public int EmptyBucketValue { get; internal set; }

    /// <summary>The total number of buckets across every set in this shard.</summary>
    public long TotalNumberOfBuckets { get; internal set; }

    /// <summary>
    /// Reads one shard's records from <paramref name="input"/>.
    /// </summary>
    public void ReadSnapshot(HollowBlobInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        MaxOrdinal = VarInt.ReadVInt(input);
        BitsPerSetPointer = VarInt.ReadVInt(input);
        BitsPerSetSizeValue = VarInt.ReadVInt(input);
        BitsPerElement = VarInt.ReadVInt(input);
        BitsPerFixedLengthSetPortion = BitsPerSetPointer + BitsPerSetSizeValue;
        EmptyBucketValue = (1 << BitsPerElement) - 1;
        TotalNumberOfBuckets = VarInt.ReadVLong(input);

        SetPointerAndSizeData = FixedLengthElementArray.NewFrom(input, _memoryRecycler);
        ElementData = FixedLengthElementArray.NewFrom(input, _memoryRecycler);
    }

    /// <summary>The number of elements in <paramref name="ordinal"/>'s set.</summary>
    public int GetSize(int ordinal) =>
        (int)SetPointerAndSizeData!.GetLargeElementValue(
            ((long)ordinal * BitsPerFixedLengthSetPortion) + BitsPerSetPointer, BitsPerSetSizeValue);

    /// <summary>The index of the first bucket of <paramref name="ordinal"/>'s hash table.</summary>
    public long GetStartBucket(int ordinal) =>
        ordinal == 0
            ? 0
            : SetPointerAndSizeData!.GetLargeElementValue(
                (long)(ordinal - 1) * BitsPerFixedLengthSetPortion, BitsPerSetPointer);

    /// <summary>The index one past the last bucket of <paramref name="ordinal"/>'s hash table.</summary>
    public long GetEndBucket(int ordinal) =>
        SetPointerAndSizeData!.GetLargeElementValue(
            (long)ordinal * BitsPerFixedLengthSetPortion, BitsPerSetPointer);

    /// <summary>The element ordinal stored in an absolute bucket index.</summary>
    public int GetBucketValue(long absoluteBucketIndex) =>
        (int)ElementData!.GetLargeElementValue(absoluteBucketIndex * BitsPerElement, BitsPerElement);

    /// <summary>Returns every segment of this shard's storage to the recycler.</summary>
    public void Destroy()
    {
        (SetPointerAndSizeData as FixedLengthElementArray)?.Destroy(_memoryRecycler);
        (ElementData as FixedLengthElementArray)?.Destroy(_memoryRecycler);
        SetPointerAndSizeData = null;
        ElementData = null;
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

            VarInt.ReadVInt(input); // bitsPerSetPointer
            VarInt.ReadVInt(input); // bitsPerSetSizeValue
            VarInt.ReadVInt(input); // bitsPerElement
            VarInt.ReadVLong(input); // totalNumberOfBuckets

            IFixedLengthData.DiscardFrom(input);
            IFixedLengthData.DiscardFrom(input);
        }
    }
}

/// <summary>
/// Holds the records of a set type.
/// </summary>
public sealed partial class HollowSetTypeReadState : HollowTypeReadState, IHollowSetTypeDataAccess
{
    private Shard[] _shards = [];
    private int _shardNumberMask;
    private int _maxOrdinal = -1;

    /// <summary>
    /// Initialises a read state for <paramref name="schema"/>.
    /// </summary>
    public HollowSetTypeReadState(
        HollowReadStateEngine stateEngine, MemoryMode memoryMode, HollowSetSchema schema)
        : base(stateEngine, memoryMode, schema)
    {
    }

    /// <summary>The schema of this type.</summary>
    public new HollowSetSchema Schema => (HollowSetSchema)base.Schema;

    /// <inheritdoc />
    HollowCollectionSchema IHollowCollectionTypeDataAccess.Schema => Schema;

    /// <inheritdoc />
    public override int MaxOrdinal => _maxOrdinal;

    /// <inheritdoc />
    public override int NumShards => _shards.Length;

    /// <inheritdoc />
    public override long ApproxHeapFootprintInBytes
    {
        get
        {
            long bits = 0;
            foreach (Shard shard in _shards)
            {
                bits += ((long)shard.DataElements.MaxOrdinal + 1) * shard.DataElements.BitsPerFixedLengthSetPortion;
                bits += shard.DataElements.TotalNumberOfBuckets * shard.DataElements.BitsPerElement;
            }

            return bits / 8;
        }
    }

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
            HollowSetTypeDataElements dataElements = new(memoryRecycler);
            dataElements.ReadSnapshot(input);
            shards[i] = new Shard(dataElements, shardOrdinalShift);
        }

        _shards = shards;
        _shardNumberMask = numShards - 1;

        if (numShards == 1)
        {
            _maxOrdinal = shards[0].DataElements.MaxOrdinal;
        }

        SnapshotPopulatedOrdinalsReader.ReadOrdinals(input, Listeners);
    }

    /// <inheritdoc />
    public override void Destroy(IArraySegmentRecycler memoryRecycler)
    {
        foreach (Shard shard in _shards)
        {
            shard.DataElements.Destroy();
        }

        _shards = [];
    }

    /// <inheritdoc />
    public int Size(int ordinal)
    {
        Shard shard = _shards[ordinal & _shardNumberMask];
        return shard.DataElements.GetSize(ordinal >> shard.ShardOrdinalShift);
    }

    /// <inheritdoc />
    public bool Contains(int ordinal, int value) => Contains(ordinal, value, value);

    /// <inheritdoc />
    public bool Contains(int ordinal, int value, int hashCode)
    {
        Shard shard = _shards[ordinal & _shardNumberMask];
        int shardOrdinal = ordinal >> shard.ShardOrdinalShift;

        long startBucket = shard.DataElements.GetStartBucket(shardOrdinal);
        long endBucket = shard.DataElements.GetEndBucket(shardOrdinal);

        if (startBucket == endBucket)
        {
            return false;
        }

        long bucket = startBucket + (HashCodes.HashInt(hashCode) & (endBucket - startBucket - 1));

        int bucketOrdinal = shard.DataElements.GetBucketValue(bucket);
        while (bucketOrdinal != shard.DataElements.EmptyBucketValue)
        {
            if (bucketOrdinal == value)
            {
                return true;
            }

            bucket++;
            if (bucket == endBucket)
            {
                bucket = startBucket;
            }

            bucketOrdinal = shard.DataElements.GetBucketValue(bucket);
        }

        return false;
    }

    /// <inheritdoc />
    public int RelativeBucketValue(int ordinal, int bucketIndex)
    {
        Shard shard = _shards[ordinal & _shardNumberMask];
        int shardOrdinal = ordinal >> shard.ShardOrdinalShift;

        long startBucket = shard.DataElements.GetStartBucket(shardOrdinal);
        int value = shard.DataElements.GetBucketValue(startBucket + bucketIndex);

        return value == shard.DataElements.EmptyBucketValue ? HollowConstants.OrdinalNone : value;
    }

    /// <inheritdoc />
    public IHollowOrdinalIterator OrdinalIterator(int ordinal) => new HollowSetOrdinalIterator(ordinal, this);

    /// <inheritdoc />
    public IHollowOrdinalIterator PotentialMatchOrdinalIterator(int ordinal, int hashCode) =>
        new PotentialMatchHollowSetOrdinalIterator(ordinal, this, hashCode);

    private sealed class Shard(HollowSetTypeDataElements dataElements, int shardOrdinalShift)
    {
        internal HollowSetTypeDataElements DataElements { get; } = dataElements;

        internal int ShardOrdinalShift { get; } = shardOrdinalShift;
    }
}
