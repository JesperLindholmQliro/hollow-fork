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

namespace Hollow.Core.Util;

/// <summary>
/// A growable list of ints that can also be grown to a length without appending, so that positions
/// beyond the current end can be written directly.
/// </summary>
/// <remarks>
/// <see cref="List{T}"/> would serve for everything except <see cref="ExpandTo"/>, which the hash
/// index's traversal relies on when it multiplies out the matches of sibling branches: it needs room
/// for the combinations before it knows what goes in each slot.
/// </remarks>
public sealed class IntList
{
    private int[] _values;

    /// <summary>Initialises an empty list.</summary>
    public IntList(int initialCapacity = 8)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialCapacity);
        _values = new int[initialCapacity];
    }

    /// <summary>The number of values in the list.</summary>
    public int Count { get; private set; }

    /// <summary>Gets the value at <paramref name="index"/>.</summary>
    public int Get(int index) => _values[index];

    /// <summary>Sets the value at <paramref name="index"/>, which must already be within the list.</summary>
    public void Set(int index, int value) => _values[index] = value;

    /// <summary>Appends <paramref name="value"/>.</summary>
    public void Add(int value)
    {
        EnsureCapacity(Count + 1);
        _values[Count++] = value;
    }

    /// <summary>
    /// Grows the list to <paramref name="count"/> values, or shrinks it to that many, leaving whatever
    /// the new positions previously held.
    /// </summary>
    public void ExpandTo(int count)
    {
        EnsureCapacity(count);
        Count = count;
    }

    /// <summary>Empties the list without releasing its storage.</summary>
    public void Clear() => Count = 0;

    private void EnsureCapacity(int required)
    {
        if (required <= _values.Length)
        {
            return;
        }

        int capacity = _values.Length;
        while (capacity < required)
        {
            capacity *= 2;
        }

        Array.Resize(ref _values, capacity);
    }
}
