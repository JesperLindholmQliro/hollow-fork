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

using Hollow.Core.Index.Key;
using Hollow.Core.Memory.Encoding;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.List;
using Hollow.Core.Read.Engine.Map;
using Hollow.Core.Read.Engine.Set;
using Hollow.Core.Read.Iterator;
using Hollow.Core.Schema;

namespace Hollow.Core.Tools.History;

/// <summary>
/// A list type, read as it stood in a state that has since gone.
/// </summary>
public sealed class HollowHistoricalListDataAccess(
    HollowHistoricalStateDataAccess dataAccess, HollowTypeReadState removedRecords)
    : HollowHistoricalTypeDataAccess(dataAccess, removedRecords), IHollowListTypeDataAccess
{
    /// <inheritdoc />
    public new HollowListSchema Schema => (HollowListSchema)RemovedRecords.Schema;

    /// <inheritdoc />
    HollowCollectionSchema IHollowCollectionTypeDataAccess.Schema => Schema;

    private HollowListTypeReadState Removed => (HollowListTypeReadState)RemovedRecords;

    /// <inheritdoc />
    public int GetElementOrdinal(int ordinal, int listIndex) =>
        OrdinalIsPresent(ordinal)
            ? Removed.GetElementOrdinal(GetMappedOrdinal(ordinal), listIndex)
            : ForwardTo<IHollowListTypeDataAccess>(ordinal).GetElementOrdinal(ordinal, listIndex);

    /// <inheritdoc />
    public int Size(int ordinal) =>
        OrdinalIsPresent(ordinal)
            ? Removed.Size(GetMappedOrdinal(ordinal))
            : ForwardTo<IHollowListTypeDataAccess>(ordinal).Size(ordinal);

    /// <inheritdoc />
    public IEnumerable<int> ElementOrdinals(int ordinal) =>
        OrdinalIsPresent(ordinal)
            ? Removed.ElementOrdinals(GetMappedOrdinal(ordinal))
            : ForwardTo<IHollowListTypeDataAccess>(ordinal).ElementOrdinals(ordinal);
}

