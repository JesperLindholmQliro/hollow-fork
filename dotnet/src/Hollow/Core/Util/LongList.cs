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
/// A growable list of longs.
/// </summary>
/// <remarks>
/// The diff stores a matched pair of ordinals as one long — the <c>from</c> ordinal in the high half
/// and the <c>to</c> ordinal in the low half — so that a list of pairs is one allocation rather than
/// one per pair.
/// </remarks>
public sealed class LongList
{
    private long[] _values;

    /// <summary>Initialises an empty list.</summary>
    public LongList(int initialCapacity = 8)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialCapacity);
        _values = new long[initialCapacity];
    }

    /// <summary>The number of values in the list.</summary>
    public int Count { get; private set; }

    /// <summary>Gets the value at <paramref name="index"/>.</summary>
    public long Get(int index) => _values[index];

    /// <summary>Sets the value at <paramref name="index"/>, which must already be within the list.</summary>
    public void Set(int index, long value) => _values[index] = value;

    /// <summary>Appends <paramref name="value"/>.</summary>
    public void Add(long value)
    {
        if (Count == _values.Length)
        {
            Array.Resize(ref _values, _values.Length * 2);
        }

        _values[Count++] = value;
    }

    /// <summary>Empties the list without releasing its storage.</summary>
    public void Clear() => Count = 0;

    /// <summary>Puts the values in ascending order.</summary>
    public void Sort() => Array.Sort(_values, 0, Count);
}
