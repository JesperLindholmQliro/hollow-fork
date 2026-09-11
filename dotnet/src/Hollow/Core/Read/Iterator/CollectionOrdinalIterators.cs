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
using Hollow.Core.Read.DataAccess;

namespace Hollow.Core.Read.Iterator;

/// <summary>
/// Iterates a list record's element ordinals in order.
/// </summary>
public sealed class HollowListOrdinalIterator : IHollowOrdinalIterator
{
    private readonly int _listOrdinal;
    private readonly IHollowListTypeDataAccess _dataAccess;
    private readonly int _size;
    private int _currentIndex;

    /// <summary>
    /// Initialises an iterator over <paramref name="listOrdinal"/>.
    /// </summary>
    public HollowListOrdinalIterator(int listOrdinal, IHollowListTypeDataAccess dataAccess)
    {
        ArgumentNullException.ThrowIfNull(dataAccess);

        _listOrdinal = listOrdinal;
        _dataAccess = dataAccess;
        _size = dataAccess.Size(listOrdinal);
    }

    /// <inheritdoc />
    public int Next() =>
        _currentIndex >= _size
            ? IHollowOrdinalIterator.NoMoreOrdinals
            : _dataAccess.GetElementOrdinal(_listOrdinal, _currentIndex++);
}

/// <summary>
/// Iterates a set record's element ordinals by walking its hash table and skipping empty buckets.
/// </summary>
public sealed class HollowSetOrdinalIterator : IHollowOrdinalIterator
{
    private readonly int _setOrdinal;
    private readonly IHollowSetTypeDataAccess _dataAccess;
    private readonly int _numBuckets;
    private int _currentBucket = -1;

    /// <summary>
    /// Initialises an iterator over <paramref name="setOrdinal"/>.
    /// </summary>
    public HollowSetOrdinalIterator(int setOrdinal, IHollowSetTypeDataAccess dataAccess)
    {
        ArgumentNullException.ThrowIfNull(dataAccess);

        _setOrdinal = setOrdinal;
        _dataAccess = dataAccess;
        _numBuckets = HashCodes.HashTableSize(dataAccess.Size(setOrdinal));
    }

    /// <summary>The bucket the last returned element came from.</summary>
    public int CurrentBucket => _currentBucket;

    /// <inheritdoc />
    public int Next()
    {
        int bucketValue = HollowConstants.OrdinalNone;

        while (bucketValue == HollowConstants.OrdinalNone)
        {
            _currentBucket++;
            if (_currentBucket >= _numBuckets)
            {
                return IHollowOrdinalIterator.NoMoreOrdinals;
            }

            bucketValue = _dataAccess.RelativeBucketValue(_setOrdinal, _currentBucket);
        }

        return bucketValue;
    }
}

/// <summary>
/// Iterates only the elements of a set record that hash to a given bucket run, stopping at the first
/// empty bucket.
/// </summary>
/// <remarks>
/// This is what a key-based lookup walks: elements that collided on the same hash land in consecutive
/// buckets, so the run from the hashed bucket to the first empty one holds every candidate.
/// </remarks>
public sealed class PotentialMatchHollowSetOrdinalIterator : IHollowOrdinalIterator
{
    private readonly int _setOrdinal;
    private readonly IHollowSetTypeDataAccess _dataAccess;
    private readonly int _numBuckets;
    private int _currentBucket;
    private bool _exhausted;

    /// <summary>
    /// Initialises an iterator over the elements of <paramref name="setOrdinal"/> that could match
    /// <paramref name="hashCode"/>.
    /// </summary>
    public PotentialMatchHollowSetOrdinalIterator(
        int setOrdinal, IHollowSetTypeDataAccess dataAccess, int hashCode)
    {
        ArgumentNullException.ThrowIfNull(dataAccess);

        _setOrdinal = setOrdinal;
        _dataAccess = dataAccess;
        _numBuckets = HashCodes.HashTableSize(dataAccess.Size(setOrdinal));
        _currentBucket = _numBuckets == 0 ? 0 : HashCodes.HashInt(hashCode) & (_numBuckets - 1);
    }

