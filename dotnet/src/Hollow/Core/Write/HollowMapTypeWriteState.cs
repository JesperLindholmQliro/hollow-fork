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
/// The write state of a map type: each record's entries are laid out in an open-addressed hash table
/// of its own, with the key and value ordinals packed side by side in each bucket.
/// </summary>
/// <remarks>
/// As with sets, a schema-declared hash key places each entry by its key's hash rather than by the key
/// record's ordinal — see <see cref="HollowSetTypeWriteState"/>.
/// </remarks>
public sealed partial class HollowMapTypeWriteState : HollowTypeWriteState
{
    private int _bitsPerKeyElement;
    private int _bitsPerValueElement;
    private int _bitsPerMapSizeValue;
    private int _bitsPerMapPointer;
    private long[] _totalOfMapBuckets = [];

    private FixedLengthElementArray[]? _mapPointersAndSizesArray;
    private FixedLengthElementArray[]? _entryData;

    /// <summary>
    /// Initialises a write state for <paramref name="schema"/>.
    /// </summary>
    public HollowMapTypeWriteState(HollowMapSchema schema, int numShards = -1)
        : base(schema, numShards)
    {
    }

    /// <summary>The schema of the type this state holds.</summary>
    public new HollowMapSchema Schema => (HollowMapSchema)base.Schema;

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
        int bitsPerMapFixedLengthPortion = _bitsPerMapSizeValue + _bitsPerMapPointer;
        int bitsPerMapEntry = _bitsPerKeyElement + _bitsPerValueElement;
        long emptyBucketKeyValue = (1L << _bitsPerKeyElement) - 1;

        _mapPointersAndSizesArray = new FixedLengthElementArray[numShards];
        _entryData = new FixedLengthElementArray[numShards];

        for (int i = 0; i < numShards; i++)
        {
            _mapPointersAndSizesArray[i] = new FixedLengthElementArray(
                WastefulRecycler.DefaultInstance, (long)bitsPerMapFixedLengthPortion * (MaxShardOrdinal[i] + 1));
            _entryData[i] = new FixedLengthElementArray(
                WastefulRecycler.DefaultInstance, (long)bitsPerMapEntry * _totalOfMapBuckets[i]);
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

                _mapPointersAndSizesArray[shardNumber].SetElementValue(
                    ((long)bitsPerMapFixedLengthPortion * shardOrdinal) + _bitsPerMapPointer,
                    _bitsPerMapSizeValue,
                    size);

                // An all-ones key marks a bucket empty.
                for (int j = 0; j < numBuckets; j++)
                {
                    _entryData[shardNumber].SetElementValue(
                        (long)bitsPerMapEntry * (bucketCounter[shardNumber] + j),
                        _bitsPerKeyElement,
                        emptyBucketKeyValue);
                }

                int keyElementOrdinal = 0;
                for (int j = 0; j < size; j++)
                {
                    int keyElementOrdinalDelta = VarInt.ReadVInt(data, readPointer);
                    readPointer += VarInt.SizeOfVInt(keyElementOrdinalDelta);

                    int valueElementOrdinal = VarInt.ReadVInt(data, readPointer);
                    readPointer += VarInt.SizeOfVInt(valueElementOrdinal);

                    int hashedBucket = VarInt.ReadVInt(data, readPointer);
                    readPointer += VarInt.SizeOfVInt(hashedBucket);

                    keyElementOrdinal += keyElementOrdinalDelta;

                    if (keyHasher is not null)
                    {
                        hashedBucket = keyHasher.GetRecordHash(keyElementOrdinal) & (numBuckets - 1);
                    }

                    while (_entryData[shardNumber].GetElementValue(
                        (long)bitsPerMapEntry * (bucketCounter[shardNumber] + hashedBucket), _bitsPerKeyElement)
                        != emptyBucketKeyValue)
                    {
                        hashedBucket = (hashedBucket + 1) & (numBuckets - 1);
                    }

                    long mapEntryBitOffset = (long)bitsPerMapEntry * (bucketCounter[shardNumber] + hashedBucket);
                    _entryData[shardNumber].ClearElementValue(mapEntryBitOffset, bitsPerMapEntry);
                    _entryData[shardNumber].SetElementValue(mapEntryBitOffset, _bitsPerKeyElement, keyElementOrdinal);
                    _entryData[shardNumber].SetElementValue(
                        mapEntryBitOffset + _bitsPerKeyElement, _bitsPerValueElement, valueElementOrdinal);
                }

                bucketCounter[shardNumber] += numBuckets;
            }

            _mapPointersAndSizesArray[shardNumber].SetElementValue(
                (long)bitsPerMapFixedLengthPortion * shardOrdinal, _bitsPerMapPointer, bucketCounter[shardNumber]);
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

