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
using Hollow.Core.Util;

namespace Hollow.Core.Read.Engine.Object;

/// <summary>
/// Lifts the object records a delta removed into storage of their own.
/// </summary>
/// <remarks>
/// Named <c>HollowObjectDeltaHistoricalStateCreator</c> in Java, and like it not meant for use outside
/// the history.
/// </remarks>
public sealed class HollowObjectDeltaHistoricalStateCreator : HollowDeltaHistoricalStateCreator
{
    private readonly HollowObjectTypeDataElements _historicalDataElements;
    private readonly HollowObjectSchema _schema;
    private readonly long[] _currentWriteVarLengthDataPointers;

    private ShardsHolder<HollowObjectTypeReadState.Shard>? _shardsHolder;

    /// <summary>
    /// Prepares to lift what the last transition removed from <paramref name="typeState"/>.
    /// </summary>
    /// <param name="typeState">The type to take the removed records from.</param>
    /// <param name="reverse">Takes the records the transition added instead.</param>
    public HollowObjectDeltaHistoricalStateCreator(HollowObjectTypeReadState typeState, bool reverse = false)
        : base(typeState, reverse)
    {
        _schema = typeState.Schema;
        _historicalDataElements = new HollowObjectTypeDataElements(_schema, WastefulRecycler.DefaultInstance);
        _currentWriteVarLengthDataPointers = new long[_schema.FieldCount];
        _shardsHolder = (ShardsHolder<HollowObjectTypeReadState.Shard>)typeState.ShardsVolatile;
    }

    /// <inheritdoc />
    public override void PopulateHistory()
    {
        PopulateStats();

        _historicalDataElements.FixedLengthData = new FixedLengthElementArray(
            _historicalDataElements.MemoryRecycler,
            (long)_historicalDataElements.BitsPerRecord * (_historicalDataElements.MaxOrdinal + 1));

        for (int fieldIndex = 0; fieldIndex < _schema.FieldCount; fieldIndex++)
        {
            if (_schema.GetFieldType(fieldIndex).IsVariableLength())
            {
                _historicalDataElements.VarLengthData[fieldIndex] =
                    new SegmentedByteArray(_historicalDataElements.MemoryRecycler);
            }
        }

        foreach (int ordinal in RemovedOrdinals)
        {
            OrdinalMapping.Put(ordinal, NextOrdinal);

            HollowObjectTypeReadState.Shard shard = ShardFor(ordinal);

            _historicalDataElements.CopyRecord(
                NextOrdinal,
                shard.DataElements,
                ordinal >> shard.ShardOrdinalShift,
                _currentWriteVarLengthDataPointers);

            NextOrdinal++;
        }
    }

    /// <inheritdoc />
    public override void DereferenceTypeState()
    {
        _shardsHolder = null;

        base.DereferenceTypeState();
    }

    /// <inheritdoc />
    public override HollowObjectTypeReadState CreateHistoricalTypeReadState(HollowReadStateEngine into) =>
        new(into, _schema, _historicalDataElements);

    private void PopulateStats()
    {
        int removedEntryCount = 0;
        long[] totalVarLengthSizes = new long[_schema.FieldCount];

        foreach (int ordinal in RemovedOrdinals)
        {
            removedEntryCount++;

            for (int fieldIndex = 0; fieldIndex < _schema.FieldCount; fieldIndex++)
            {
                if (!_schema.GetFieldType(fieldIndex).IsVariableLength())
                {
                    continue;
                }

                HollowObjectTypeReadState.Shard shard = ShardFor(ordinal);

                totalVarLengthSizes[fieldIndex] +=
                    shard.DataElements.VarLengthSize(ordinal >> shard.ShardOrdinalShift, fieldIndex);
            }
        }

        _historicalDataElements.MaxOrdinal = removedEntryCount - 1;

        for (int fieldIndex = 0; fieldIndex < _schema.FieldCount; fieldIndex++)
        {
            if (_schema.GetFieldType(fieldIndex).IsVariableLength())
            {
                // Wide enough for a pointer just past the last byte, plus the bit that marks null.
                _historicalDataElements.BitsPerField[fieldIndex] =
                    (64 - BitOperations.LeadingZeroCount((ulong)(totalVarLengthSizes[fieldIndex] + 1))) + 1;
            }
            else
            {
                // A record can come from any shard, so the field has to be as wide as the widest of
                // them encodes it.
                _historicalDataElements.BitsPerField[fieldIndex] = Shards
                    .Select(shard => shard.DataElements.BitsPerField[fieldIndex])
                    .DefaultIfEmpty(0)
                    .Max();
            }

            _historicalDataElements.NullValueForField[fieldIndex] =
                _historicalDataElements.BitsPerField[fieldIndex] == 64
                    ? -1L
                    : (1L << _historicalDataElements.BitsPerField[fieldIndex]) - 1;

            _historicalDataElements.BitOffsetPerField[fieldIndex] = _historicalDataElements.BitsPerRecord;
            _historicalDataElements.BitsPerRecord += _historicalDataElements.BitsPerField[fieldIndex];
        }

        OrdinalMapping = new IntMap(removedEntryCount);
    }

    private ShardsHolder<HollowObjectTypeReadState.Shard> ShardsHolder =>
        _shardsHolder ?? throw new InvalidOperationException(
            $"{nameof(DereferenceTypeState)} has already run, so there is nothing left to read.");

    private HollowObjectTypeReadState.Shard[] Shards => ShardsHolder.TypedShards;

    private HollowObjectTypeReadState.Shard ShardFor(int ordinal) =>
        ShardsHolder.TypedShards[ordinal & ShardsHolder.ShardNumberMask];
}
