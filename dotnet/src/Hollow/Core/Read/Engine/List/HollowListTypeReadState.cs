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
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Iterator;
using Hollow.Core.Schema;

namespace Hollow.Core.Read.Engine.List;

/// <summary>
/// The in-memory record storage of one shard of a list type: every list's elements concatenated into
/// one bit-packed array, with a per-record pointer to the end of its own run.
/// </summary>
public sealed partial class HollowListTypeDataElements
{
    private readonly IArraySegmentRecycler _memoryRecycler;

    /// <summary>
    /// Initialises empty storage.
    /// </summary>
    public HollowListTypeDataElements(IArraySegmentRecycler memoryRecycler)
    {
        ArgumentNullException.ThrowIfNull(memoryRecycler);
        _memoryRecycler = memoryRecycler;
    }

    /// <summary>The highest ordinal this shard holds.</summary>
    public int MaxOrdinal { get; internal set; }

    /// <summary>The end-of-run pointer of each record.</summary>
    public IFixedLengthData? ListPointerData { get; internal set; }

    /// <summary>Every list's element ordinals, back to back.</summary>
    public IFixedLengthData? ElementData { get; internal set; }

    /// <summary>The width of a pointer into <see cref="ElementData"/>, in bits.</summary>
    public int BitsPerListPointer { get; internal set; }

    /// <summary>The width of an element ordinal, in bits.</summary>
    public int BitsPerElement { get; internal set; }

    /// <summary>The total number of elements across every list in this shard.</summary>
    public long TotalNumberOfElements { get; internal set; }

    /// <summary>
    /// Reads one shard's records from <paramref name="input"/>.
    /// </summary>
    public void ReadSnapshot(HollowBlobInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        MaxOrdinal = VarInt.ReadVInt(input);
        BitsPerListPointer = VarInt.ReadVInt(input);
        BitsPerElement = VarInt.ReadVInt(input);
        TotalNumberOfElements = VarInt.ReadVLong(input);

        ListPointerData = FixedLengthElementArray.NewFrom(input, _memoryRecycler);
        ElementData = FixedLengthElementArray.NewFrom(input, _memoryRecycler);
    }

    /// <summary>The index of the first element of <paramref name="ordinal"/>'s list.</summary>
    public long GetStartElement(int ordinal) =>
        ordinal == 0
            ? 0
            : ListPointerData!.GetLargeElementValue((long)(ordinal - 1) * BitsPerListPointer, BitsPerListPointer);

    /// <summary>The index one past the last element of <paramref name="ordinal"/>'s list.</summary>
    public long GetEndElement(int ordinal) =>
        ListPointerData!.GetLargeElementValue((long)ordinal * BitsPerListPointer, BitsPerListPointer);

    /// <summary>The ordinal stored at <paramref name="elementIndex"/>.</summary>
    public int GetElementValue(long elementIndex) =>
        (int)ElementData!.GetLargeElementValue(elementIndex * BitsPerElement, BitsPerElement);

    /// <summary>Returns every segment of this shard's storage to the recycler.</summary>
    public void Destroy()
    {
        (ListPointerData as FixedLengthElementArray)?.Destroy(_memoryRecycler);
        (ElementData as FixedLengthElementArray)?.Destroy(_memoryRecycler);
        ListPointerData = null;
        ElementData = null;
    }

    /// <summary>
    /// Skips a type's records without materialising them.
    /// </summary>
    public static void DiscardFromInput(HollowBlobInput input, int numShards)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (numShards > 1)
        {
            VarInt.ReadVInt(input);
        }

        for (int i = 0; i < numShards; i++)
        {
            VarInt.ReadVInt(input); // The shard's max ordinal.
            VarInt.ReadVInt(input); // bitsPerListPointer
            VarInt.ReadVInt(input); // bitsPerElement
            VarInt.ReadVLong(input); // totalNumberOfElements

            IFixedLengthData.DiscardFrom(input);
            IFixedLengthData.DiscardFrom(input);
        }
    }
}

