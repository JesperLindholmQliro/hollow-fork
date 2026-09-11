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

namespace Hollow.Core.Read.Engine.Set;

public sealed partial class HollowSetTypeReadState
{
    /// <inheritdoc />
    /// <exception cref="ArgumentException">
    /// <paramref name="withSchema"/> differs from this type's schema.
    /// </exception>
    /// <remarks>
    /// The bucket index goes into the checksum alongside the element, so two sets holding the same
    /// elements in a different hash layout do not match. That is deliberate: the layout is part of what
    /// a consumer reads back.
    /// </remarks>
    protected override void ApplyToChecksum(HollowChecksum checksum, HollowSchema withSchema)
    {
        ArgumentNullException.ThrowIfNull(checksum);

        if (!Schema.Equals(withSchema))
        {
            throw new ArgumentException(
                $"A set type ({Schema.Name}) can only be checksummed against an equal schema.",
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

                long startBucket = shard.DataElements.GetStartBucket(shardOrdinal);
                long endBucket = shard.DataElements.GetEndBucket(shardOrdinal);

                for (long bucket = startBucket; bucket < endBucket; bucket++)
                {
                    int bucketValue = shard.DataElements.GetBucketValue(bucket);

                    if (bucketValue != shard.DataElements.EmptyBucketValue)
                    {
                        checksum.ApplyInt((int)(bucket - startBucket));
                        checksum.ApplyInt(bucketValue);
                    }
                }
            }
        }
    }
}
