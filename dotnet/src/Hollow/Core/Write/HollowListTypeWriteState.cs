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
/// The write state of a list type: element ordinals are concatenated into one bit-packed array, and
/// each record stores a pointer to the end of its own run.
/// </summary>
public sealed partial class HollowListTypeWriteState : HollowTypeWriteState
{
    private int _bitsPerListPointer;
    private int _bitsPerElement;
    private long[] _totalOfListSizes = [];

    private FixedLengthElementArray[]? _listPointerArray;
    private FixedLengthElementArray[]? _elementArray;

    /// <summary>
    /// Initialises a write state for <paramref name="schema"/>.
    /// </summary>
    public HollowListTypeWriteState(HollowListSchema schema, int numShards = -1)
        : base(schema, numShards)
    {
    }

    /// <summary>The schema of the type this state holds.</summary>
    public new HollowListSchema Schema => (HollowListSchema)base.Schema;

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

        _listPointerArray = new FixedLengthElementArray[numShards];
        _elementArray = new FixedLengthElementArray[numShards];

        for (int i = 0; i < numShards; i++)
        {
            _listPointerArray[i] = new FixedLengthElementArray(
                WastefulRecycler.DefaultInstance, (long)_bitsPerListPointer * (MaxShardOrdinal[i] + 1));
            _elementArray[i] = new FixedLengthElementArray(
                WastefulRecycler.DefaultInstance, (long)_bitsPerElement * _totalOfListSizes[i]);
        }

        long[] elementCounter = new long[numShards];
        int shardMask = numShards - 1;

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

                for (int j = 0; j < size; j++)
                {
                    int elementOrdinal = VarInt.ReadVInt(data, readPointer);
                    readPointer += VarInt.SizeOfVInt(elementOrdinal);

                    _elementArray[shardNumber].SetElementValue(
                        (long)_bitsPerElement * elementCounter[shardNumber], _bitsPerElement, elementOrdinal);
                    elementCounter[shardNumber]++;
                }
            }

            // Every ordinal, populated or not, records the end of its run, because the next record
            // reads it as its own start.
            _listPointerArray[shardNumber].SetElementValue(
                (long)_bitsPerListPointer * shardOrdinal, _bitsPerListPointer, elementCounter[shardNumber]);
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

        _listPointerArray = null;
        _elementArray = null;
    }

    /// <inheritdoc />
    protected override int TypeStateNumShards(int maxOrdinal)
    {
        (long maxElementOrdinal, long totalOfListSizes) = MeasureElements();

        long bitsPerElement = maxElementOrdinal == 0 ? 1 : 64 - BitOperations.LeadingZeroCount((ulong)maxElementOrdinal);
        long bitsPerListPointer = totalOfListSizes == 0 ? 1 : 64 - BitOperations.LeadingZeroCount((ulong)totalOfListSizes);

        long projectedSizeOfType = (bitsPerElement * totalOfListSizes) / 8;
        projectedSizeOfType += ((bitsPerListPointer * maxOrdinal) + 1) / 8;

        long targetMaxShardSize = StateEngine?.TargetMaxTypeShardSize
            ?? HollowWriteStateEngine.DefaultTargetMaxTypeShardSize;

        int targetNumShards = 1;
        while (targetMaxShardSize * targetNumShards < projectedSizeOfType)
        {
            targetNumShards *= 2;
        }

        return targetNumShards;
    }

    private (long MaxElementOrdinal, long TotalOfListSizes) MeasureElements()
    {
        long maxElementOrdinal = 0;
        long totalOfListSizes = 0;

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

            for (int j = 0; j < size; j++)
            {
                int elementOrdinal = VarInt.ReadVInt(data, pointer);
                maxElementOrdinal = Math.Max(maxElementOrdinal, elementOrdinal);
                pointer += VarInt.SizeOfVInt(elementOrdinal);
            }

            totalOfListSizes += size;
        }

        return (maxElementOrdinal, totalOfListSizes);
    }

    private void GatherStatistics()
    {
        int numShards = NumShards;
        int shardMask = numShards - 1;

        long maxElementOrdinal = 0;
        _totalOfListSizes = new long[numShards];

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

            for (int j = 0; j < size; j++)
            {
                int elementOrdinal = VarInt.ReadVInt(data, pointer);
                maxElementOrdinal = Math.Max(maxElementOrdinal, elementOrdinal);
                pointer += VarInt.SizeOfVInt(elementOrdinal);
            }

            _totalOfListSizes[ordinal & shardMask] += size;
        }

        long maxShardTotalOfListSizes = 0;
        foreach (long total in _totalOfListSizes)
        {
            maxShardTotalOfListSizes = Math.Max(maxShardTotalOfListSizes, total);
        }

        _bitsPerElement = maxElementOrdinal == 0
            ? 1
            : 64 - BitOperations.LeadingZeroCount((ulong)maxElementOrdinal);
        _bitsPerListPointer = maxShardTotalOfListSizes == 0
            ? 1
            : 64 - BitOperations.LeadingZeroCount((ulong)maxShardTotalOfListSizes);
    }

    private void WriteSnapshotShard(HollowBlobOutput output, int shardNumber)
    {
        // 1) The shard's max ordinal.
        VarInt.WriteVInt(output, MaxShardOrdinal[shardNumber]);

        // 2) Statistics.
        VarInt.WriteVInt(output, _bitsPerListPointer);
        VarInt.WriteVInt(output, _bitsPerElement);
        VarInt.WriteVLong(output, _totalOfListSizes[shardNumber]);

        // 3) The list pointer array.
        int numListPointerLongs = MaxShardOrdinal[shardNumber] == -1
            ? 0
            : (int)((((long)(MaxShardOrdinal[shardNumber] + 1) * _bitsPerListPointer) - 1) / 64) + 1;
        WriteLongs(output, _listPointerArray![shardNumber], numListPointerLongs);

        // 4) The element array.
        int numElementLongs = _totalOfListSizes[shardNumber] == 0
            ? 0
            : (int)(((_totalOfListSizes[shardNumber] * _bitsPerElement) - 1) / 64) + 1;
        WriteLongs(output, _elementArray![shardNumber], numElementLongs);
    }

    /// <summary>
    /// Writes a count followed by that many words, which is the same framing
    /// <see cref="FixedLengthElementArray.NewFrom(Read.HollowBlobInput, IArraySegmentRecycler)"/> reads.
    /// </summary>
    internal static void WriteLongs(HollowBlobOutput output, FixedLengthElementArray array, int numLongs)
    {
        VarInt.WriteVInt(output, numLongs);
        for (int i = 0; i < numLongs; i++)
        {
            output.WriteInt64(array.Get(i));
        }
    }
}