/// <summary>
/// Holds the records of a list type.
/// </summary>
public sealed partial class HollowListTypeReadState : HollowTypeReadState, IHollowListTypeDataAccess
{
    private Shard[] _shards = [];
    private int _shardNumberMask;
    private int _maxOrdinal = -1;

    /// <summary>
    /// Initialises a read state for <paramref name="schema"/>.
    /// </summary>
    public HollowListTypeReadState(
        HollowReadStateEngine stateEngine, MemoryMode memoryMode, HollowListSchema schema)
        : base(stateEngine, memoryMode, schema)
    {
    }

    /// <summary>The schema of this type.</summary>
    public new HollowListSchema Schema => (HollowListSchema)base.Schema;

    /// <inheritdoc />
    HollowCollectionSchema IHollowCollectionTypeDataAccess.Schema => Schema;

    /// <inheritdoc />
    public override int MaxOrdinal => _maxOrdinal;

    /// <inheritdoc />
    public override long ApproxHeapFootprintInBytes
    {
        get
        {
            long bits = 0;
            foreach (Shard shard in _shards)
            {
                bits += ((long)shard.DataElements.MaxOrdinal + 1) * shard.DataElements.BitsPerListPointer;
                bits += shard.DataElements.TotalNumberOfElements * shard.DataElements.BitsPerElement;
            }

            return bits / 8;
        }
    }

    /// <inheritdoc />
    public override void ReadSnapshot(HollowBlobInput input, IArraySegmentRecycler memoryRecycler, int numShards)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(memoryRecycler);

        if (numShards > 1)
        {
            _maxOrdinal = VarInt.ReadVInt(input);
        }

        int shardOrdinalShift = BitOperations.TrailingZeroCount((uint)numShards);

        Shard[] shards = new Shard[numShards];
        for (int i = 0; i < numShards; i++)
        {
            HollowListTypeDataElements dataElements = new(memoryRecycler);
            dataElements.ReadSnapshot(input);
            shards[i] = new Shard(dataElements, shardOrdinalShift);
        }

        _shards = shards;
        _shardNumberMask = numShards - 1;

        if (numShards == 1)
        {
            _maxOrdinal = shards[0].DataElements.MaxOrdinal;
        }

        SnapshotPopulatedOrdinalsReader.ReadOrdinals(input, Listeners);
    }

    /// <inheritdoc />
    public override void Destroy(IArraySegmentRecycler memoryRecycler)
    {
        foreach (Shard shard in _shards)
        {
            shard.DataElements.Destroy();
        }

        _shards = [];
    }

    /// <inheritdoc />
    public int Size(int ordinal)
    {
        Shard shard = _shards[ordinal & _shardNumberMask];
        int shardOrdinal = ordinal >> shard.ShardOrdinalShift;

        return (int)(shard.DataElements.GetEndElement(shardOrdinal) - shard.DataElements.GetStartElement(shardOrdinal));
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="listIndex"/> is past the end.</exception>
    public int GetElementOrdinal(int ordinal, int listIndex)
    {
        Shard shard = _shards[ordinal & _shardNumberMask];
        int shardOrdinal = ordinal >> shard.ShardOrdinalShift;

        long startElement = shard.DataElements.GetStartElement(shardOrdinal);
        long endElement = shard.DataElements.GetEndElement(shardOrdinal);
        long elementIndex = startElement + listIndex;

        if (elementIndex >= endElement)
        {
            throw new ArgumentOutOfRangeException(
                nameof(listIndex), listIndex, $"list size is {endElement - startElement}");
        }

        return shard.DataElements.GetElementValue(elementIndex);
    }

    /// <inheritdoc />
    public IHollowOrdinalIterator OrdinalIterator(int ordinal) => new HollowListOrdinalIterator(ordinal, this);

    private sealed class Shard(HollowListTypeDataElements dataElements, int shardOrdinalShift)
    {
        internal HollowListTypeDataElements DataElements { get; } = dataElements;

        internal int ShardOrdinalShift { get; } = shardOrdinalShift;
    }
}
