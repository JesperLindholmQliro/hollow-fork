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
/// The delta half of the list type's write state.
/// </summary>
public sealed partial class HollowListTypeWriteState
{
    private int[]? _numListsInDelta;
    private long[]? _numElementsInDelta;
    private ByteDataArray[]? _deltaAddedOrdinals;
    private ByteDataArray[]? _deltaRemovedOrdinals;

    /// <inheritdoc />
    public override void CalculateDelta(
        ThreadSafeBitSet fromCyclePopulated, ThreadSafeBitSet toCyclePopulated, bool isReverse)
    {
        ArgumentNullException.ThrowIfNull(fromCyclePopulated);
        ArgumentNullException.ThrowIfNull(toCyclePopulated);

        int numShards = NumShardsForDelta(isReverse);
        int shardMask = numShards - 1;

        ThreadSafeBitSet deltaAdditions = toCyclePopulated.AndNot(fromCyclePopulated);

        _numListsInDelta = new int[numShards];
        _numElementsInDelta = new long[numShards];
        _listPointerArray = new FixedLengthElementArray[numShards];
        _elementArray = new FixedLengthElementArray[numShards];
        _deltaAddedOrdinals = new ByteDataArray[numShards];
        _deltaRemovedOrdinals = new ByteDataArray[numShards];

        foreach (int addedOrdinal in deltaAdditions.EnumerateSetBits())
        {
            _numListsInDelta[addedOrdinal & shardMask]++;
            _numElementsInDelta[addedOrdinal & shardMask] +=
                VarInt.ReadVInt(GetByteDataForOrdinal(addedOrdinal), GetPointerForData(addedOrdinal));
        }

        for (int i = 0; i < numShards; i++)
        {
            _listPointerArray[i] = new FixedLengthElementArray(
                WastefulRecycler.DefaultInstance, (long)_numListsInDelta[i] * _bitsPerListPointer);
            _elementArray[i] = new FixedLengthElementArray(
                WastefulRecycler.DefaultInstance, _numElementsInDelta[i] * _bitsPerElement);
            _deltaAddedOrdinals[i] = new ByteDataArray(WastefulRecycler.DefaultInstance);
            _deltaRemovedOrdinals[i] = new ByteDataArray(WastefulRecycler.DefaultInstance);
        }

        int[] listCounter = new int[numShards];
        long[] elementCounter = new long[numShards];
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

                // In a delta the pointer array is indexed by position among the added records, not by
                // ordinal, so it records the end of this record's run directly.
                _listPointerArray[shardNumber].SetElementValue(
                    (long)_bitsPerListPointer * listCounter[shardNumber],
                    _bitsPerListPointer,
                    elementCounter[shardNumber] + size);

                for (int j = 0; j < size; j++)
                {
                    int elementOrdinal = VarInt.ReadVInt(data, readPointer);
                    readPointer += VarInt.SizeOfVInt(elementOrdinal);

                    _elementArray[shardNumber].SetElementValue(
                        (long)_bitsPerElement * elementCounter[shardNumber], _bitsPerElement, elementOrdinal);
                    elementCounter[shardNumber]++;
                }

                listCounter[shardNumber]++;

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
    public override void WriteCalculatedDelta(HollowBlobOutput output, bool isReverse, int[] maxShardOrdinal)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(maxShardOrdinal);

        if (_deltaAddedOrdinals is null)
        {
            throw new InvalidOperationException($"{nameof(CalculateDelta)} has not been called");
        }

        int numShards = NumShardsForDelta(isReverse);

        if (numShards == 1)
        {
            WriteCalculatedDeltaShard(output, 0, maxShardOrdinal);
        }
        else
        {
            VarInt.WriteVInt(output, MaxOrdinal);
            for (int i = 0; i < numShards; i++)
            {
                WriteCalculatedDeltaShard(output, i, maxShardOrdinal);
            }
        }

        _listPointerArray = null;
        _elementArray = null;
        _deltaAddedOrdinals = null;
        _deltaRemovedOrdinals = null;
    }

    private void WriteCalculatedDeltaShard(HollowBlobOutput output, int shardNumber, int[] maxShardOrdinal)
    {
        // 1) The shard's max ordinal.
        VarInt.WriteVInt(output, maxShardOrdinal[shardNumber]);

        // 2) The removed and added ordinals.
        HollowObjectTypeWriteState.WriteOrdinals(output, _deltaRemovedOrdinals![shardNumber]);
        HollowObjectTypeWriteState.WriteOrdinals(output, _deltaAddedOrdinals![shardNumber]);

        // 3) Statistics.
        VarInt.WriteVInt(output, _bitsPerListPointer);
        VarInt.WriteVInt(output, _bitsPerElement);
        VarInt.WriteVLong(output, _totalOfListSizes[shardNumber]);

        // 4) The list pointer array of the added records.
        int numListPointerLongs = _numListsInDelta![shardNumber] == 0
            ? 0
            : (int)((((long)_numListsInDelta[shardNumber] * _bitsPerListPointer) - 1) / 64) + 1;
        WriteLongs(output, _listPointerArray![shardNumber], numListPointerLongs);

        // 5) The element array of the added records.
        int numElementLongs = _numElementsInDelta![shardNumber] == 0
            ? 0
            : (int)(((_numElementsInDelta[shardNumber] * _bitsPerElement) - 1) / 64) + 1;
        WriteLongs(output, _elementArray![shardNumber], numElementLongs);
    }
}
