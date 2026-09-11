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

using Hollow.Core.Schema;

namespace Hollow.Core.Read.Engine.Object;

/// <summary>
/// Divides one shard of an object type into several.
/// </summary>
public sealed class HollowObjectTypeDataElementsSplitter(HollowObjectTypeDataElements from, int numSplits)
    : HollowTypeDataElementsSplitter<HollowObjectTypeDataElements>(from, numSplits)
{
    /// <inheritdoc />
    protected override HollowObjectTypeDataElements[] CreateSplits() =>
        [
            .. Enumerable.Range(0, NumSplits)
                .Select(_ => new HollowObjectTypeDataElements(From.Schema, From.MemoryRecycler))
        ];

    /// <inheritdoc />
    protected override void PopulateStats()
    {
        HollowObjectSchema schema = From.Schema;

        long[][] varLengthSizes = [.. Enumerable.Range(0, NumSplits).Select(_ => new long[schema.FieldCount])];

        for (int ordinal = 0; ordinal <= From.MaxOrdinal; ordinal++)
        {
            int toIndex = ordinal & ToMask;
            To[toIndex].MaxOrdinal = ordinal >> ToOrdinalShift;

            for (int fieldIndex = 0; fieldIndex < schema.FieldCount; fieldIndex++)
            {
                if (From.VarLengthData[fieldIndex] is not null)
                {
                    varLengthSizes[toIndex][fieldIndex] += From.VarLengthSize(ordinal, fieldIndex);
                }
            }
        }

        for (int toIndex = 0; toIndex < To.Length; toIndex++)
        {
            To[toIndex].AllocateForSizes(varLengthSizes[toIndex], From.BitsPerField);
        }
    }

    /// <inheritdoc />
    protected override void CopyRecords()
    {
        long[][] varLengthWritePointers =
            [.. Enumerable.Range(0, NumSplits).Select(_ => new long[From.Schema.FieldCount])];

        for (int ordinal = 0; ordinal <= From.MaxOrdinal; ordinal++)
        {
            int toIndex = ordinal & ToMask;

            To[toIndex].CopyRecord(ordinal >> ToOrdinalShift, From, ordinal, varLengthWritePointers[toIndex]);
        }
    }
}

/// <summary>
/// Merges several shards of an object type into one.
/// </summary>
public sealed class HollowObjectTypeDataElementsJoiner(HollowObjectTypeDataElements[] from)
    : HollowTypeDataElementsJoiner<HollowObjectTypeDataElements>(from)
{
    /// <inheritdoc />
    protected override HollowObjectTypeDataElements CreateJoined() =>
        new(From[0].Schema, From[0].MemoryRecycler);

    /// <inheritdoc />
    protected override void PopulateStats()
    {
        HollowObjectSchema schema = To.Schema;

        long[] varLengthSizes = new long[schema.FieldCount];

        // A fixed-length field's width need not agree across the sources — each was sized for its own
        // records — so the join takes the widest.
        int[] fixedFieldWidths = new int[schema.FieldCount];

        foreach (HollowObjectTypeDataElements source in From)
        {
            for (int ordinal = 0; ordinal <= source.MaxOrdinal; ordinal++)
            {
                for (int fieldIndex = 0; fieldIndex < schema.FieldCount; fieldIndex++)
                {
                    if (source.VarLengthData[fieldIndex] is not null)
                    {
                        varLengthSizes[fieldIndex] += source.VarLengthSize(ordinal, fieldIndex);
                    }
                }
            }

            for (int fieldIndex = 0; fieldIndex < schema.FieldCount; fieldIndex++)
            {
                fixedFieldWidths[fieldIndex] =
                    Math.Max(fixedFieldWidths[fieldIndex], source.BitsPerField[fieldIndex]);
            }
        }

        To.MaxOrdinal = JoinedMaxOrdinal();
        To.AllocateForSizes(varLengthSizes, fixedFieldWidths);
    }

    /// <inheritdoc />
    protected override void CopyRecords()
    {
        long[] varLengthWritePointers = new long[To.Schema.FieldCount];

        for (int ordinal = 0; ordinal <= To.MaxOrdinal; ordinal++)
        {
            HollowObjectTypeDataElements source = From[ordinal & FromMask];
            int fromOrdinal = ordinal >> FromOrdinalShift;

            if (fromOrdinal <= source.MaxOrdinal)
            {
                To.CopyRecord(ordinal, source, fromOrdinal, varLengthWritePointers);
            }
            else
            {
                // Lopsided sources: one shard reached a higher ordinal than another, so the joined
                // shard has slots in between that nothing came from.
                To.WriteNullRecord(ordinal, varLengthWritePointers);
            }
        }
    }
}
