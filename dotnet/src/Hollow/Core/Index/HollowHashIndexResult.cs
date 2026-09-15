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

using System.Collections;
using Hollow.Core.Memory.Encoding;

namespace Hollow.Core.Index;

/// <summary>
/// The records one query matched in a <see cref="HollowHashIndex"/>.
/// </summary>
/// <remarks>
/// The matches are held as a hash table of ordinals rather than a list, so <see cref="Contains"/> is a
/// probe rather than a scan. Iteration walks the table, so the order is the hash's, not the order the
/// records were added.
/// </remarks>
public sealed class HollowHashIndexResult : IEnumerable<int>
{
    private readonly HollowHashIndex.HashIndexState _hashIndexState;
    private readonly long _selectTableStartPointer;
    private readonly int _selectTableBuckets;
    private readonly int _selectBucketMask;

    internal HollowHashIndexResult(
        HollowHashIndex.HashIndexState hashIndexState, long selectTableStartPointer, int selectTableSize)
    {
        _hashIndexState = hashIndexState;
        _selectTableStartPointer = selectTableStartPointer;
        _selectTableBuckets = HashCodes.HashTableSize(selectTableSize);
        _selectBucketMask = _selectTableBuckets - 1;

        Count = selectTableSize;
    }

    /// <summary>The number of matched records.</summary>
    public int Count { get; }

    /// <summary>Whether <paramref name="ordinal"/> is among the matches.</summary>
    public bool Contains(int ordinal)
    {
        int bucket = HashCodes.HashInt(ordinal) & _selectBucketMask;
        int selectOrdinal = ReadBucket(_selectTableStartPointer + bucket);

        while (selectOrdinal != HollowConstants.OrdinalNone)
        {
            if (selectOrdinal == ordinal)
            {
                return true;
            }

            bucket = (bucket + 1) & _selectBucketMask;
            selectOrdinal = ReadBucket(_selectTableStartPointer + bucket);
        }

        return false;
    }

    /// <summary>
    /// Enumerates the matched ordinals, which may be used with the read state to inspect the records.
    /// </summary>
    /// <remarks>
    /// Java hands back a <c>HollowOrdinalIterator</c> from <c>iterator()</c>. The result is the
    /// sequence here, so it works with <c>foreach</c> and LINQ without an intermediate.
    /// </remarks>
    public IEnumerator<int> GetEnumerator()
    {
        long endBucket = _selectTableStartPointer + _selectTableBuckets;

        for (long bucket = _selectTableStartPointer; bucket < endBucket; bucket++)
        {
            int selectOrdinal = ReadBucket(bucket);

            if (selectOrdinal != HollowConstants.OrdinalNone)
            {
                yield return selectOrdinal;
            }
        }
    }

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private int ReadBucket(long bucket) =>
        (int)_hashIndexState.SelectHashArray.GetElementValue(
            bucket * _hashIndexState.BitsPerSelectHashEntry, _hashIndexState.BitsPerSelectHashEntry) - 1;

}
