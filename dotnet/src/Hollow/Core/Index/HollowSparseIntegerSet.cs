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
using Hollow.Core.Read.Engine;
using Hollow.Core.Util;

namespace Hollow.Core.Index;

/// <summary>
/// Decides whether a record takes part in an index.
/// </summary>
/// <remarks>
/// Java declares this as the nested functional interface
/// <c>HollowSparseIntegerSet.IndexPredicate</c>; a delegate is the C# equivalent.
/// </remarks>
public delegate bool IndexPredicate(int ordinal);

/// <summary>
/// A membership test over the sparse, non-negative integers a field path reaches.
/// </summary>
/// <remarks>
/// <para>
/// Given a type and a path to an integer field, this answers "is <c>n</c> one of the values in the
/// data?" without holding an ordinary bit set over the whole integer range. The values are expected to
/// be sparse — identifiers scattered across a large range rather than packed near zero — which is what
/// makes a plain bit set wasteful and this worth having.
/// </para>
/// <para>
/// The storage divides the range into buckets of 4096 values. A bucket holds only the 64-bit words
/// that actually have a bit set, plus a 64-bit index saying which of its 64 words those are, so an
/// empty bucket costs one null reference and a bucket with one value costs two longs.
/// </para>
/// <para>
/// Reads are safe alongside a delta update; only one thread may drive the updates.
/// </para>
/// </remarks>
public sealed class HollowSparseIntegerSet : IHollowTypeStateListener
{
    private readonly HollowReadStateEngine _readStateEngine;
    private readonly string _type;
    private readonly ValueFieldPath _fieldPath;
    private readonly IndexPredicate _predicate;

    private readonly HashSet<int> _valuesToSet = [];
    private readonly HashSet<int> _valuesToClear = [];

    private volatile SparseBitSet _sparseBitSet;

    private int _maxValueToSet;

    /// <summary>
    /// Indexes the integer values <paramref name="fieldPath"/> reaches from
    /// <paramref name="type"/>.
    /// </summary>
    /// <param name="readStateEngine">The state to index.</param>
    /// <param name="type">The type the path starts from.</param>
    /// <param name="fieldPath">The dot-separated path to an integer field.</param>
    /// <param name="predicate">
    /// Which records to index, or <see langword="null"/> to index them all.
    /// </param>
    public HollowSparseIntegerSet(
        HollowReadStateEngine readStateEngine,
        string type,
        string fieldPath,
        IndexPredicate? predicate = null)
    {
        ArgumentNullException.ThrowIfNull(readStateEngine);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrEmpty(fieldPath);

        _readStateEngine = readStateEngine;
        _type = type;
        _fieldPath = new ValueFieldPath(readStateEngine, type, fieldPath);
        _predicate = predicate ?? (static _ => true);
        _sparseBitSet = new SparseBitSet(int.MaxValue);

        Build();
    }

    /// <summary>Whether <paramref name="value"/> is one of the indexed values.</summary>
    public bool Get(int value)
    {
        SparseBitSet current;
        bool result;

        // A delta update may replace the whole set between the load and the read, so the read re-runs
        // against whichever set is current afterwards.
        do
        {
            current = _sparseBitSet;
            result = current.Get(value);
        }
        while (!ReferenceEquals(current, _sparseBitSet));

        return result;
    }

    /// <summary>How many values the set holds.</summary>
    public int Cardinality()
    {
        SparseBitSet current;
        int cardinality;

        do
        {
            current = _sparseBitSet;
            cardinality = current.Cardinality();
        }
        while (!ReferenceEquals(current, _sparseBitSet));

        return cardinality;
    }

    /// <summary>
    /// An estimate of the storage the set occupies, in bits.
    /// </summary>
    /// <remarks>Named <c>size</c> in Java, which reads as a count of members rather than of bits.</remarks>
    public long EstimateBitsUsed()
    {
        SparseBitSet current;
        long bits;

        do
        {
            current = _sparseBitSet;
            bits = current.EstimateBitsUsed();
        }
        while (!ReferenceEquals(current, _sparseBitSet));

        return bits;
    }