        _mapPointersAndSizesArray = null;
        _entryData = null;
    }

    /// <inheritdoc />
    protected override int TypeStateNumShards(int maxOrdinal)
    {
        Measurements measurements = MeasureEntries(numShards: 1);

        long bitsPerKeyElement = 64 - BitOperations.LeadingZeroCount((ulong)(measurements.MaxKeyOrdinal + 1));
        long bitsPerValueElement = measurements.MaxValueOrdinal == 0
            ? 1
            : 64 - BitOperations.LeadingZeroCount((ulong)measurements.MaxValueOrdinal);
        long bitsPerMapSizeValue = 64 - BitOperations.LeadingZeroCount((ulong)measurements.MaxMapSize);
        long bitsPerMapPointer = 64 - BitOperations.LeadingZeroCount((ulong)measurements.TotalOfMapBuckets);

        long projectedSizeOfType = (bitsPerMapSizeValue + bitsPerMapPointer) * (maxOrdinal + 1) / 8;
        projectedSizeOfType += ((bitsPerKeyElement + bitsPerValueElement) * measurements.TotalOfMapBuckets) / 8;

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
        _totalOfMapBuckets = new long[numShards];

        Measurements measurements = MeasureEntries(numShards, _totalOfMapBuckets);

        long maxShardTotalOfMapBuckets = 0;
        foreach (long total in _totalOfMapBuckets)
        {
            maxShardTotalOfMapBuckets = Math.Max(maxShardTotalOfMapBuckets, total);
        }

        _bitsPerKeyElement = 64 - BitOperations.LeadingZeroCount((ulong)(measurements.MaxKeyOrdinal + 1));
        _bitsPerValueElement = measurements.MaxValueOrdinal == 0
            ? 1
            : 64 - BitOperations.LeadingZeroCount((ulong)measurements.MaxValueOrdinal);
        _bitsPerMapSizeValue = 64 - BitOperations.LeadingZeroCount((ulong)measurements.MaxMapSize);
        _bitsPerMapPointer = 64 - BitOperations.LeadingZeroCount((ulong)maxShardTotalOfMapBuckets);
    }

    private Measurements MeasureEntries(int numShards, long[]? totalOfMapBucketsPerShard = null)
    {
        long maxKeyOrdinal = 0;
        long maxValueOrdinal = 0;
        int maxMapSize = 0;
        long totalOfMapBuckets = 0;

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
            maxMapSize = Math.Max(maxMapSize, size);

            int keyOrdinal = 0;
            for (int j = 0; j < size; j++)
            {
                int keyOrdinalDelta = VarInt.ReadVInt(data, pointer);
                pointer += VarInt.SizeOfVInt(keyOrdinalDelta);

                int valueOrdinal = VarInt.ReadVInt(data, pointer);
                pointer += VarInt.SizeOfVInt(valueOrdinal);

                keyOrdinal += keyOrdinalDelta;
                maxKeyOrdinal = Math.Max(maxKeyOrdinal, keyOrdinal);
                maxValueOrdinal = Math.Max(maxValueOrdinal, valueOrdinal);

                // Skip the hashed bucket that follows each entry.
                pointer += VarInt.NextVLongSize(data, pointer);
            }

            totalOfMapBuckets += numBuckets;
            if (totalOfMapBucketsPerShard is not null)
            {
                totalOfMapBucketsPerShard[ordinal & shardMask] += numBuckets;
            }
        }

        return new Measurements(maxKeyOrdinal, maxValueOrdinal, maxMapSize, totalOfMapBuckets);
    }

    private void WriteSnapshotShard(HollowBlobOutput output, int shardNumber)
    {
        int bitsPerMapFixedLengthPortion = _bitsPerMapSizeValue + _bitsPerMapPointer;
        int bitsPerMapEntry = _bitsPerKeyElement + _bitsPerValueElement;

        // 1) The shard's max ordinal.
        VarInt.WriteVInt(output, MaxShardOrdinal[shardNumber]);

        // 2) Statistics.
        VarInt.WriteVInt(output, _bitsPerMapPointer);
        VarInt.WriteVInt(output, _bitsPerMapSizeValue);
        VarInt.WriteVInt(output, _bitsPerKeyElement);
        VarInt.WriteVInt(output, _bitsPerValueElement);
        VarInt.WriteVLong(output, _totalOfMapBuckets[shardNumber]);

        // 3) The pointer-and-size array.
        int numMapFixedLengthLongs = MaxShardOrdinal[shardNumber] == -1
            ? 0
            : (int)((((long)(MaxShardOrdinal[shardNumber] + 1) * bitsPerMapFixedLengthPortion) - 1) / 64) + 1;
        HollowListTypeWriteState.WriteLongs(output, _mapPointersAndSizesArray![shardNumber], numMapFixedLengthLongs);

        // 4) The entry array.
        int numElementLongs = _totalOfMapBuckets[shardNumber] == 0
            ? 0
            : (int)(((_totalOfMapBuckets[shardNumber] * bitsPerMapEntry) - 1) / 64) + 1;
        HollowListTypeWriteState.WriteLongs(output, _entryData![shardNumber], numElementLongs);
    }

    private readonly record struct Measurements(
        long MaxKeyOrdinal, long MaxValueOrdinal, int MaxMapSize, long TotalOfMapBuckets);
}
