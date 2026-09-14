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

namespace Hollow.Core.Read.Engine.Map;

/// <summary>
/// Lifts the map records a delta removed into storage of their own.
/// </summary>
/// <remarks>
/// Named <c>HollowMapDeltaHistoricalStateCreator</c> in Java, and like it not meant for use outside
/// the history.
/// </remarks>
public sealed class HollowMapDeltaHistoricalStateCreator : HollowDeltaHistoricalStateCreator
{
    private readonly HollowMapTypeDataElements _historicalDataElements =
        new(WastefulRecycler.DefaultInstance);

    private readonly HollowMapSchema _schema;

    private ShardsHolder<HollowMapTypeReadState.Shard>? _shardsHolder;
    private long _nextStartBucket;

    /// <summary>
    /// Prepares to lift what the last transition removed from <paramref name="typeState"/>.
    /// </summary>
    /// <param name="typeState">The type to take the removed records from.</param>
    /// <param name="reverse">Takes the records the transition added instead.</param>
    public HollowMapDeltaHistoricalStateCreator(HollowMapTypeReadState typeState, bool reverse = false)
        : base(typeState, reverse)
    {
        _schema = typeState.Schema;
        _shardsHolder = (ShardsHolder<HollowMapTypeReadState.Shard>)typeState.ShardsVolatile;
    }

    /// <inheritdoc />
    public override void PopulateHistory()
    {
        PopulateStats();

        _historicalDataElements.MapPointerAndSizeData = new FixedLengthElementArray(
            _historicalDataElements.MemoryRecycler,
            ((long)_historicalDataElements.MaxOrdinal + 1)
                * _historicalDataElements.BitsPerFixedLengthMapPortion);

        _historicalDataElements.EntryData = new FixedLengthElementArray(
            _historicalDataElements.MemoryRecycler,
            _historicalDataElements.TotalNumberOfBuckets * _historicalDataElements.BitsPerMapEntry);

        RemovedOrdinals.Reset();

        foreach (int ordinal in RemovedOrdinals.Enumerate())
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
    public override HollowMapTypeReadState CreateHistoricalTypeReadState(HollowReadStateEngine into) =>
        new(into, _schema, _historicalDataElements);

    private void PopulateStats()
    {
        RemovedOrdinals.Reset();

        int removedEntryCount = 0;
        int maxSize = 0;
        long totalBucketCount = 0;

        foreach (int ordinal in RemovedOrdinals.Enumerate())
        {
            removedEntryCount++;

            (HollowMapTypeDataElements elements, int shardOrdinal) = ShardFor(ordinal);
            int size = elements.GetSize(shardOrdinal);

            maxSize = Math.Max(maxSize, size);

            // The space a record occupies is its hash table's, not its entries': the copy moves the
            // table across whole, so the space has to be counted the same way.
            totalBucketCount += HashCodes.HashTableSize(size);
        }

        _historicalDataElements.MaxOrdinal = removedEntryCount - 1;
        _historicalDataElements.BitsPerMapPointer = 64 - BitOperations.LeadingZeroCount((ulong)totalBucketCount);
        _historicalDataElements.BitsPerMapSizeValue = 64 - BitOperations.LeadingZeroCount((ulong)maxSize);
        _historicalDataElements.BitsPerFixedLengthMapPortion =
            _historicalDataElements.BitsPerMapPointer + _historicalDataElements.BitsPerMapSizeValue;
        _historicalDataElements.TotalNumberOfBuckets = totalBucketCount;

        // A record can come from any shard, so key and value have to be as wide as the widest of them
        // encodes each — and the empty-bucket marker is whatever the key width makes it.
        foreach (HollowMapTypeReadState.Shard shard in Shards)
        {
            if (shard.DataElements.BitsPerKeyElement > _historicalDataElements.BitsPerKeyElement)
            {
                _historicalDataElements.BitsPerKeyElement = shard.DataElements.BitsPerKeyElement;
                _historicalDataElements.EmptyBucketKeyValue = shard.DataElements.EmptyBucketKeyValue;
            }

            _historicalDataElements.BitsPerValueElement = Math.Max(
                _historicalDataElements.BitsPerValueElement, shard.DataElements.BitsPerValueElement);

            _historicalDataElements.BitsPerMapEntry = Math.Max(
                _historicalDataElements.BitsPerMapEntry, shard.DataElements.BitsPerMapEntry);
        }

        OrdinalMapping = new IntMap(removedEntryCount);
    }

    private void CopyRecord(int ordinal)
    {
        (HollowMapTypeDataElements elements, int shardOrdinal) = ShardFor(ordinal);

        long bitsPerBucket = _historicalDataElements.BitsPerMapEntry;
        long size = elements.GetSize(shardOrdinal);
        long fromStartBucket = elements.GetStartBucket(shardOrdinal);
        long numBuckets = elements.GetEndBucket(shardOrdinal) - fromStartBucket;

        _historicalDataElements.MapPointerAndSizeData!.SetElementValue(
            (long)NextOrdinal * _historicalDataElements.BitsPerFixedLengthMapPortion,
            _historicalDataElements.BitsPerMapPointer,
            _nextStartBucket + numBuckets);

        _historicalDataElements.MapPointerAndSizeData.SetElementValue(
            ((long)NextOrdinal * _historicalDataElements.BitsPerFixedLengthMapPortion)
                + _historicalDataElements.BitsPerMapPointer,
            _historicalDataElements.BitsPerMapSizeValue,
            size);

        _historicalDataElements.EntryData!.CopyBits(
            elements.EntryData!,
            fromStartBucket * bitsPerBucket,
            _nextStartBucket * bitsPerBucket,
            numBuckets * bitsPerBucket);

        NextOrdinal++;
        _nextStartBucket += numBuckets;
    }

    private ShardsHolder<HollowMapTypeReadState.Shard> ShardsHolder =>
        _shardsHolder ?? throw new InvalidOperationException(
            $"{nameof(DereferenceTypeState)} has already run, so there is nothing left to read.");

    private HollowMapTypeReadState.Shard[] Shards => ShardsHolder.TypedShards;

    private (HollowMapTypeDataElements Elements, int ShardOrdinal) ShardFor(int ordinal)
    {
        HollowMapTypeReadState.Shard shard = ShardsHolder.TypedShards[ordinal & ShardsHolder.ShardNumberMask];

        return (shard.DataElements, ordinal >> shard.ShardOrdinalShift);
    }
}
