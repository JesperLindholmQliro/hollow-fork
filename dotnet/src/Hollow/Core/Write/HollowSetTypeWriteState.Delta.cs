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
/// The delta half of the set type's write state.
/// </summary>
public sealed partial class HollowSetTypeWriteState
{
    private int[]? _numSetsInDelta;
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
        int bitsPerSetFixedLengthPortion = _bitsPerSetSizeValue + _bitsPerSetPointer;
        long emptyBucketValue = (1L << _bitsPerElement) - 1;

        ThreadSafeBitSet deltaAdditions = toCyclePopulated.AndNot(fromCyclePopulated);

        _numSetsInDelta = new int[numShards];
        _numBucketsInDelta = new long[numShards];
        _setPointersAndSizesArray = new FixedLengthElementArray[numShards];
        _elementArray = new FixedLengthElementArray[numShards];
        _deltaAddedOrdinals = new ByteDataArray[numShards];
        _deltaRemovedOrdinals = new ByteDataArray[numShards];

        foreach (int addedOrdinal in deltaAdditions.EnumerateSetBits())
        {
            _numSetsInDelta[addedOrdinal & shardMask]++;
            int size = VarInt.ReadVInt(GetByteDataForOrdinal(addedOrdinal), GetPointerForData(addedOrdinal));
            _numBucketsInDelta[addedOrdinal & shardMask] += HashCodes.HashTableSize(size);
        }

        for (int i = 0; i < numShards; i++)
        {
            _setPointersAndSizesArray[i] = new FixedLengthElementArray(
                WastefulRecycler.DefaultInstance, (long)_numSetsInDelta[i] * bitsPerSetFixedLengthPortion);
            _elementArray[i] = new FixedLengthElementArray(
                WastefulRecycler.DefaultInstance, _numBucketsInDelta[i] * _bitsPerElement);
            _deltaAddedOrdinals[i] = new ByteDataArray(WastefulRecycler.DefaultInstance);
            _deltaRemovedOrdinals[i] = new ByteDataArray(WastefulRecycler.DefaultInstance);
        }

        int[] setCounter = new int[numShards];
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

                _setPointersAndSizesArray[shardNumber].SetElementValue(
                    (long)bitsPerSetFixedLengthPortion * setCounter[shardNumber], _bitsPerSetPointer, endBucketPosition);
                _setPointersAndSizesArray[shardNumber].SetElementValue(
                    ((long)bitsPerSetFixedLengthPortion * setCounter[shardNumber]) + _bitsPerSetPointer,
                    _bitsPerSetSizeValue,
                    size);

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

                bucketCounter[shardNumber] = endBucketPosition;
                setCounter[shardNumber]++;

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

        _setPointersAndSizesArray = null;
        _elementArray = null;
        _deltaAddedOrdinals = null;
        _deltaRemovedOrdinals = null;
    }

    private void WriteCalculatedDeltaShard(HollowBlobOutput output, int shardNumber)
    {
        int bitsPerSetFixedLengthPortion = _bitsPerSetSizeValue + _bitsPerSetPointer;

        // 1) The shard's max ordinal.
        VarInt.WriteVInt(output, MaxShardOrdinal[shardNumber]);

        // 2) The removed and added ordinals.
        HollowObjectTypeWriteState.WriteOrdinals(output, _deltaRemovedOrdinals![shardNumber]);
        HollowObjectTypeWriteState.WriteOrdinals(output, _deltaAddedOrdinals![shardNumber]);

        // 3) Statistics.
        VarInt.WriteVInt(output, _bitsPerSetPointer);
        VarInt.WriteVInt(output, _bitsPerSetSizeValue);
        VarInt.WriteVInt(output, _bitsPerElement);
        VarInt.WriteVLong(output, _totalOfSetBuckets[shardNumber]);

        // 4) The pointer-and-size array of the added records.
        int numSetFixedLengthLongs = _numSetsInDelta![shardNumber] == 0
            ? 0
            : (int)((((long)_numSetsInDelta[shardNumber] * bitsPerSetFixedLengthPortion) - 1) / 64) + 1;
        HollowListTypeWriteState.WriteLongs(output, _setPointersAndSizesArray![shardNumber], numSetFixedLengthLongs);

        // 5) The element array of the added records.
        int numElementLongs = _numBucketsInDelta![shardNumber] == 0
            ? 0
            : (int)(((_numBucketsInDelta[shardNumber] * _bitsPerElement) - 1) / 64) + 1;
        HollowListTypeWriteState.WriteLongs(output, _elementArray![shardNumber], numElementLongs);
    }
}
