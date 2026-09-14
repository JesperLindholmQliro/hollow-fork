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

using System.Numerics;

namespace Hollow.Core.Util;

/// <summary>
/// A fixed-capacity map from non-negative <see cref="int"/> to <see cref="int"/>.
/// </summary>
/// <remarks>
/// <para>
/// Open addressing with linear probing over two flat arrays, sized once at construction. A
/// <see cref="Dictionary{TKey,TValue}"/> would do the same job with an object header, a bucket array
/// and an entry array per instance; the history keeps one of these per type per state, so the
/// difference is worth having.
/// </para>
/// <para>
/// <strong>The capacity is a contract.</strong> There is no growth: the table is sized for the entry
/// count it is told about, and putting more than that eventually fills it. Java spins forever when
/// that happens; this throws, because a full table means the caller counted wrong and silence would
/// only hide it.
/// </para>
/// <para>
/// -1 marks an empty bucket, which is what makes a negative key meaningless here — the same
/// restriction Java's has, stated rather than implied.
/// </para>
/// </remarks>
public sealed class IntMap
{
    private readonly int[] _keys;
    private readonly int[] _values;

    /// <summary>
    /// Creates a map able to hold <paramref name="numEntries"/> entries without filling.
    /// </summary>
    public IntMap(int numEntries)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(numEntries);

        // Sized to a power of two at a 3/4 load factor, which is what keeps the probe runs short; the
        // power of two is also what lets the bucket index be a mask rather than a division.
        int capacity = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, (((numEntries + 1) * 4) / 3)));

        _keys = new int[capacity];
        _values = new int[capacity];

        Array.Fill(_keys, -1);
    }

    /// <summary>The number of entries held.</summary>
    public int Count { get; private set; }

    /// <summary>
    /// The value stored against <paramref name="key"/>, or -1 when there is none.
    /// </summary>
    public int Get(int key)
    {
        int mask = _keys.Length - 1;
        int bucket = HashKey(key) & mask;

        while (_keys[bucket] != -1)
        {
            if (_keys[bucket] == key)
            {
                return _values[bucket];
            }

            bucket = (bucket + 1) & mask;
        }

        return -1;
    }

    /// <summary>
    /// Stores <paramref name="value"/> against <paramref name="key"/>, replacing any previous value.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The map is full, which means it was sized for fewer entries than were put into it.
    /// </exception>
    public void Put(int key, int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(key);

        int mask = _keys.Length - 1;
        int bucket = HashKey(key) & mask;

        while (_keys[bucket] != -1)
        {
            if (_keys[bucket] == key)
            {
                _values[bucket] = value;

                return;
            }

            bucket = (bucket + 1) & mask;

            // Java probes forever here. A full table is a sizing mistake, and saying so is more use
            // than hanging.
            if (Count == _keys.Length)
            {
                throw new InvalidOperationException(
                    $"the map is full at {_keys.Length} entries; it was sized for fewer than were put into it");
            }
        }

        _keys[bucket] = key;
        _values[bucket] = value;
        Count++;
    }

    /// <summary>
    /// Every entry, in bucket order — which is neither insertion nor key order, and is not meant to be
    /// relied on.
    /// </summary>
    /// <remarks>
    /// Java exposes an <c>IntMapEntryIterator</c> with a <c>next()</c>/<c>getKey()</c>/<c>getValue()</c>
    /// shape. A sequence of tuples says the same thing and works with <c>foreach</c>.
    /// </remarks>
    public IEnumerable<(int Key, int Value)> Entries()
    {
        for (int i = 0; i < _keys.Length; i++)
        {
            if (_keys[i] != -1)
            {
                yield return (_keys[i], _values[i]);
            }
        }
    }

    /// <summary>
    /// Scrambles a key so that consecutive ordinals do not land in consecutive buckets.
    /// </summary>
    /// <remarks>
    /// Thomas Wang's 32-bit integer hash, as Java uses. The keys here are ordinals, which are dense
    /// and ascending, so the identity would cluster badly; this spreads them.
    /// </remarks>
    private static int HashKey(int key)
    {
        unchecked
        {
            key = ~key + (key << 15);
            key ^= (int)((uint)key >> 12);
            key += key << 2;
            key ^= (int)((uint)key >> 4);
            key *= 2057;
            key ^= (int)((uint)key >> 16);

            return key & int.MaxValue;
        }
    }
}
