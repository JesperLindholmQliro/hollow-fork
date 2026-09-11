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

namespace Hollow.Core.Memory;

/// <summary>
/// A stack of unused ordinals.
/// </summary>
/// <remarks>
/// Used by <see cref="ByteArrayOrdinalMap"/> to track and assign unused ordinals to new records. The
/// goal is to ensure the holes left by removing unused ordinals during server processing are reused in
/// subsequent cycles, instead of growing the ordinal space indefinitely.
/// </remarks>
public sealed class FreeOrdinalTracker
{
    private int[] _freeOrdinals = new int[64];
    private int _size;
    private int _nextEmptyOrdinal;

    /// <summary>
    /// Returns either an ordinal which was previously deallocated, or the next empty, previously
    /// unallocated ordinal in the sequence 0..n.
    /// </summary>
    public int GetFreeOrdinal() => _size == 0 ? _nextEmptyOrdinal++ : _freeOrdinals[--_size];

    /// <summary>
    /// Returns an ordinal to the pool after the record it was assigned to is discarded.
    /// </summary>
    public void ReturnOrdinalToPool(int ordinal)
    {
        if (_size == _freeOrdinals.Length)
        {
            Array.Resize(ref _freeOrdinals, _freeOrdinals.Length * 3 / 2);
        }

        _freeOrdinals[_size++] = ordinal;
    }

    /// <summary>
    /// Specifies the next ordinal to hand out once the reusable pool is exhausted.
    /// </summary>
    public void SetNextEmptyOrdinal(int nextEmptyOrdinal) => _nextEmptyOrdinal = nextEmptyOrdinal;

    /// <summary>
    /// Ensures that all future ordinals are returned in ascending order.
    /// </summary>
    public void Sort()
    {
        Array.Sort(_freeOrdinals, 0, _size);
        ReverseFreeOrdinalPool();
    }

    /// <summary>
    /// Concentrates the returned ordinal holes in as few shards as possible. Within each shard,
    /// ordinals are returned in ascending order.
    /// </summary>
    public void Sort(int numShards, int mapIndexBits, int mapIndex)
    {
        int shardNumberMask = numShards - 1;

        Shard[] shards = new Shard[numShards];
        for (int i = 0; i < shards.Length; i++)
        {
            shards[i] = new Shard();
        }

        for (int i = 0; i < _size; i++)
        {
            int freeGlobalOrdinal = (_freeOrdinals[i] << mapIndexBits) | mapIndex;
            shards[freeGlobalOrdinal & shardNumberMask].FreeOrdinalCount++;
        }

        // Lay the shards out back to back, densest first, so the holes cluster.
        Shard[] orderedShards = (Shard[])shards.Clone();
        Array.Sort(orderedShards, (first, second) => second.FreeOrdinalCount - first.FreeOrdinalCount);

        for (int i = 1; i < numShards; i++)
        {
            orderedShards[i].CurrentPosition = orderedShards[i - 1].CurrentPosition + orderedShards[i - 1].FreeOrdinalCount;
        }

        // Each shard receives its ordinals in ascending order.
        Array.Sort(_freeOrdinals, 0, _size);

        int[] newFreeOrdinals = new int[_freeOrdinals.Length];
        for (int i = 0; i < _size; i++)
        {
            int freeGlobalOrdinal = (_freeOrdinals[i] << mapIndexBits) | mapIndex;
            Shard shard = shards[freeGlobalOrdinal & shardNumberMask];
            newFreeOrdinals[shard.CurrentPosition++] = _freeOrdinals[i];
        }

        _freeOrdinals = newFreeOrdinals;

        ReverseFreeOrdinalPool();
    }

    /// <summary>
    /// Resets the tracker to its initial state.
    /// </summary>
    public void Reset()
    {
        _size = 0;
        _nextEmptyOrdinal = 0;
    }

    private void ReverseFreeOrdinalPool() => Array.Reverse(_freeOrdinals, 0, _size);

    private sealed class Shard
    {
        internal int FreeOrdinalCount;
        internal int CurrentPosition;
    }
}
