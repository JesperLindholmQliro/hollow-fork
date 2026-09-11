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
using Hollow.Core.Tools.Checksum;
using Hollow.Core.Util;

namespace Hollow.Core.Read.Engine.List;

public sealed partial class HollowListTypeReadState
{
    /// <inheritdoc />
    /// <exception cref="ArgumentException">
    /// <paramref name="withSchema"/> differs from this type's schema. A collection schema has nothing
    /// to intersect, so unlike an object type it has to match exactly.
    /// </exception>
    protected override void ApplyToChecksum(HollowChecksum checksum, HollowSchema withSchema)
    {
        ArgumentNullException.ThrowIfNull(checksum);

        if (!Schema.Equals(withSchema))
        {
            throw new ArgumentException(
                $"A list type ({Schema.Name}) can only be checksummed against an equal schema.",
                nameof(withSchema));
        }

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

                long startElement = shard.DataElements.GetStartElement(shardOrdinal);
                long endElement = shard.DataElements.GetEndElement(shardOrdinal);

                for (long element = startElement; element < endElement; element++)
                {
                    checksum.ApplyInt(shard.DataElements.GetElementValue(element));
                }
            }
        }
    }
}
