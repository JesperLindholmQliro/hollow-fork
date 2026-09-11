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

using Hollow.Core.Memory.Encoding;
using Hollow.Core.Schema;
using Hollow.Core.Tools.Checksum;
using Hollow.Core.Util;

namespace Hollow.Core.Read.Engine.Object;

public sealed partial class HollowObjectTypeReadState
{
    /// <inheritdoc />
    /// <exception cref="ArgumentException">
    /// <paramref name="withSchema"/> is not an object schema.
    /// </exception>
    protected override void ApplyToChecksum(HollowChecksum checksum, HollowSchema withSchema)
    {
        ArgumentNullException.ThrowIfNull(checksum);

        if (withSchema is not HollowObjectSchema otherSchema)
        {
            throw new ArgumentException(
                $"An object type ({Schema.Name}) can only be checksummed against an object schema.",
                nameof(withSchema));
        }

        // Only the fields both schemas declare, in name order so that the two sides walk them the same
        // way even when they were declared in a different order.
        HollowObjectSchema commonSchema = Schema.FindCommonSchema(otherSchema);

        int[] fieldIndexes =
        [
            .. Enumerable.Range(0, commonSchema.FieldCount)
                .Select(commonSchema.GetFieldName)
                .Order(StringComparer.Ordinal)
                .Select(Schema.GetPosition)
        ];

        BitSet populatedOrdinals = PopulatedOrdinals;

        ShardsHolder<Shard> holder = _shardsVolatile;

        for (int shardNumber = 0; shardNumber < holder.TypedShards.Length; shardNumber++)
        {
            Shard shard = holder.TypedShards[shardNumber];

            for (int ordinal = populatedOrdinals.NextSetBit(0);
                ordinal != HollowConstants.OrdinalNone;
                ordinal = populatedOrdinals.NextSetBit(ordinal + 1))
            {
                if ((ordinal & holder.ShardNumberMask) != shardNumber)
                {
                    continue;
                }

                int shardOrdinal = ordinal >> shard.ShardOrdinalShift;

                checksum.ApplyInt(ordinal);

                foreach (int fieldIndex in fieldIndexes)
                {
                    ApplyFieldToChecksum(checksum, shard, shardOrdinal, fieldIndex);
                }
            }
        }
    }

    private void ApplyFieldToChecksum(HollowChecksum checksum, Shard shard, int shardOrdinal, int fieldIndex)
    {
        if (Schema.GetFieldType(fieldIndex).IsVariableLength())
        {
            (long startByte, long endByte, int numBitsForField) = shard.VarLengthRange(shardOrdinal, fieldIndex);
            checksum.ApplyInt(VarLengthFieldHashCode(shard, startByte, endByte, numBitsForField, fieldIndex));

            return;
        }

        // A decimal is the one fixed-length field wider than 64 bits, so it contributes both halves.
        // See "Format extension: the Decimal field type" in PORTING.md.
        if (Schema.GetFieldType(fieldIndex) == FieldType.Decimal)
        {
            (long low, long high) = shard.ReadWideValue(shardOrdinal, fieldIndex);

            if (DecimalBits.IsNull(low, high))
            {
                checksum.ApplyInt(int.MaxValue);
            }
            else
            {
                checksum.ApplyLong(low);
                checksum.ApplyLong(high);
            }

            return;
        }

        long value = shard.ReadValue(shardOrdinal, fieldIndex);

        if (value == shard.DataElements.NullValueForField[fieldIndex])
        {
            checksum.ApplyInt(int.MaxValue);
        }
        else
        {
            checksum.ApplyLong(value);
        }
    }

    /// <summary>
    /// The hash of a variable-length field's bytes, or -1 when the field is null — which the high bit
    /// of the stored end offset marks.
    /// </summary>
    private static int VarLengthFieldHashCode(
        Shard shard, long startByte, long endByte, int numBitsForField, int fieldIndex)
    {
        if ((endByte & (1L << (numBitsForField - 1))) != 0)
        {
            return -1;
        }

        startByte &= (1L << (numBitsForField - 1)) - 1;

        return HashCodes.Compute(
            shard.DataElements.VarLengthData[fieldIndex]!, startByte, (int)(endByte - startByte));
    }
}
