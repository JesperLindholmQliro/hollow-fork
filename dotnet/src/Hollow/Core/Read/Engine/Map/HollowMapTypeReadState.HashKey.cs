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

namespace Hollow.Core.Read.Engine.Map;

/// <summary>
/// Lookups by a map schema's declared hash key.
/// </summary>
public sealed partial class HollowMapTypeReadState
{
    private HollowPrimaryKeyValueDeriver? _keyDeriver;

    /// <summary>
    /// Reads the keys' declared hash key, once the schema's key type has been wired up.
    /// </summary>
    /// <remarks>
    /// A key naming a type the state does not hold is not fatal: the records remain readable, only the
    /// lookups here stop working.
    /// </remarks>
    public HollowPrimaryKeyValueDeriver? KeyDeriver => _keyDeriver;

    /// <summary>
    /// Finds the key record of <paramref name="ordinal"/>'s map whose declared hash key is
    /// <paramref name="hashKey"/>.
    /// </summary>
    /// <returns>
    /// The key record's ordinal, or <see cref="HollowConstants.OrdinalNone"/> when the map has no such
    /// entry, this type declares no hash key, or the key has the wrong number of fields.
    /// </returns>
    public int FindKey(int ordinal, params object?[] hashKey)
    {
        long entry = FindEntry(ordinal, hashKey);
        return entry == -1L ? HollowConstants.OrdinalNone : (int)(entry >> 32);
    }

    /// <summary>
    /// Finds the value mapped to the key whose declared hash key is <paramref name="hashKey"/>.
    /// </summary>
    /// <returns>
    /// The value record's ordinal, or <see cref="HollowConstants.OrdinalNone"/> when the map has no such
    /// entry, this type declares no hash key, or the key has the wrong number of fields.
    /// </returns>
    public int FindValue(int ordinal, params object?[] hashKey)
    {
        long entry = FindEntry(ordinal, hashKey);
        return entry == -1L ? HollowConstants.OrdinalNone : (int)entry;
    }

    /// <summary>
    /// Finds the entry of <paramref name="ordinal"/>'s map whose key has the declared hash key
    /// <paramref name="hashKey"/>.
    /// </summary>
    /// <returns>
    /// The key ordinal in the high 32 bits and the value ordinal in the low 32 bits, or <c>-1</c> when
    /// the map has no such entry, this type declares no hash key, or the key has the wrong number of
    /// fields.
    /// </returns>
    public long FindEntry(int ordinal, params object?[] hashKey)
    {
        ArgumentNullException.ThrowIfNull(hashKey);

        if (_keyDeriver is not { } keyDeriver || hashKey.Length != keyDeriver.FieldTypes.Count)
        {
            return -1L;
        }

        Shard shard = _shards[ordinal & _shardNumberMask];
        int shardOrdinal = ordinal >> shard.ShardOrdinalShift;

        long startBucket = shard.DataElements.GetStartBucket(shardOrdinal);
        long endBucket = shard.DataElements.GetEndBucket(shardOrdinal);

        if (startBucket == endBucket)
        {
            return -1L;
        }

        int hashCode = SetMapKeyHasher.Hash(hashKey, keyDeriver.FieldTypes);

        long bucket = startBucket + (hashCode & (endBucket - startBucket - 1));
        int bucketKeyOrdinal = shard.DataElements.GetBucketKeyValue(bucket);

        while (bucketKeyOrdinal != shard.DataElements.EmptyBucketKeyValue)
        {
            if (keyDeriver.KeyMatches(bucketKeyOrdinal, hashKey))
            {
                return ((long)bucketKeyOrdinal << 32)
                    | (uint)shard.DataElements.GetBucketValueValue(bucket);
            }

            bucket++;
            if (bucket == endBucket)
            {
                bucket = startBucket;
            }

            bucketKeyOrdinal = shard.DataElements.GetBucketKeyValue(bucket);
        }

        return -1L;
    }

    /// <summary>
    /// Resolves the declared hash key against the state engine. Called once the key type has been wired
    /// onto the schema.
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
