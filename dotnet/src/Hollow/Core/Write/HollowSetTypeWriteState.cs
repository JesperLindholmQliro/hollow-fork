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
using Hollow.Core.Schema;

namespace Hollow.Core.Write;

/// <summary>
/// The write state of a set type: each record's elements are laid out in an open-addressed hash table
/// of its own, and every record's table is concatenated into one bit-packed array.
/// </summary>
/// <remarks>
/// When the schema declares a hash key, elements are placed by that key's hash rather than by their
/// ordinal's, so a consumer can look one up by key. Ordinal-based lookup then no longer works — see
/// <c>PORTING.md</c>.
/// </remarks>
public sealed partial class HollowSetTypeWriteState : HollowTypeWriteState
{
    private int _bitsPerElement;
    private int _bitsPerSetSizeValue;
    private int _bitsPerSetPointer;
    private long[] _totalOfSetBuckets = [];

    private FixedLengthElementArray[]? _setPointersAndSizesArray;
    private FixedLengthElementArray[]? _elementArray;

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
    /// records contends on four write locks instead of one.
    /// </param>
    public HollowSetTypeWriteState(
        HollowSetSchema schema, int numShards = -1, bool usePartitionedOrdinalMap = false)
        : base(schema, numShards, usePartitionedOrdinalMap)
    {
    }

    /// <summary>The schema of the type this state holds.</summary>
    public new HollowSetSchema Schema => (HollowSetSchema)base.Schema;

    /// <inheritdoc />
    public override void PrepareForWrite(bool canReshard)
    {
        base.PrepareForWrite(canReshard);

        GatherShardingStats(MaxOrdinal, canReshard);
        GatherStatistics();
    }

    /// <inheritdoc />
    public override void CalculateSnapshot()
    {
        int numShards = NumShards;
        int bitsPerSetFixedLengthPortion = _bitsPerSetSizeValue + _bitsPerSetPointer;
        long emptyBucketValue = (1L << _bitsPerElement) - 1;

        _setPointersAndSizesArray = new FixedLengthElementArray[numShards];
        _elementArray = new FixedLengthElementArray[numShards];

        for (int i = 0; i < numShards; i++)
        {
            _setPointersAndSizesArray[i] = new FixedLengthElementArray(
                WastefulRecycler.DefaultInstance, (long)bitsPerSetFixedLengthPortion * (MaxShardOrdinal[i] + 1));
            _elementArray[i] = new FixedLengthElementArray(
                WastefulRecycler.DefaultInstance, (long)_bitsPerElement * _totalOfSetBuckets[i]);
        }

        int[] bucketCounter = new int[numShards];
        int shardMask = numShards - 1;

        // A declared hash key means the producer must place each element in the bucket a consumer
        // probing by that key will look in, rather than in its ordinal's bucket.
        HollowWriteStateEnginePrimaryKeyHasher? keyHasher =
            HollowWriteStateEnginePrimaryKeyHasher.TryCreate(Schema.HashKey, StateEngine);

        for (int ordinal = 0; ordinal <= MaxOrdinal; ordinal++)
        {
            int shardNumber = ordinal & shardMask;
            int shardOrdinal = ordinal / numShards;

            if (CurrentCyclePopulated.Get(ordinal))
            {
                long readPointer = GetPointerForData(ordinal);
                IByteData data = GetByteDataForOrdinal(ordinal);

                int size = VarInt.ReadVInt(data, readPointer);
                readPointer += VarInt.SizeOfVInt(size);

                int numBuckets = HashCodes.HashTableSize(size);

                _setPointersAndSizesArray[shardNumber].SetElementValue(
                    ((long)bitsPerSetFixedLengthPortion * shardOrdinal) + _bitsPerSetPointer,
                    _bitsPerSetSizeValue,
                    size);

                // Start every bucket empty, then place each element at its hash bucket, probing
                // linearly on collision.
                for (int j = 0; j < numBuckets; j++)
                {
                    _elementArray[shardNumber].SetElementValue(
                        (long)_bitsPerElement * (bucketCounter[shardNumber] + j), _bitsPerElement, emptyBucketValue);
                }

                int elementOrdinal = 0;
                for (int j = 0; j < size; j++)
                {
                    int elementOrdinalDelta = VarInt.ReadVInt(data, readPointer);
                    readPointer += VarInt.SizeOfVInt(elementOrdinalDelta);

                    int hashedBucket = VarInt.ReadVInt(data, readPointer);
                    readPointer += VarInt.SizeOfVInt(hashedBucket);

                    elementOrdinal += elementOrdinalDelta;

                    if (keyHasher is not null)
                    {
                        hashedBucket = keyHasher.GetRecordHash(elementOrdinal) & (numBuckets - 1);
                    }

                    while (_elementArray[shardNumber].GetElementValue(
                        (long)_bitsPerElement * (bucketCounter[shardNumber] + hashedBucket), _bitsPerElement)
                        != emptyBucketValue)
                    {
                        hashedBucket = (hashedBucket + 1) & (numBuckets - 1);
                    }

                    long bucketBitOffset = (long)_bitsPerElement * (bucketCounter[shardNumber] + hashedBucket);
                    _elementArray[shardNumber].ClearElementValue(bucketBitOffset, _bitsPerElement);
                    _elementArray[shardNumber].SetElementValue(bucketBitOffset, _bitsPerElement, elementOrdinal);
                }

                bucketCounter[shardNumber] += numBuckets;
            }

            _setPointersAndSizesArray[shardNumber].SetElementValue(
                (long)bitsPerSetFixedLengthPortion * shardOrdinal, _bitsPerSetPointer, bucketCounter[shardNumber]);
        }
    }