/// <summary>
/// A set type, read as it stood in a state that has since gone.
/// </summary>
public sealed class HollowHistoricalSetDataAccess(
    HollowHistoricalStateDataAccess dataAccess, HollowTypeReadState removedRecords)
    : HollowHistoricalTypeDataAccess(dataAccess, removedRecords), IHollowSetTypeDataAccess
{
    private HistoricalPrimaryKeyMatcher? _keyMatcher;

    /// <inheritdoc />
    public new HollowSetSchema Schema => (HollowSetSchema)RemovedRecords.Schema;

    /// <inheritdoc />
    HollowCollectionSchema IHollowCollectionTypeDataAccess.Schema => Schema;

    private HollowSetTypeReadState Removed => (HollowSetTypeReadState)RemovedRecords;

    /// <inheritdoc />
    public int Size(int ordinal) =>
        OrdinalIsPresent(ordinal)
            ? Removed.Size(GetMappedOrdinal(ordinal))
            : ForwardTo<IHollowSetTypeDataAccess>(ordinal).Size(ordinal);

    /// <inheritdoc />
    public bool Contains(int ordinal, int value) =>
        OrdinalIsPresent(ordinal)
            ? Removed.Contains(GetMappedOrdinal(ordinal), value)
            : ForwardTo<IHollowSetTypeDataAccess>(ordinal).Contains(ordinal, value);

    /// <inheritdoc />
    public bool Contains(int ordinal, int value, int hashCode) =>
        OrdinalIsPresent(ordinal)
            ? Removed.Contains(GetMappedOrdinal(ordinal), value, hashCode)
            : ForwardTo<IHollowSetTypeDataAccess>(ordinal).Contains(ordinal, value, hashCode);

    /// <inheritdoc />
    public int RelativeBucketValue(int ordinal, int bucketIndex) =>
        OrdinalIsPresent(ordinal)
            ? Removed.RelativeBucketValue(GetMappedOrdinal(ordinal), bucketIndex)
            : ForwardTo<IHollowSetTypeDataAccess>(ordinal).RelativeBucketValue(ordinal, bucketIndex);

    /// <inheritdoc />
    public IEnumerable<int> PotentialMatchElementOrdinals(int ordinal, int hashCode) =>
        OrdinalIsPresent(ordinal)
            ? Removed.PotentialMatchElementOrdinals(GetMappedOrdinal(ordinal), hashCode)
            : ForwardTo<IHollowSetTypeDataAccess>(ordinal).PotentialMatchElementOrdinals(ordinal, hashCode);

    /// <inheritdoc />
    public IEnumerable<int> ElementOrdinals(int ordinal) =>
        OrdinalIsPresent(ordinal)
            ? Removed.ElementOrdinals(GetMappedOrdinal(ordinal))
            : ForwardTo<IHollowSetTypeDataAccess>(ordinal).ElementOrdinals(ordinal);

    /// <inheritdoc />
    /// <remarks>
    /// The lookup is done here rather than delegated, because the set's hash table is laid out
    /// relative to the record and the element ordinals inside it are this state's, not the copy's.
    /// Without a hash key there is nothing to look up by, and -1 is the answer.
    /// </remarks>
    public int FindElement(int ordinal, params object?[] hashKey)
    {
        if (_keyMatcher is not { } keyMatcher)
        {
            return -1;
        }

        if (!OrdinalIsPresent(ordinal))
        {
            return ForwardTo<IHollowSetTypeDataAccess>(ordinal).FindElement(ordinal, hashKey);
        }

        int mappedOrdinal = GetMappedOrdinal(ordinal);
        int hashTableSize = HashCodes.HashTableSize(Removed.Size(mappedOrdinal));
        int hash = SetMapKeyHasher.Hash(hashKey, keyMatcher.FieldTypes);

        int bucket = hash & (hashTableSize - 1);
        int bucketOrdinal = Removed.RelativeBucketValue(mappedOrdinal, bucket);

        while (bucketOrdinal != -1)
        {
            if (keyMatcher.KeyMatches(bucketOrdinal, hashKey))
            {
                return bucketOrdinal;
            }

            bucket = (bucket + 1) & (hashTableSize - 1);
            bucketOrdinal = Removed.RelativeBucketValue(mappedOrdinal, bucket);
        }

        return -1;
    }

    /// <summary>
    /// Prepares the key lookup, once every type of the state is in place.
    /// </summary>
    /// <remarks>
    /// Deferred because the matcher reads through the whole state: the key's path may leave this type,
    /// and the type it lands in has to exist first.
    /// </remarks>
    internal void BuildKeyMatcher()
    {
        if (Schema.HashKey is { } hashKey && HistoricalDataAccess.GetSchema(hashKey.Type) is not null)
        {
            _keyMatcher = new HistoricalPrimaryKeyMatcher(HistoricalDataAccess, hashKey);
        }
    }
}

