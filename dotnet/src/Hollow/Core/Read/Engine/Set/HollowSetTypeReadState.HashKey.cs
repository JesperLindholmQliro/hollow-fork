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

using Hollow.Core.Index;
using Hollow.Core.Index.Key;

namespace Hollow.Core.Read.Engine.Set;

/// <summary>
/// Lookups by a set schema's declared hash key.
/// </summary>
public sealed partial class HollowSetTypeReadState
{
    private HollowPrimaryKeyValueDeriver? _keyDeriver;

    /// <summary>
    /// Reads the elements' declared hash key, once the schema's element type has been wired up.
    /// </summary>
    /// <remarks>
    /// A key naming a type the state does not hold is not fatal: the records remain readable, only
    /// <see cref="FindElement"/> stops working.
    /// </remarks>
    public HollowPrimaryKeyValueDeriver? KeyDeriver => _keyDeriver;

    /// <summary>
    /// Finds the element of <paramref name="ordinal"/>'s set whose declared hash key is
    /// <paramref name="hashKey"/>.
    /// </summary>
    /// <returns>
    /// The element's ordinal, or <see cref="HollowConstants.OrdinalNone"/> when the set has no such
    /// element, this type declares no hash key, or the key has the wrong number of fields.
    /// </returns>
    public int FindElement(int ordinal, params object?[] hashKey)
    {
        ArgumentNullException.ThrowIfNull(hashKey);

        if (_keyDeriver is not { } keyDeriver || hashKey.Length != keyDeriver.FieldTypes.Count)
        {
            return HollowConstants.OrdinalNone;
        }

        Shard shard = ShardFor(ordinal);
        int shardOrdinal = ordinal >> shard.ShardOrdinalShift;

        long startBucket = shard.DataElements.GetStartBucket(shardOrdinal);
        long endBucket = shard.DataElements.GetEndBucket(shardOrdinal);

        if (startBucket == endBucket)
        {
            return HollowConstants.OrdinalNone;
        }

        int hashCode = SetMapKeyHasher.Hash(hashKey, keyDeriver.FieldTypes);

        long bucket = startBucket + (hashCode & (endBucket - startBucket - 1));
        int bucketOrdinal = shard.DataElements.GetBucketValue(bucket);

        while (bucketOrdinal != shard.DataElements.EmptyBucketValue)
        {
            if (keyDeriver.KeyMatches(bucketOrdinal, hashKey))
            {
                return bucketOrdinal;
            }

            bucket++;
            if (bucket == endBucket)
            {
                bucket = startBucket;
            }

            bucketOrdinal = shard.DataElements.GetBucketValue(bucket);
        }

        return HollowConstants.OrdinalNone;
    }

    /// <summary>
    /// Resolves the declared hash key against the state engine. Called once the element type has been
    /// wired onto the schema.
    /// </summary>
    internal void BuildKeyDeriver()
    {
        if (Schema.HashKey is not { } hashKey)
        {
            return;
        }

        try
        {
            _keyDeriver = new HollowPrimaryKeyValueDeriver(hashKey, StateEngine);
        }
        catch (FieldPathException e) when (e.Error == FieldPathError.NotBindable)
        {
            _keyDeriver = null;
        }
    }
}
