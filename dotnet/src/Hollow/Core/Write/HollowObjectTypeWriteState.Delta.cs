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
/// The delta half of the object type's write state: the records added since the previous cycle, packed
/// as they would be in a snapshot, alongside gap-encoded lists of the ordinals added and removed.
/// </summary>
public sealed partial class HollowObjectTypeWriteState
{
    private ByteDataArray[]? _deltaAddedOrdinals;
    private ByteDataArray[]? _deltaRemovedOrdinals;

    /// <inheritdoc />
    public override void CalculateDelta(
        ThreadSafeBitSet fromCyclePopulated, ThreadSafeBitSet toCyclePopulated, bool isReverse)
    {
        ArgumentNullException.ThrowIfNull(fromCyclePopulated);
        ArgumentNullException.ThrowIfNull(toCyclePopulated);

        FieldStatistics fieldStats = _fieldStats
            ?? throw new InvalidOperationException($"{nameof(PrepareForWrite)} has not been called");

        int numShards = NumShardsForDelta(isReverse);
        int numBitsPerRecord = fieldStats.NumBitsPerRecord;

        ThreadSafeBitSet deltaAdditions = toCyclePopulated.AndNot(fromCyclePopulated);

        _fixedLengthLongArray = new FixedLengthElementArray[numShards];
        _varLengthByteArrays = new ByteDataArray[numShards][];
        _recordBitOffset = new long[numShards];
        _deltaAddedOrdinals = new ByteDataArray[numShards];
        _deltaRemovedOrdinals = new ByteDataArray[numShards];

        int shardMask = numShards - 1;

        // Size each shard's buffer to exactly the records it will receive.
        int[] numAddedRecordsInShard = new int[numShards];
        foreach (int addedOrdinal in deltaAdditions.EnumerateSetBits())
        {
            numAddedRecordsInShard[addedOrdinal & shardMask]++;
        }

        for (int i = 0; i < numShards; i++)
        {
            _fixedLengthLongArray[i] = new FixedLengthElementArray(
                WastefulRecycler.DefaultInstance, (long)numAddedRecordsInShard[i] * numBitsPerRecord);
            _varLengthByteArrays[i] = new ByteDataArray[Schema.FieldCount];
            _deltaAddedOrdinals[i] = new ByteDataArray(WastefulRecycler.DefaultInstance);
            _deltaRemovedOrdinals[i] = new ByteDataArray(WastefulRecycler.DefaultInstance);
        }

        int[] previousRemovedOrdinal = new int[numShards];
        int[] previousAddedOrdinal = new int[numShards];

        for (int ordinal = 0; ordinal <= MaxOrdinal; ordinal++)
        {
            int shardNumber = ordinal & shardMask;
            int shardOrdinal = ordinal / numShards;

            if (deltaAdditions.Get(ordinal))
            {
                AddRecord(
                    ordinal, _recordBitOffset[shardNumber], _fixedLengthLongArray[shardNumber], _varLengthByteArrays[shardNumber]);
                _recordBitOffset[shardNumber] += numBitsPerRecord;

                // The ordinal lists are gap-encoded, so only the step from the last one is written.
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

        _fixedLengthLongArray = null;
        _varLengthByteArrays = null;
        _recordBitOffset = null;
        _deltaAddedOrdinals = null;
        _deltaRemovedOrdinals = null;
    }

    private void WriteCalculatedDeltaShard(HollowBlobOutput output, int shardNumber, int[] maxShardOrdinal)
    {
        FieldStatistics fieldStats = _fieldStats!;

        // 1) The shard's max ordinal.
        VarInt.WriteVInt(output, maxShardOrdinal[shardNumber]);

        // 2) The removed and added ordinals.
        WriteOrdinals(output, _deltaRemovedOrdinals![shardNumber]);
        WriteOrdinals(output, _deltaAddedOrdinals![shardNumber]);

        // 3) The width of each fixed-length field.
        for (int i = 0; i < Schema.FieldCount; i++)
        {
            VarInt.WriteVInt(output, fieldStats.GetMaxBitsForField(i));
        }

        // 4) The fixed-length bit string of the added records.
        long numBitsRequired = _recordBitOffset![shardNumber];
        long numLongsRequired = numBitsRequired == 0 ? 0 : ((numBitsRequired - 1) / 64) + 1;
        _fixedLengthLongArray![shardNumber].WriteTo(output, numLongsRequired);

        // 5) The variable-length payload of each field.
        foreach (ByteDataArray? varLengthBuffer in _varLengthByteArrays![shardNumber])
        {
            if (varLengthBuffer is null)
            {
                VarInt.WriteVLong(output, 0);
            }
            else
            {
                VarInt.WriteVLong(output, varLengthBuffer.Length);
                varLengthBuffer.UnderlyingArray.WriteTo(output.Stream, 0, varLengthBuffer.Length);
            }
        }
    }

    /// <summary>
    /// Writes a gap-encoded ordinal list, preceded by its length in bytes.
    /// </summary>
    internal static void WriteOrdinals(HollowBlobOutput output, ByteDataArray ordinals)
    {
        VarInt.WriteVLong(output, ordinals.Length);
        if (ordinals.Length > 0)
        {
            ordinals.UnderlyingArray.WriteTo(output.Stream, 0, ordinals.Length);
        }
    }
}