    /// <summary>
    /// Keeps this index in step with deltas applied to the read state.
    /// </summary>
    public void ListenForDeltaUpdates() => _readStateEngine.GetTypeState(_type)?.AddListener(this);

    /// <summary>Stops following delta updates.</summary>
    public void DetachFromDeltaUpdates() => _readStateEngine.GetTypeState(_type)?.RemoveListener(this);

    /// <inheritdoc />
    public void BeginUpdate()
    {
        _valuesToSet.Clear();
        _valuesToClear.Clear();
        _maxValueToSet = -1;
    }

    /// <inheritdoc />
    public void AddedOrdinal(int ordinal)
    {
        if (!_predicate(ordinal))
        {
            return;
        }

        foreach (int value in IntegerValues(ordinal))
        {
            _valuesToSet.Add(value);
            _maxValueToSet = Math.Max(_maxValueToSet, value);
        }
    }

    /// <inheritdoc />
    public void RemovedOrdinal(int ordinal)
    {
        foreach (int value in IntegerValues(ordinal))
        {
            _valuesToClear.Add(value);
        }
    }

    /// <inheritdoc />
    public void EndUpdate()
    {
        SparseBitSet updated = _sparseBitSet;
        bool replaced = false;

        // A value past the end of the current set needs a wider one, which means a new object rather
        // than a mutation, so the reference has to be republished afterwards.
        if (_valuesToSet.Count > 0 && _maxValueToSet > updated.FindMaxValue())
        {
            updated = SparseBitSet.Resize(updated, _maxValueToSet);
            replaced = true;
        }

        foreach (int value in _valuesToSet)
        {
            updated.Set(value);
        }

        foreach (int value in _valuesToClear)
        {
            updated.Clear(value);
        }

        if (replaced)
        {
            _sparseBitSet = updated;
        }
    }

    private void Build()
    {
        if (_readStateEngine.GetTypeState(_type) is { } typeState)
        {
            BitSet populatedOrdinals = typeState.PopulatedOrdinals;

            foreach (int ordinal in populatedOrdinals.EnumerateSetBits())
            {
                if (!_predicate(ordinal))
                {
                    continue;
                }

                foreach (int value in IntegerValues(ordinal))
                {
                    _sparseBitSet.Set(value);
                }
            }
        }

        // The set was built over the whole integer range; drop the buckets past the largest value.
        _sparseBitSet = SparseBitSet.Compact(_sparseBitSet);
    }

    private IEnumerable<int> IntegerValues(int ordinal) =>
        _fieldPath.FindValues(ordinal).OfType<int>();

    /// <summary>
    /// A bit set that stores only the words it needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The range is divided into buckets of 4096 values — 64 words of 64 bits. A bucket holds an index
    /// word whose <c>n</c>th bit says whether word <c>n</c> of the bucket has anything in it, and an
    /// array of just those words. So a bucket with one value costs two longs rather than 64, and a
    /// bucket with nothing costs a null reference.
    /// </para>
    /// <para>
    /// Updates replace a whole bucket at a time through an interlocked exchange, so a concurrent read
    /// sees either the bucket before the change or the one after it.
    /// </para>
    /// </remarks>
    internal sealed class SparseBitSet
    {
        /// <summary>How far to shift a value to get its bucket: 4096 values per bucket.</summary>
        private const int BucketShift = 12;

        /// <summary>How far to shift a value to get its word: 64 values per word.</summary>
        private const int WordShift = 6;

        /// <summary>How many words a bucket covers.</summary>
        private const int WordsPerBucket = 1 << (BucketShift - WordShift);

        private readonly Bucket?[] _buckets;

        internal SparseBitSet(int maxValue)
            : this(maxValue, new Bucket?[(maxValue >>> BucketShift) + 1])
        {
        }

        private SparseBitSet(int maxValue, Bucket?[] buckets)
        {
            MaxValue = maxValue;
            _buckets = buckets;
        }

        /// <summary>The largest value this set can hold.</summary>
        internal int MaxValue { get; }