    /// <inheritdoc />
    public int Next()
    {
        if (_exhausted || _numBuckets == 0)
        {
            return IHollowOrdinalIterator.NoMoreOrdinals;
        }

        int bucketValue = _dataAccess.RelativeBucketValue(_setOrdinal, _currentBucket);
        if (bucketValue == HollowConstants.OrdinalNone)
        {
            _exhausted = true;
            return IHollowOrdinalIterator.NoMoreOrdinals;
        }

        _currentBucket = (_currentBucket + 1) & (_numBuckets - 1);
        return bucketValue;
    }
}

/// <summary>
/// Iterates a map record's entries by walking its hash table and skipping empty buckets.
/// </summary>
public sealed class HollowMapEntryOrdinalIteratorImpl : IHollowMapEntryOrdinalIterator
{
    private readonly int _mapOrdinal;
    private readonly IHollowMapTypeDataAccess _dataAccess;
    private readonly int _numBuckets;
    private int _currentBucket = -1;

    /// <summary>
    /// Initialises an iterator over <paramref name="mapOrdinal"/>.
    /// </summary>
    public HollowMapEntryOrdinalIteratorImpl(int mapOrdinal, IHollowMapTypeDataAccess dataAccess)
    {
        ArgumentNullException.ThrowIfNull(dataAccess);

        _mapOrdinal = mapOrdinal;
        _dataAccess = dataAccess;
        _numBuckets = HashCodes.HashTableSize(dataAccess.Size(mapOrdinal));
    }

    /// <inheritdoc />
    public int Key { get; private set; } = HollowConstants.OrdinalNone;

    /// <inheritdoc />
    public int Value { get; private set; } = HollowConstants.OrdinalNone;

    /// <inheritdoc />
    public bool Next()
    {
        while (true)
        {
            _currentBucket++;
            if (_currentBucket >= _numBuckets)
            {
                return false;
            }

            long entry = _dataAccess.RelativeBucket(_mapOrdinal, _currentBucket);
            if (entry != -1L)
            {
                Key = (int)((ulong)entry >> 32);
                Value = (int)entry;
                return true;
            }
        }
    }
}

/// <summary>
/// Iterates only the entries of a map record whose keys hash to a given bucket run, stopping at the
/// first empty bucket.
/// </summary>
public sealed class PotentialMatchHollowMapEntryOrdinalIteratorImpl : IHollowMapEntryOrdinalIterator
{
    private readonly int _mapOrdinal;
    private readonly IHollowMapTypeDataAccess _dataAccess;
    private readonly int _numBuckets;
    private int _currentBucket;
    private bool _exhausted;

    /// <summary>
    /// Initialises an iterator over the entries of <paramref name="mapOrdinal"/> that could match
    /// <paramref name="hashCode"/>.
    /// </summary>
    public PotentialMatchHollowMapEntryOrdinalIteratorImpl(
        int mapOrdinal, IHollowMapTypeDataAccess dataAccess, int hashCode)
    {
        ArgumentNullException.ThrowIfNull(dataAccess);

        _mapOrdinal = mapOrdinal;
        _dataAccess = dataAccess;
        _numBuckets = HashCodes.HashTableSize(dataAccess.Size(mapOrdinal));
        _currentBucket = _numBuckets == 0 ? 0 : HashCodes.HashInt(hashCode) & (_numBuckets - 1);
    }

    /// <inheritdoc />
    public int Key { get; private set; } = HollowConstants.OrdinalNone;

    /// <inheritdoc />
    public int Value { get; private set; } = HollowConstants.OrdinalNone;

    /// <inheritdoc />
    public bool Next()
    {
        if (_exhausted || _numBuckets == 0)
        {
            return false;
        }

        long entry = _dataAccess.RelativeBucket(_mapOrdinal, _currentBucket);
        if (entry == -1L)
        {
            _exhausted = true;
            return false;
        }

        Key = (int)((ulong)entry >> 32);
        Value = (int)entry;
        _currentBucket = (_currentBucket + 1) & (_numBuckets - 1);

        return true;
    }
}
