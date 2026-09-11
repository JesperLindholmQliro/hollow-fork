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

namespace Hollow.Core.Write;

/// <summary>
/// The delta half of the map type's write state.
/// </summary>
public sealed partial class HollowMapTypeWriteState
{
    private int[]? _numMapsInDelta;
    private long[]? _numBucketsInDelta;
    private ByteDataArray[]? _deltaAddedOrdinals;
    private ByteDataArray[]? _deltaRemovedOrdinals;

    /// <inheritdoc />
    public override void CalculateDelta(ThreadSafeBitSet fromCyclePopulated, ThreadSafeBitSet toCyclePopulated)
    {
        ArgumentNullException.ThrowIfNull(fromCyclePopulated);
        ArgumentNullException.ThrowIfNull(toCyclePopulated);

        int numShards = NumShards;
        int shardMask = numShards - 1;
        int bitsPerMapFixedLengthPortion = _bitsPerMapSizeValue + _bitsPerMapPointer;
        int bitsPerMapEntry = _bitsPerKeyElement + _bitsPerValueElement;
        long emptyBucketKeyValue = (1L << _bitsPerKeyElement) - 1;

        ThreadSafeBitSet deltaAdditions = toCyclePopulated.AndNot(fromCyclePopulated);

        _numMapsInDelta = new int[numShards];
        _numBucketsInDelta = new long[numShards];
        _mapPointersAndSizesArray = new FixedLengthElementArray[numShards];
        _entryData = new FixedLengthElementArray[numShards];
        _deltaAddedOrdinals = new ByteDataArray[numShards];
        _deltaRemovedOrdinals = new ByteDataArray[numShards];

        foreach (int addedOrdinal in deltaAdditions.EnumerateSetBits())
        {
            _numMapsInDelta[addedOrdinal & shardMask]++;
            int size = VarInt.ReadVInt(GetByteDataForOrdinal(addedOrdinal), GetPointerForData(addedOrdinal));
            _numBucketsInDelta[addedOrdinal & shardMask] += HashCodes.HashTableSize(size);
        }

        for (int i = 0; i < numShards; i++)
        {
            _mapPointersAndSizesArray[i] = new FixedLengthElementArray(
                WastefulRecycler.DefaultInstance, (long)_numMapsInDelta[i] * bitsPerMapFixedLengthPortion);
            _entryData[i] = new FixedLengthElementArray(
                WastefulRecycler.DefaultInstance, _numBucketsInDelta[i] * bitsPerMapEntry);
            _deltaAddedOrdinals[i] = new ByteDataArray(WastefulRecycler.DefaultInstance);
            _deltaRemovedOrdinals[i] = new ByteDataArray(WastefulRecycler.DefaultInstance);
        }

        int[] mapCounter = new int[numShards];
        long[] bucketCounter = new long[numShards];
        int[] previousRemovedOrdinal = new int[numShards];
        int[] previousAddedOrdinal = new int[numShards];

        for (int ordinal = 0; ordinal <= MaxOrdinal; ordinal++)
        {
            int shardNumber = ordinal & shardMask;
            int shardOrdinal = ordinal / numShards;

            if (deltaAdditions.Get(ordinal))
            {
                long readPointer = GetPointerForData(ordinal);
                IByteData data = GetByteDataForOrdinal(ordinal);

                int size = VarInt.ReadVInt(data, readPointer);
                readPointer += VarInt.SizeOfVInt(size);

                int numBuckets = HashCodes.HashTableSize(size);
                long endBucketPosition = bucketCounter[shardNumber] + numBuckets;

                _mapPointersAndSizesArray[shardNumber].SetElementValue(
                    (long)bitsPerMapFixedLengthPortion * mapCounter[shardNumber], _bitsPerMapPointer, endBucketPosition);
                _mapPointersAndSizesArray[shardNumber].SetElementValue(
                    ((long)bitsPerMapFixedLengthPortion * mapCounter[shardNumber]) + _bitsPerMapPointer,
                    _bitsPerMapSizeValue,
                    size);

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

                bucketCounter[shardNumber] = endBucketPosition;
                mapCounter[shardNumber]++;

                VarInt.WriteVInt(_deltaAddedOrdinals[shardNumber], shardOrdinal - previousAddedOrdinal[shardNumber]);
                previousAddedOrdinal[shardNumber] = shardOrdinal;
            }
            else if (fromCyclePopulated.Get(ordinal) && !toCyclePopulated.Get(ordinal))
            {
                VarInt.WriteVInt(_deltaRemovedOrdinals[shardNumber], shardOrdinal - previousRemovedOrdinal[shardNumber]);
                previousRemovedOrdinal[shardNumber] = shardOrdinal;
            }
        }
    }

    /// <inheritdoc />
    public override void WriteCalculatedDelta(HollowBlobOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (_deltaAddedOrdinals is null)
        {
            throw new InvalidOperationException($"{nameof(CalculateDelta)} has not been called");
        }

        if (NumShards == 1)
        {
            WriteCalculatedDeltaShard(output, 0);
        }
        else
        {
            VarInt.WriteVInt(output, MaxOrdinal);
            for (int i = 0; i < NumShards; i++)
            {
                WriteCalculatedDeltaShard(output, i);
            }
        }

        _mapPointersAndSizesArray = null;
        _entryData = null;
        _deltaAddedOrdinals = null;
        _deltaRemovedOrdinals = null;
    }

    private void WriteCalculatedDeltaShard(HollowBlobOutput output, int shardNumber)
    {
        int bitsPerMapFixedLengthPortion = _bitsPerMapSizeValue + _bitsPerMapPointer;
        int bitsPerMapEntry = _bitsPerKeyElement + _bitsPerValueElement;

        // 1) The shard's max ordinal.
        VarInt.WriteVInt(output, MaxShardOrdinal[shardNumber]);

        // 2) The removed and added ordinals.
        HollowObjectTypeWriteState.WriteOrdinals(output, _deltaRemovedOrdinals![shardNumber]);
        HollowObjectTypeWriteState.WriteOrdinals(output, _deltaAddedOrdinals![shardNumber]);

        // 3) Statistics.
        VarInt.WriteVInt(output, _bitsPerMapPointer);
        VarInt.WriteVInt(output, _bitsPerMapSizeValue);
        VarInt.WriteVInt(output, _bitsPerKeyElement);
        VarInt.WriteVInt(output, _bitsPerValueElement);
        VarInt.WriteVLong(output, _totalOfMapBuckets[shardNumber]);

        // 4) The pointer-and-size array of the added records.
        int numMapFixedLengthLongs = _numMapsInDelta![shardNumber] == 0
            ? 0
            : (int)((((long)_numMapsInDelta[shardNumber] * bitsPerMapFixedLengthPortion) - 1) / 64) + 1;
        HollowListTypeWriteState.WriteLongs(output, _mapPointersAndSizesArray![shardNumber], numMapFixedLengthLongs);

        // 5) The entry array of the added records.
        int numElementLongs = _numBucketsInDelta![shardNumber] == 0
            ? 0
            : (int)(((_numBucketsInDelta[shardNumber] * bitsPerMapEntry) - 1) / 64) + 1;
        HollowListTypeWriteState.WriteLongs(output, _entryData![shardNumber], numElementLongs);
    }
}
