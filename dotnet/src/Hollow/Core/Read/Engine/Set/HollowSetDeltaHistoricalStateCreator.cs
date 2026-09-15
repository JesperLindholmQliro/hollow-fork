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
using Hollow.Core.Memory.Encoding;
using Hollow.Core.Memory.Pool;
using Hollow.Core.Schema;
using Hollow.Core.Util;

namespace Hollow.Core.Read.Engine.Set;

/// <summary>
/// Lifts the set records a delta removed into storage of their own.
/// </summary>
/// <remarks>
/// Named <c>HollowSetDeltaHistoricalStateCreator</c> in Java, and like it not meant for use outside
/// the history.
/// </remarks>
public sealed class HollowSetDeltaHistoricalStateCreator : HollowDeltaHistoricalStateCreator
{
    private readonly HollowSetTypeDataElements _historicalDataElements =
        new(WastefulRecycler.DefaultInstance);

    private readonly HollowSetSchema _schema;

    private ShardsHolder<HollowSetTypeReadState.Shard>? _shardsHolder;
    private long _nextStartBucket;

    /// <summary>
    /// Prepares to lift what the last transition removed from <paramref name="typeState"/>.
    /// </summary>
    /// <param name="typeState">The type to take the removed records from.</param>
    /// <param name="reverse">Takes the records the transition added instead.</param>
    public HollowSetDeltaHistoricalStateCreator(HollowSetTypeReadState typeState, bool reverse = false)
        : base(typeState, reverse)
    {
        _schema = typeState.Schema;
        _shardsHolder = (ShardsHolder<HollowSetTypeReadState.Shard>)typeState.ShardsVolatile;
    }

    /// <inheritdoc />
    public override void PopulateHistory()
    {
        PopulateStats();

        _historicalDataElements.SetPointerAndSizeData = new FixedLengthElementArray(
            _historicalDataElements.MemoryRecycler,
            ((long)_historicalDataElements.MaxOrdinal + 1)
                * _historicalDataElements.BitsPerFixedLengthSetPortion);

        _historicalDataElements.ElementData = new FixedLengthElementArray(
            _historicalDataElements.MemoryRecycler,
            _historicalDataElements.TotalNumberOfBuckets * _historicalDataElements.BitsPerElement);

        foreach (int ordinal in RemovedOrdinals)
        {
            OrdinalMapping.Put(ordinal, NextOrdinal);
            CopyRecord(ordinal);
        }
    }

    /// <inheritdoc />
    public override void DereferenceTypeState()
    {
        _shardsHolder = null;

        base.DereferenceTypeState();
    }

    /// <inheritdoc />
    public override HollowSetTypeReadState CreateHistoricalTypeReadState(HollowReadStateEngine into) =>
        new(into, _schema, _historicalDataElements);

    private void PopulateStats()
    {
        int removedEntryCount = 0;
        int maxSize = 0;
        long totalBucketCount = 0;

        foreach (int ordinal in RemovedOrdinals)
        {
            removedEntryCount++;

            (HollowSetTypeDataElements elements, int shardOrdinal) = ShardFor(ordinal);
            int size = elements.GetSize(shardOrdinal);

            maxSize = Math.Max(maxSize, size);

            // The space a record occupies is its hash table's, not its elements': the copy moves the
            // table across whole, so the space has to be counted the same way.
            totalBucketCount += HashCodes.HashTableSize(size);
        }

        _historicalDataElements.MaxOrdinal = removedEntryCount - 1;
        _historicalDataElements.BitsPerSetPointer = 64 - BitOperations.LeadingZeroCount((ulong)totalBucketCount);
        _historicalDataElements.BitsPerSetSizeValue = 64 - BitOperations.LeadingZeroCount((ulong)maxSize);
        _historicalDataElements.BitsPerFixedLengthSetPortion =
            _historicalDataElements.BitsPerSetPointer + _historicalDataElements.BitsPerSetSizeValue;
        _historicalDataElements.TotalNumberOfBuckets = totalBucketCount;

        // A record can come from any shard, so an element has to be as wide as the widest of them
        // encodes it — and the empty-bucket marker is whatever that width makes it.
        foreach (HollowSetTypeReadState.Shard shard in Shards)
        {
            if (shard.DataElements.BitsPerElement > _historicalDataElements.BitsPerElement)
            {
                _historicalDataElements.BitsPerElement = shard.DataElements.BitsPerElement;
                _historicalDataElements.EmptyBucketValue = shard.DataElements.EmptyBucketValue;
            }
        }

        OrdinalMapping = new IntMap(removedEntryCount);
    }

    private void CopyRecord(int ordinal)
    {
        (HollowSetTypeDataElements elements, int shardOrdinal) = ShardFor(ordinal);

        long bitsPerBucket = _historicalDataElements.BitsPerElement;
        long size = elements.GetSize(shardOrdinal);
        long fromStartBucket = elements.GetStartBucket(shardOrdinal);
        long numBuckets = elements.GetEndBucket(shardOrdinal) - fromStartBucket;

        _historicalDataElements.SetPointerAndSizeData!.SetElementValue(
            (long)NextOrdinal * _historicalDataElements.BitsPerFixedLengthSetPortion,
            _historicalDataElements.BitsPerSetPointer,
            _nextStartBucket + numBuckets);

        _historicalDataElements.SetPointerAndSizeData.SetElementValue(
            ((long)NextOrdinal * _historicalDataElements.BitsPerFixedLengthSetPortion)
                + _historicalDataElements.BitsPerSetPointer,
            _historicalDataElements.BitsPerSetSizeValue,
            size);

        _historicalDataElements.ElementData!.CopyBits(
            elements.ElementData!,
            fromStartBucket * bitsPerBucket,
            _nextStartBucket * bitsPerBucket,
            numBuckets * bitsPerBucket);

        NextOrdinal++;
        _nextStartBucket += numBuckets;
    }

    private ShardsHolder<HollowSetTypeReadState.Shard> ShardsHolder =>
        _shardsHolder ?? throw new InvalidOperationException(
            $"{nameof(DereferenceTypeState)} has already run, so there is nothing left to read.");

    private HollowSetTypeReadState.Shard[] Shards => ShardsHolder.TypedShards;

    private (HollowSetTypeDataElements Elements, int ShardOrdinal) ShardFor(int ordinal)
    {
        HollowSetTypeReadState.Shard shard = ShardsHolder.TypedShards[ordinal & ShardsHolder.ShardNumberMask];

        return (shard.DataElements, ordinal >> shard.ShardOrdinalShift);
    }
}
