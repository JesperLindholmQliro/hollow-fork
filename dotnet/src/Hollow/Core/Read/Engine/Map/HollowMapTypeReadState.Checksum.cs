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

namespace Hollow.Core.Read.Engine.Map;

public sealed partial class HollowMapTypeReadState
{
    /// <inheritdoc />
    /// <exception cref="ArgumentException">
    /// <paramref name="withSchema"/> differs from this type's schema.
    /// </exception>
    /// <remarks>See <see cref="Set.HollowSetTypeReadState"/> for why the bucket index is included.</remarks>
    protected override void ApplyToChecksum(HollowChecksum checksum, HollowSchema withSchema)
    {
        ArgumentNullException.ThrowIfNull(checksum);

        if (!Schema.Equals(withSchema))
        {
            throw new ArgumentException(
                $"A map type ({Schema.Name}) can only be checksummed against an equal schema.",
                nameof(withSchema));
        }

        BitSet populatedOrdinals = PopulatedOrdinals;

        for (int shardNumber = 0; shardNumber < _shards.Length; shardNumber++)
        {
            Shard shard = _shards[shardNumber];

            for (int ordinal = populatedOrdinals.NextSetBit(0);
                ordinal != HollowConstants.OrdinalNone;
                ordinal = populatedOrdinals.NextSetBit(ordinal + 1))
            {
                if ((ordinal & _shardNumberMask) != shardNumber)
                {
                    continue;
                }

                int shardOrdinal = ordinal >> shard.ShardOrdinalShift;

                checksum.ApplyInt(ordinal);

                long startBucket = shard.DataElements.GetStartBucket(shardOrdinal);
                long endBucket = shard.DataElements.GetEndBucket(shardOrdinal);

                for (long bucket = startBucket; bucket < endBucket; bucket++)
                {
                    int bucketKey = shard.DataElements.GetBucketKeyValue(bucket);

                    if (bucketKey != shard.DataElements.EmptyBucketKeyValue)
                    {
                        checksum.ApplyInt((int)(bucket - startBucket));
                        checksum.ApplyInt(bucketKey);
                        checksum.ApplyInt(shard.DataElements.GetBucketValueValue(bucket));
                    }
                }
            }
        }
    }
}