    /// <inheritdoc />
    public override void WriteSnapshot(HollowBlobOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (NumShards == 1)
        {
            WriteSnapshotShard(output, 0);
        }
        else
        {
            VarInt.WriteVInt(output, MaxOrdinal);
            for (int i = 0; i < NumShards; i++)
            {
                WriteSnapshotShard(output, i);
            }
        }

        CurrentCyclePopulated.SerializeBitsTo(output);

        _setPointersAndSizesArray = null;
        _elementArray = null;
    }

    /// <inheritdoc />
    protected override int TypeStateNumShards(int maxOrdinal)
    {
        (long maxElementOrdinal, int maxSetSize, long totalOfSetBuckets) = MeasureElements(numShards: 1);

        long bitsPerElement = 64 - BitOperations.LeadingZeroCount((ulong)(maxElementOrdinal + 1));
        long bitsPerSetSizeValue = 64 - BitOperations.LeadingZeroCount((ulong)maxSetSize);
        long bitsPerSetPointer = 64 - BitOperations.LeadingZeroCount((ulong)totalOfSetBuckets);

        long projectedSizeOfType = (bitsPerSetSizeValue + bitsPerSetPointer) * (maxOrdinal + 1) / 8;
        projectedSizeOfType += (bitsPerElement * totalOfSetBuckets) / 8;

        long targetMaxShardSize = StateEngine?.TargetMaxTypeShardSize
            ?? HollowWriteStateEngine.DefaultTargetMaxTypeShardSize;

        int targetNumShards = 1;
        while (targetMaxShardSize * targetNumShards < projectedSizeOfType)
        {
            targetNumShards *= 2;
        }

        return targetNumShards;
    }

    private void GatherStatistics()
    {
        int numShards = NumShards;
        _totalOfSetBuckets = new long[numShards];

        (long maxElementOrdinal, int maxSetSize, _) = MeasureElements(numShards, _totalOfSetBuckets);

        long maxShardTotalOfSetBuckets = 0;
        foreach (long total in _totalOfSetBuckets)
        {
            maxShardTotalOfSetBuckets = Math.Max(maxShardTotalOfSetBuckets, total);
        }

        _bitsPerElement = 64 - BitOperations.LeadingZeroCount((ulong)(maxElementOrdinal + 1));
        _bitsPerSetSizeValue = 64 - BitOperations.LeadingZeroCount((ulong)maxSetSize);
        _bitsPerSetPointer = 64 - BitOperations.LeadingZeroCount((ulong)maxShardTotalOfSetBuckets);
    }

    /// <summary>
    /// Walks every record, returning the largest element ordinal and set size, and optionally
    /// accumulating the bucket count per shard.
    /// </summary>
    private (long MaxElementOrdinal, int MaxSetSize, long TotalOfSetBuckets) MeasureElements(
        int numShards, long[]? totalOfSetBucketsPerShard = null)
    {
        long maxElementOrdinal = 0;
        int maxSetSize = 0;
        long totalOfSetBuckets = 0;

        int shardMask = numShards - 1;

        for (int ordinal = 0; ordinal <= MaxOrdinal; ordinal++)
        {
            if (!CurrentCyclePopulated.Get(ordinal) && !PreviousCyclePopulated.Get(ordinal))
            {
                continue;
            }

            long pointer = GetPointerForData(ordinal);
            IByteData data = GetByteDataForOrdinal(ordinal);

            int size = VarInt.ReadVInt(data, pointer);
            pointer += VarInt.SizeOfVInt(size);

            int numBuckets = HashCodes.HashTableSize(size);
            maxSetSize = Math.Max(maxSetSize, size);

            int elementOrdinal = 0;
            for (int j = 0; j < size; j++)
            {
                int elementOrdinalDelta = VarInt.ReadVInt(data, pointer);
                elementOrdinal += elementOrdinalDelta;
                maxElementOrdinal = Math.Max(maxElementOrdinal, elementOrdinal);
                pointer += VarInt.SizeOfVInt(elementOrdinalDelta);

                // Skip the hashed bucket that follows each element.
                pointer += VarInt.NextVLongSize(data, pointer);
            }

            totalOfSetBuckets += numBuckets;
            if (totalOfSetBucketsPerShard is not null)
            {
                totalOfSetBucketsPerShard[ordinal & shardMask] += numBuckets;
            }
        }

        return (maxElementOrdinal, maxSetSize, totalOfSetBuckets);
    }

    private void WriteSnapshotShard(HollowBlobOutput output, int shardNumber)
    {
        int bitsPerSetFixedLengthPortion = _bitsPerSetSizeValue + _bitsPerSetPointer;

        // 1) The shard's max ordinal.
        VarInt.WriteVInt(output, MaxShardOrdinal[shardNumber]);

        // 2) Statistics.
        VarInt.WriteVInt(output, _bitsPerSetPointer);
        VarInt.WriteVInt(output, _bitsPerSetSizeValue);
        VarInt.WriteVInt(output, _bitsPerElement);
        VarInt.WriteVLong(output, _totalOfSetBuckets[shardNumber]);

        // 3) The pointer-and-size array.
        int numSetFixedLengthLongs = MaxShardOrdinal[shardNumber] == -1
            ? 0
            : (int)((((long)(MaxShardOrdinal[shardNumber] + 1) * bitsPerSetFixedLengthPortion) - 1) / 64) + 1;
        HollowListTypeWriteState.WriteLongs(output, _setPointersAndSizesArray![shardNumber], numSetFixedLengthLongs);

        // 4) The element array.
        int numElementLongs = _totalOfSetBuckets[shardNumber] == 0
            ? 0
            : (int)(((_totalOfSetBuckets[shardNumber] * _bitsPerElement) - 1) / 64) + 1;
        HollowListTypeWriteState.WriteLongs(output, _elementArray![shardNumber], numElementLongs);
    }
}