        internal bool Get(int value)
        {
            if (value < 0 || value > MaxValue)
            {
                return false;
            }

            if (Volatile.Read(ref _buckets[BucketOf(value)]) is not { } bucket)
            {
                return false;
            }

            long wordBit = WordBitOf(value);

            if ((bucket.WordIndex & wordBit) == 0)
            {
                return false;
            }

            return (bucket.Words[OffsetOf(bucket.WordIndex, wordBit)] & BitWithinWordOf(value)) != 0;
        }

        internal void Set(int value)
        {
            if (value > MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value), value, $"This set holds values up to {MaxValue.Invariant()}.");
            }

            ArgumentOutOfRangeException.ThrowIfNegative(value);

            int bucketIndex = BucketOf(value);
            long wordBit = WordBitOf(value);
            long bitWithinWord = BitWithinWordOf(value);

            while (true)
            {
                Bucket? currentBucket = Volatile.Read(ref _buckets[bucketIndex]);

                long wordIndex = currentBucket?.WordIndex ?? 0;
                long[] words = currentBucket is null ? [] : [.. currentBucket.Words];

                if ((wordIndex & wordBit) != 0)
                {
                    // The word already exists; setting another bit in it leaves the layout alone.
                    words[OffsetOf(wordIndex, wordBit)] |= bitWithinWord;
                }
                else
                {
                    // The word has to be inserted, in the position its bit occupies in the index.
                    wordIndex |= wordBit;
                    words = Insert(words, OffsetOf(wordIndex, wordBit), bitWithinWord);
                }

                Bucket newBucket = new(wordIndex, words);

                if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _buckets[bucketIndex], newBucket, currentBucket),
                    currentBucket))
                {
                    return;
                }
            }
        }

        internal void Clear(int value)
        {
            if (value < 0 || value > MaxValue)
            {
                return;
            }

            int bucketIndex = BucketOf(value);
            long wordBit = WordBitOf(value);
            long bitWithinWord = BitWithinWordOf(value);

            while (true)
            {
                Bucket? currentBucket = Volatile.Read(ref _buckets[bucketIndex]);

                if (currentBucket is null || (currentBucket.WordIndex & wordBit) == 0)
                {
                    return;
                }

                long wordIndex = currentBucket.WordIndex;
                long[] words = [.. currentBucket.Words];

                int offset = OffsetOf(wordIndex, wordBit);
                long updatedWord = words[offset] & ~bitWithinWord;

                Bucket? newBucket;

                if (updatedWord != 0)
                {
                    words[offset] = updatedWord;
                    newBucket = new Bucket(wordIndex, words);
                }
                else if (words.Length == 1)
                {
                    // That was the bucket's only word, so the whole bucket goes.
                    newBucket = null;
                }
                else
                {
                    newBucket = new Bucket(wordIndex & ~wordBit, RemoveAt(words, offset));
                }

                if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _buckets[bucketIndex], newBucket, currentBucket),
                    currentBucket))
                {
                    return;
                }
            }
        }

        /// <summary>The largest value in the set, or -1 when it holds nothing.</summary>
        internal int FindMaxValue()
        {
            int bucketIndex = _buckets.Length - 1;
            while (bucketIndex >= 0 && Volatile.Read(ref _buckets[bucketIndex]) is null)
            {
                bucketIndex--;
            }

            if (bucketIndex < 0)
            {
                return -1;
            }

            Bucket bucket = Volatile.Read(ref _buckets[bucketIndex])!;

            int highestWord = 63 - BitOperations.LeadingZeroCount((ulong)bucket.WordIndex);
            int highestBit = 63 - BitOperations.LeadingZeroCount((ulong)bucket.Words[^1]);

            return ((int)((uint)bucketIndex << BucketShift)) + (highestWord << WordShift) + highestBit;
        }

        internal int Cardinality()
        {
            int cardinality = 0;

            foreach (ref Bucket? slot in _buckets.AsSpan())
            {
                if (Volatile.Read(ref slot) is { } bucket)
                {
                    foreach (long word in bucket.Words)
                    {
                        cardinality += BitOperations.PopCount((ulong)word);
                    }
                }
            }

            return cardinality;
        }

        internal long EstimateBitsUsed()
        {
            long populatedBuckets = 0;
            long words = 0;

            foreach (ref Bucket? slot in _buckets.AsSpan())
            {
                if (Volatile.Read(ref slot) is { } bucket)
                {
                    populatedBuckets++;
                    words += bucket.Words.Length;
                }
            }

            // One reference per bucket slot, one index word per populated bucket, and the words.
            return (_buckets.LongLength * 64) + (populatedBuckets * 64) + (words * 64);
        }

        /// <summary>
        /// Returns a copy holding no buckets past the largest value in <paramref name="sparseBitSet"/>.
        /// </summary>
        /// <remarks>
        /// A set is built over the whole integer range, which is half a million empty bucket slots.
        /// Compaction is what brings that down once the values are known; the result cannot take a value
        /// larger than the one it was compacted around without being resized first.
        /// </remarks>
        internal static SparseBitSet Compact(SparseBitSet sparseBitSet)
        {
            int maxValue = sparseBitSet.FindMaxValue();

            if (maxValue < 0)
            {
                // An empty set compacts to one bucket rather than none, so it can still take a value.
                maxValue = (1 << BucketShift) - 1;
            }

            int newLength = BucketOf(maxValue) + 1;

            return CopyWithNewLength(sparseBitSet, newLength, newLength, maxValue);
        }

        /// <summary>
        /// Returns a copy able to hold <paramref name="newMaxValue"/>, or the original when it already
        /// reaches that far.
        /// </summary>
        internal static SparseBitSet Resize(SparseBitSet sparseBitSet, int newMaxValue)
        {
            if (sparseBitSet.FindMaxValue() >= newMaxValue)
            {
                return sparseBitSet;
            }

            int newLength = BucketOf(newMaxValue) + 1;

            return CopyWithNewLength(
                sparseBitSet, newLength, sparseBitSet._buckets.Length, newMaxValue);
        }

        private static SparseBitSet CopyWithNewLength(
            SparseBitSet sparseBitSet, int newLength, int lengthToClone, int newMaxValue)
        {
            Bucket?[] buckets = new Bucket?[newLength];

            for (int i = 0; i < lengthToClone && i < newLength; i++)
            {
                buckets[i] = Volatile.Read(ref sparseBitSet._buckets[i]);
            }

            return new SparseBitSet(newMaxValue, buckets);
        }

        /// <summary>Which bucket holds a value.</summary>
        private static int BucketOf(int value) => value >>> BucketShift;

        /// <summary>
        /// The bit in a bucket's word index standing for the word that holds a value.
        /// </summary>
        private static long WordBitOf(int value) => 1L << ((value >>> WordShift) & (WordsPerBucket - 1));

        /// <summary>The bit within a word standing for a value.</summary>
        private static long BitWithinWordOf(int value) => 1L << (value & 63);

        /// <summary>
        /// Where a word sits in a bucket's array, which is how many words below it are populated.
        /// </summary>
        private static int OffsetOf(long wordIndex, long wordBit) =>
            BitOperations.PopCount((ulong)(wordIndex & (wordBit - 1)));

        private static long[] Insert(long[] words, int offset, long word)
        {
            long[] inserted = new long[words.Length + 1];

            words.AsSpan(0, offset).CopyTo(inserted);
            inserted[offset] = word;
            words.AsSpan(offset).CopyTo(inserted.AsSpan(offset + 1));

            return inserted;
        }

        private static long[] RemoveAt(long[] words, int offset)
        {
            long[] removed = new long[words.Length - 1];

            words.AsSpan(0, offset).CopyTo(removed);
            words.AsSpan(offset + 1).CopyTo(removed.AsSpan(offset));

            return removed;
        }

        /// <summary>
        /// One bucket's populated words, together with the index saying which words they are.
        /// </summary>
        internal sealed class Bucket(long wordIndex, long[] words)
        {
            /// <summary>A bit per word of the bucket, set where that word is present.</summary>
            internal long WordIndex { get; } = wordIndex;

            /// <summary>The populated words, in ascending order of the bits in the index.</summary>
            internal long[] Words { get; } = words;
        }
    }
}
