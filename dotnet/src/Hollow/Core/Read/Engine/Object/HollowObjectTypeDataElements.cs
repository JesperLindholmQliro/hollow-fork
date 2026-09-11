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
using Hollow.Core.Schema;

namespace Hollow.Core.Read.Engine.Object;

/// <summary>
/// The in-memory record storage of one shard of an object type: a fixed-width bit string holding every
/// record's fixed-length fields, plus one variable-length byte buffer per string or bytes field.
/// </summary>
public sealed partial class HollowObjectTypeDataElements
{
    private readonly IArraySegmentRecycler _memoryRecycler;

    private int[] _bitsPerUnfilteredField = [];
    private bool[] _unfilteredFieldIsIncluded = [];

    /// <summary>
    /// Initialises empty storage for <paramref name="schema"/>.
    /// </summary>
    public HollowObjectTypeDataElements(HollowObjectSchema schema, IArraySegmentRecycler memoryRecycler)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(memoryRecycler);

        Schema = schema;
        _memoryRecycler = memoryRecycler;

        VarLengthData = new IVariableLengthData[schema.FieldCount];
        BitsPerField = new int[schema.FieldCount];
        BitOffsetPerField = new int[schema.FieldCount];
        NullValueForField = new long[schema.FieldCount];
    }

    /// <summary>The schema of this shard's type, after any field filtering.</summary>
    public HollowObjectSchema Schema { get; }

    /// <summary>The highest ordinal this shard holds.</summary>
    public int MaxOrdinal { get; internal set; }

    /// <summary>The packed fixed-length fields of every record in this shard.</summary>
    public IFixedLengthData? FixedLengthData { get; internal set; }

    /// <summary>The variable-length payload of each field, or null for fields that have none.</summary>
    public IVariableLengthData?[] VarLengthData { get; }

    /// <summary>The width of each field, in bits.</summary>
    public int[] BitsPerField { get; }

    /// <summary>The bit offset of each field within a record.</summary>
    public int[] BitOffsetPerField { get; }

    /// <summary>The all-ones value that marks each field as null.</summary>
    public long[] NullValueForField { get; }

    /// <summary>The total width of one record, in bits.</summary>
    public int BitsPerRecord { get; internal set; }

    /// <summary>
    /// Reads one shard's records from <paramref name="input"/>.
    /// </summary>
    /// <param name="input">The blob to read from.</param>
    /// <param name="unfilteredSchema">
    /// The schema as written in the blob, which may contain fields <see cref="Schema"/> excludes.
    /// </param>
    public void ReadSnapshot(HollowBlobInput input, HollowObjectSchema unfilteredSchema)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(unfilteredSchema);

        MaxOrdinal = VarInt.ReadVInt(input);

        ReadFieldStatistics(input, unfilteredSchema);

        FixedLengthData = FixedLengthElementArray.NewFrom(input, _memoryRecycler);
        RemoveExcludedFieldsFromFixedLengthData();

        ReadVarLengthData(input, unfilteredSchema);
    }

    /// <summary>
    /// Returns every segment of this shard's storage to the recycler.
    /// </summary>
    public void Destroy()
    {
        if (FixedLengthData is FixedLengthElementArray fixedLength)
        {
            fixedLength.Destroy(_memoryRecycler);
        }

        for (int i = 0; i < VarLengthData.Length; i++)
        {
            if (VarLengthData[i] is SegmentedByteArray segmented)
            {
                segmented.Destroy();
            }

            VarLengthData[i] = null;
        }

        FixedLengthData = null;
    }

    /// <summary>
    /// Skips a type's records without materialising them, for a type excluded by the filter.
    /// </summary>
    public static void DiscardFromInput(HollowBlobInput input, HollowObjectSchema schema, int numShards)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(schema);

        if (numShards > 1)
        {
            VarInt.ReadVInt(input); // The overall max ordinal.
        }

        for (int i = 0; i < numShards; i++)
        {
            VarInt.ReadVInt(input); // The shard's max ordinal.

            for (int j = 0; j < schema.FieldCount; j++)
            {
                VarInt.ReadVInt(input); // The field's width.
            }

            IFixedLengthData.DiscardFrom(input);

            for (int j = 0; j < schema.FieldCount; j++)
            {
                SkipBytes(input, VarInt.ReadVLong(input));
            }
        }
    }

    private static void SkipBytes(HollowBlobInput input, long count)
    {
        while (count > 0)
        {
            long skipped = input.SkipBytes(count);
            if (skipped <= 0)
            {
                throw new EndOfStreamException("unexpected end of variable-length data");
            }

            count -= skipped;
        }
    }

    private void ReadFieldStatistics(HollowBlobInput input, HollowObjectSchema unfilteredSchema)
    {
        BitsPerRecord = 0;

        _bitsPerUnfilteredField = new int[unfilteredSchema.FieldCount];
        _unfilteredFieldIsIncluded = new bool[unfilteredSchema.FieldCount];

        int filteredFieldIndex = 0;

        for (int i = 0; i < unfilteredSchema.FieldCount; i++)
        {
            int readBitsPerField = VarInt.ReadVInt(input);
            _bitsPerUnfilteredField[i] = readBitsPerField;
            _unfilteredFieldIsIncluded[i] = Schema.GetPosition(unfilteredSchema.GetFieldName(i)) != -1;

            if (_unfilteredFieldIsIncluded[i])
            {
                BitsPerField[filteredFieldIndex] = readBitsPerField;
                NullValueForField[filteredFieldIndex] =
                    readBitsPerField >= 64 ? -1L : (1L << readBitsPerField) - 1;
                BitOffsetPerField[filteredFieldIndex] = BitsPerRecord;
                BitsPerRecord += readBitsPerField;
                filteredFieldIndex++;
            }
        }
    }

    /// <summary>
    /// Rewrites the bit string with the excluded fields dropped, so that reads can use the filtered
    /// record layout directly.
    /// </summary>
    private void RemoveExcludedFieldsFromFixedLengthData()
    {
        if (BitsPerField.Length >= _bitsPerUnfilteredField.Length)
        {
            return;
        }

        long numBitsRequired = (long)BitsPerRecord * (MaxOrdinal + 1);
        FixedLengthElementArray filteredData = new(_memoryRecycler, numBitsRequired);

        long currentReadBit = 0;
        long currentWriteBit = 0;

        for (int i = 0; i <= MaxOrdinal; i++)
        {
            for (int j = 0; j < _bitsPerUnfilteredField.Length; j++)
            {
                if (_unfilteredFieldIsIncluded[j])
                {
                    (long low, long high) =
                        FixedLengthData!.GetWideElementValue(currentReadBit, _bitsPerUnfilteredField[j]);
                    filteredData.SetWideElementValue(
                        currentWriteBit, _bitsPerUnfilteredField[j], low, high);
                    currentWriteBit += _bitsPerUnfilteredField[j];
                }

                currentReadBit += _bitsPerUnfilteredField[j];
            }
        }

        if (FixedLengthData is FixedLengthElementArray previous)
        {
            previous.Destroy(_memoryRecycler);
        }

        _memoryRecycler.Swap();
        FixedLengthData = filteredData;
    }

    private void ReadVarLengthData(HollowBlobInput input, HollowObjectSchema unfilteredSchema)
    {
        int filteredFieldIndex = 0;

        for (int i = 0; i < unfilteredSchema.FieldCount; i++)
        {
            long numBytesInVarLengthData = VarInt.ReadVLong(input);

            if (Schema.GetPosition(unfilteredSchema.GetFieldName(i)) != -1)
            {
                if (numBytesInVarLengthData != 0)
                {
                    SegmentedByteArray data = new(_memoryRecycler);
                    data.LoadFrom(input, numBytesInVarLengthData);
                    VarLengthData[filteredFieldIndex] = data;
                }

                filteredFieldIndex++;
            }
            else
            {
                SkipBytes(input, numBytesInVarLengthData);
            }
        }
    }
}
