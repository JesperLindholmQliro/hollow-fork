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
/// A growable set of bits indexed by a non-negative <see cref="int"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Port note.</strong> Hollow leans on <c>java.util.BitSet</c> throughout, mostly to track
/// which ordinals are populated. .NET's <see cref="System.Collections.BitArray"/> is fixed-length and
/// has no <c>nextSetBit</c>, so this supplies the growable, ordinal-oriented subset Hollow actually
/// uses.
/// </para>
/// <para>
/// This type is not thread-safe; see <see cref="Memory.ThreadSafeBitSet"/> for the concurrent variant.
/// </para>
/// </remarks>
public sealed class BitSet : IEquatable<BitSet>
{
    private long[] _words;

    /// <summary>
    /// Initialises an empty bit set.
    /// </summary>
    public BitSet()
        : this(64)
    {
    }

    /// <summary>
    /// Initialises an empty bit set sized to hold at least <paramref name="numBits"/> bits without
    /// growing.
    /// </summary>
    public BitSet(int numBits)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(numBits);
        _words = new long[WordCountFor(numBits)];
    }

    /// <summary>
    /// One past the highest set bit, or 0 when no bit is set.
    /// </summary>
    public int Length
    {
        get
        {
            for (int i = _words.Length - 1; i >= 0; i--)
            {
                if (_words[i] != 0)
                {
                    return (i * 64) + 64 - BitOperations.LeadingZeroCount((ulong)_words[i]);
                }
            }

            return 0;
        }
    }

    /// <summary>Gets or sets the bit at <paramref name="index"/>.</summary>
    public bool this[int index]
    {
        get => Get(index);
        set
        {
            if (value)
            {
                Set(index);
            }
            else
            {
                Clear(index);
            }
        }
    }

    /// <summary>Sets the bit at <paramref name="index"/>.</summary>
    public void Set(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        int wordIndex = index >> 6;
        EnsureCapacity(wordIndex + 1);
        _words[wordIndex] |= 1L << index;
    }

    /// <summary>Clears the bit at <paramref name="index"/>.</summary>
    public void Clear(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        int wordIndex = index >> 6;
        if (wordIndex < _words.Length)
        {
            _words[wordIndex] &= ~(1L << index);
        }
    }

    /// <summary>Gets the bit at <paramref name="index"/>.</summary>
    public bool Get(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        int wordIndex = index >> 6;
        return wordIndex < _words.Length && (_words[wordIndex] & (1L << index)) != 0;
    }

    /// <summary>Clears every bit.</summary>
    public void Clear() => Array.Clear(_words);

    /// <summary>Sets every bit that is set in <paramref name="other"/>.</summary>
    public void Or(BitSet other)
    {
        ArgumentNullException.ThrowIfNull(other);

        EnsureCapacity(other._words.Length);
        for (int i = 0; i < other._words.Length; i++)
        {
            _words[i] |= other._words[i];
        }
    }

    /// <summary>Clears every bit that is not also set in <paramref name="other"/>.</summary>
    public void And(BitSet other)
    {
        ArgumentNullException.ThrowIfNull(other);

        for (int i = 0; i < _words.Length; i++)
        {
            // Anything past the end of the other set is clear there, so it clears here.
            _words[i] &= i < other._words.Length ? other._words[i] : 0;
        }
    }

    /// <summary>Clears every bit that is set in <paramref name="other"/>.</summary>
    public void AndNot(BitSet other)
    {
        ArgumentNullException.ThrowIfNull(other);

        int shared = Math.Min(_words.Length, other._words.Length);
        for (int i = 0; i < shared; i++)
        {
            _words[i] &= ~other._words[i];
        }
    }

    /// <summary>The number of set bits.</summary>
    public int Cardinality()
    {
        int count = 0;
        foreach (long word in _words)
        {
            count += BitOperations.PopCount((ulong)word);
        }

        return count;
    }

    /// <summary>
    /// The first set bit at or after <paramref name="fromIndex"/>, or -1 when there is none.
    /// </summary>
    public int NextSetBit(int fromIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fromIndex);

        int wordIndex = fromIndex >> 6;
        if (wordIndex >= _words.Length)
        {
            return -1;
        }

        long word = _words[wordIndex] & (-1L << fromIndex);

        while (true)
        {
            if (word != 0)
            {
                return (wordIndex * 64) + BitOperations.TrailingZeroCount((ulong)word);
            }

            if (++wordIndex == _words.Length)
            {
                return -1;
            }

            word = _words[wordIndex];
        }
    }

    /// <summary>
    /// The first clear bit at or after <paramref name="fromIndex"/>. Because the set is conceptually
    /// unbounded, this always finds one.
    /// </summary>
    public int NextClearBit(int fromIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fromIndex);

        int wordIndex = fromIndex >> 6;
        if (wordIndex >= _words.Length)
        {
            return fromIndex;
        }

        long word = ~_words[wordIndex] & (-1L << fromIndex);

        while (true)
        {
            if (word != 0)
            {
                return (wordIndex * 64) + BitOperations.TrailingZeroCount((ulong)word);
            }

            if (++wordIndex == _words.Length)
            {
                return wordIndex * 64;
            }

            word = ~_words[wordIndex];
        }
    }

    /// <summary>
    /// Enumerates the positions of the set bits, in ascending order.
    /// </summary>
    public IEnumerable<int> EnumerateSetBits()
    {
        for (int index = NextSetBit(0); index != -1; index = NextSetBit(index + 1))
        {
            yield return index;
        }
    }

    /// <summary>Returns an independent copy of this bit set.</summary>
    public BitSet Clone()
    {
        BitSet copy = new(0);
        copy._words = (long[])_words.Clone();
        return copy;
    }

    /// <inheritdoc />
    public bool Equals(BitSet? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        int shared = Math.Min(_words.Length, other._words.Length);
        for (int i = 0; i < shared; i++)
        {
            if (_words[i] != other._words[i])
            {
                return false;
            }
        }

        // Trailing words beyond the shorter set must be empty for the two to be equal.
        return AllZeroFrom(_words, shared) && AllZeroFrom(other._words, shared);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is BitSet other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        System.HashCode hash = default;
        foreach (int index in EnumerateSetBits())
        {
            hash.Add(index);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"{{{InvariantFormatting.JoinInvariant(", ", EnumerateSetBits())}}}";

    private static bool AllZeroFrom(long[] words, int start)
    {
        for (int i = start; i < words.Length; i++)
        {
            if (words[i] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static int WordCountFor(int numBits) => numBits == 0 ? 0 : ((numBits - 1) >> 6) + 1;

    private void EnsureCapacity(int wordCount)
    {
        if (wordCount > _words.Length)
        {
            Array.Resize(ref _words, Math.Max(wordCount, _words.Length * 2));
        }
    }
}
