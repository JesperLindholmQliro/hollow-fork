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

namespace Hollow.Core.Read.Engine.List;

/// <summary>
/// Lifts the list records a delta removed into storage of their own.
/// </summary>
/// <remarks>
/// Named <c>HollowListDeltaHistoricalStateCreator</c> in Java, and like it not meant for use outside
/// the history.
/// </remarks>
public sealed class HollowListDeltaHistoricalStateCreator : HollowDeltaHistoricalStateCreator
{
    private readonly HollowListTypeDataElements _historicalDataElements =
        new(WastefulRecycler.DefaultInstance);

    private readonly HollowListSchema _schema;

    private ShardsHolder<HollowListTypeReadState.Shard>? _shardsHolder;
    private long _nextStartElement;

    /// <summary>
    /// Prepares to lift what the last transition removed from <paramref name="typeState"/>.
    /// </summary>
    /// <param name="typeState">The type to take the removed records from.</param>
    /// <param name="reverse">Takes the records the transition added instead.</param>
    public HollowListDeltaHistoricalStateCreator(HollowListTypeReadState typeState, bool reverse = false)
        : base(typeState, reverse)
    {
        _schema = typeState.Schema;
        _shardsHolder = (ShardsHolder<HollowListTypeReadState.Shard>)typeState.ShardsVolatile;
    }

    /// <inheritdoc />
    public override void PopulateHistory()
    {
        PopulateStats();

        _historicalDataElements.ListPointerData = new FixedLengthElementArray(
            _historicalDataElements.MemoryRecycler,
            ((long)_historicalDataElements.MaxOrdinal + 1) * _historicalDataElements.BitsPerListPointer);

        _historicalDataElements.ElementData = new FixedLengthElementArray(
            _historicalDataElements.MemoryRecycler,
            _historicalDataElements.TotalNumberOfElements * _historicalDataElements.BitsPerElement);

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
    public override HollowListTypeReadState CreateHistoricalTypeReadState(HollowReadStateEngine into) =>
        new(into, _schema, _historicalDataElements);

    private void PopulateStats()
    {
        RemovedOrdinals.Reset();

        int removedEntryCount = 0;
        long totalElementCount = 0;

        foreach (int ordinal in RemovedOrdinals.Enumerate())
        {
            removedEntryCount++;

            (HollowListTypeDataElements elements, int shardOrdinal) = ShardFor(ordinal);
            totalElementCount += elements.GetEndElement(shardOrdinal) - elements.GetStartElement(shardOrdinal);
        }

        _historicalDataElements.MaxOrdinal = removedEntryCount - 1;
        _historicalDataElements.TotalNumberOfElements = totalElementCount;
        _historicalDataElements.BitsPerListPointer = totalElementCount == 0
            ? 1
            : 64 - BitOperations.LeadingZeroCount((ulong)totalElementCount);

        // A record can come from any shard, so an element has to be as wide as the widest of them
        // encodes it.
        _historicalDataElements.BitsPerElement = Shards
            .Select(shard => shard.DataElements.BitsPerElement)
            .DefaultIfEmpty(0)
            .Max();

        OrdinalMapping = new IntMap(removedEntryCount);
    }

    private void CopyRecord(int ordinal)
    {
        (HollowListTypeDataElements elements, int shardOrdinal) = ShardFor(ordinal);

        long bitsPerElement = elements.BitsPerElement;
        long fromStartElement = elements.GetStartElement(shardOrdinal);
        long size = elements.GetEndElement(shardOrdinal) - fromStartElement;

        _historicalDataElements.ElementData!.CopyBits(
            elements.ElementData!,
            fromStartElement * bitsPerElement,
            _nextStartElement * bitsPerElement,
            size * bitsPerElement);

        _historicalDataElements.ListPointerData!.SetElementValue(
            (long)NextOrdinal * _historicalDataElements.BitsPerListPointer,
            _historicalDataElements.BitsPerListPointer,
            _nextStartElement + size);

        NextOrdinal++;
        _nextStartElement += size;
    }

    private ShardsHolder<HollowListTypeReadState.Shard> ShardsHolder =>
        _shardsHolder ?? throw new InvalidOperationException(
            $"{nameof(DereferenceTypeState)} has already run, so there is nothing left to read.");

    private HollowListTypeReadState.Shard[] Shards => ShardsHolder.TypedShards;

    private (HollowListTypeDataElements Elements, int ShardOrdinal) ShardFor(int ordinal)
    {
        HollowListTypeReadState.Shard shard = ShardsHolder.TypedShards[ordinal & ShardsHolder.ShardNumberMask];

        return (shard.DataElements, ordinal >> shard.ShardOrdinalShift);
    }
}