/// <summary>
/// A map type, read as it stood in a state that has since gone.
/// </summary>
public sealed class HollowHistoricalMapDataAccess(
    HollowHistoricalStateDataAccess dataAccess, HollowTypeReadState removedRecords)
    : HollowHistoricalTypeDataAccess(dataAccess, removedRecords), IHollowMapTypeDataAccess
{
    private HistoricalPrimaryKeyMatcher? _keyMatcher;

    /// <inheritdoc />
    public new HollowMapSchema Schema => (HollowMapSchema)RemovedRecords.Schema;

    private HollowMapTypeReadState Removed => (HollowMapTypeReadState)RemovedRecords;

    /// <inheritdoc />
    public int Size(int ordinal) =>
        OrdinalIsPresent(ordinal)
            ? Removed.Size(GetMappedOrdinal(ordinal))
            : ForwardTo<IHollowMapTypeDataAccess>(ordinal).Size(ordinal);

    /// <inheritdoc />
    public int Get(int ordinal, int keyOrdinal) =>
        OrdinalIsPresent(ordinal)
            ? Removed.Get(GetMappedOrdinal(ordinal), keyOrdinal)
            : ForwardTo<IHollowMapTypeDataAccess>(ordinal).Get(ordinal, keyOrdinal);

    /// <inheritdoc />
    public int Get(int ordinal, int keyOrdinal, int hashCode) =>
        OrdinalIsPresent(ordinal)
            ? Removed.Get(GetMappedOrdinal(ordinal), keyOrdinal, hashCode)
            : ForwardTo<IHollowMapTypeDataAccess>(ordinal).Get(ordinal, keyOrdinal, hashCode);

    /// <inheritdoc />
    public long RelativeBucket(int ordinal, int bucketIndex) =>
        OrdinalIsPresent(ordinal)
            ? Removed.RelativeBucket(GetMappedOrdinal(ordinal), bucketIndex)
            : ForwardTo<IHollowMapTypeDataAccess>(ordinal).RelativeBucket(ordinal, bucketIndex);

    /// <inheritdoc />
    public IEnumerable<HollowMapEntry> PotentialMatchEntries(int ordinal, int hashCode) =>
        OrdinalIsPresent(ordinal)
            ? Removed.PotentialMatchEntries(GetMappedOrdinal(ordinal), hashCode)
            : ForwardTo<IHollowMapTypeDataAccess>(ordinal).PotentialMatchEntries(ordinal, hashCode);

    /// <inheritdoc />
    public IEnumerable<HollowMapEntry> Entries(int ordinal) =>
        OrdinalIsPresent(ordinal)
            ? Removed.Entries(GetMappedOrdinal(ordinal))
            : ForwardTo<IHollowMapTypeDataAccess>(ordinal).Entries(ordinal);

    /// <inheritdoc />
    public int FindKey(int ordinal, params object?[] hashKey) => (int)(FindEntry(ordinal, hashKey) >> 32);

    /// <inheritdoc />
    public int FindValue(int ordinal, params object?[] hashKey) => (int)FindEntry(ordinal, hashKey);

    /// <inheritdoc />
    /// <remarks>
    /// As with the set, the walk is done here because the hash table is relative to the record and
    /// holds this state's ordinals.
    /// </remarks>
    public long FindEntry(int ordinal, params object?[] hashKey)
    {
        if (_keyMatcher is not { } keyMatcher)
        {
            return -1L;
        }

        if (!OrdinalIsPresent(ordinal))
        {
            return ForwardTo<IHollowMapTypeDataAccess>(ordinal).FindEntry(ordinal, hashKey);
        }

        int mappedOrdinal = GetMappedOrdinal(ordinal);
        int hashTableSize = HashCodes.HashTableSize(Removed.Size(mappedOrdinal));
        int hash = SetMapKeyHasher.Hash(hashKey, keyMatcher.FieldTypes);

        int bucket = hash & (hashTableSize - 1);
        long bucketOrdinals = Removed.RelativeBucket(mappedOrdinal, bucket);

        while (bucketOrdinals != -1L)
        {
            if (keyMatcher.KeyMatches((int)(bucketOrdinals >> 32), hashKey))
            {
                return bucketOrdinals;
            }

            bucket = (bucket + 1) & (hashTableSize - 1);
            bucketOrdinals = Removed.RelativeBucket(mappedOrdinal, bucket);
        }

        return -1L;
    }

    /// <summary>
    /// Prepares the key lookup, once every type of the state is in place.
    /// </summary>
    internal void BuildKeyMatcher()
    {
        if (Schema.HashKey is { } hashKey && HistoricalDataAccess.GetSchema(hashKey.Type) is not null)
        {
            _keyMatcher = new HistoricalPrimaryKeyMatcher(HistoricalDataAccess, hashKey);
        }
    }
}
