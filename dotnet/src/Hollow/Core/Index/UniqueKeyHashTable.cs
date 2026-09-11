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
using Hollow.Core.Memory.Encoding;
using Hollow.Core.Memory.Pool;
using Hollow.Core.Util;

namespace Hollow.Core.Index;

/// <summary>
/// The records a <see cref="UniqueKeyHashTable"/> indexes, and how to hash and compare their keys.
/// </summary>
/// <remarks>
/// The table itself does not know how a key is laid out; that is what separates
/// <see cref="HollowPrimaryKeyIndex"/> from <see cref="HollowUniqueKeyIndex"/>.
/// </remarks>
internal interface IUniqueKeyRecords
{
    /// <summary>The highest ordinal the indexed type holds.</summary>
    int MaxOrdinal { get; }

    /// <summary>The ordinals currently populated.</summary>
    BitSet PopulatedOrdinals { get; }

    /// <summary>The ordinals populated before the last delta transition.</summary>
    BitSet PreviousOrdinals { get; }

    /// <summary>Hashes the key of <paramref name="ordinal"/>'s record.</summary>
    int RecordHash(int ordinal);

    /// <summary>Whether two records hold the same key.</summary>
    bool RecordsHaveEqualKeys(int ordinal1, int ordinal2);

    /// <summary>Reads a record's key as boxed values.</summary>
    object?[] GetRecordKey(int ordinal);
}

/// <summary>
/// An open-addressed table of ordinals keyed by a record's key hash, shared by the two unique-key
/// indexes.
/// </summary>
/// <remarks>
/// An ordinal is stored one higher than it is, so that zero means "empty bucket". The table is
/// replaced wholesale rather than mutated, so a query running concurrently with an update reads one
/// generation or the other.
/// </remarks>
internal sealed class UniqueKeyHashTable(
    IUniqueKeyRecords records, IArraySegmentRecycler memoryRecycler, BitSet? specificOrdinalsToIndex)
{
    private readonly Lock _lock = new();

    private volatile Generation? _current;

    /// <summary>The table's current generation, or null before the first build.</summary>
    internal Generation? Current => _current;

    /// <summary>
    /// Whether delta updates patch the table incrementally rather than rebuilding it.
    /// </summary>
    /// <remarks>
    /// Off by default, as in Java: an incremental rebuild that goes wrong corrupts the index silently,
    /// and queries then return no match rather than failing.
    /// </remarks>
    internal bool AllowDeltaUpdate { get; set; }

    /// <summary>An approximation of the memory the table occupies, in bytes.</summary>
    internal long ApproxHeapFootprintInBytes => _current?.Table.ApproxHeapFootprintInBytes ?? 0;

    /// <summary>Rebuilds the whole table from the ordinals currently populated.</summary>
    internal void Reindex()
    {
        lock (_lock)
        {
            _current?.Table.Destroy(memoryRecycler);

            BitSet ordinals = specificOrdinalsToIndex ?? records.PopulatedOrdinals;

            long hashTableSize = HashCodes.IndexHashTableSize(ordinals.Cardinality());
            int bitsPerElement = 32 - BitOperations.LeadingZeroCount((uint)(records.MaxOrdinal + 1));

            FixedLengthElementArray table = new(memoryRecycler, hashTableSize * bitsPerElement);
            long hashMask = hashTableSize - 1;

            foreach (int ordinal in ordinals.EnumerateSetBits())
            {
                long bucket = records.RecordHash(ordinal) & hashMask;

                while (table.GetElementValue(bucket * bitsPerElement, bitsPerElement) != 0)
                {
                    bucket = (bucket + 1) & hashMask;
                }

                table.SetElementValue(bucket * bitsPerElement, bitsPerElement, ordinal + 1);
            }

            _current = new Generation(table, hashTableSize, hashMask, bitsPerElement);
            memoryRecycler.Swap();
        }
    }

    /// <summary>
    /// Brings the table up to date after a delta, incrementally where that is allowed and worthwhile.
    /// </summary>
    internal void EndUpdate()
    {
        lock (_lock)
        {
            if (_current is not { } generation)
            {
                return;
            }

            long hashTableSize = HashCodes.IndexHashTableSize(records.PopulatedOrdinals.Cardinality());
            int bitsPerElement = 32 - BitOperations.LeadingZeroCount((uint)(records.MaxOrdinal + 1));

            if (AllowDeltaUpdate
                && hashTableSize == generation.Size
                && bitsPerElement == generation.BitsPerElement
                && ShouldPerformDeltaUpdate())
            {
                try
                {
                    DeltaUpdate(hashTableSize, bitsPerElement);
                    return;
                }
                catch (OrdinalNotFoundException)
                {
                    // The incremental path could not find an ordinal it expected to remove. Rather than
                    // leave a corrupt table behind, fall through to a full rebuild.
                }
            }

            Reindex();
        }
    }

    /// <summary>The keys held by two or more records.</summary>
    internal IReadOnlyList<object?[]> GetDuplicateKeys()
    {
        lock (_lock)
        {
            if (_current is not { BitsPerElement: > 0 } generation)
            {
                return [];
            }

            List<object?[]> duplicateKeys = [];

            for (long i = 0; i < generation.Size; i++)
            {
                int ordinal = generation.ReadOrdinal(i);
                if (ordinal == HollowConstants.OrdinalNone)
                {
                    continue;
                }

                // A duplicate always lands in the run of occupied buckets following its twin, because
                // both hash to the same bucket and probe linearly from there.
                foreach (int compareOrdinal in EnumerateProbeRun(generation, i))
                {
                    if (records.RecordsHaveEqualKeys(ordinal, compareOrdinal))
                    {
                        duplicateKeys.Add(records.GetRecordKey(ordinal));
                    }
                }
            }

            return duplicateKeys;
        }
    }

    /// <summary>
    /// The keys held by two or more records, with the number of records sharing each, up to
    /// <paramref name="maxDuplicateKeys"/> of them.
    /// </summary>
    internal IReadOnlyList<HollowPrimaryKeyIndex.DuplicateKeyInfo> GetDuplicateKeys(int maxDuplicateKeys)
    {
        lock (_lock)
        {
            if (maxDuplicateKeys <= 0 || _current is not { BitsPerElement: > 0 } generation)
            {
                return [];
            }

            BitSet counted = new();
            List<HollowPrimaryKeyIndex.DuplicateKeyInfo> duplicateKeys = [];

            for (long i = 0; i < generation.Size && duplicateKeys.Count < maxDuplicateKeys; i++)
            {
                int ordinal = generation.ReadOrdinal(i);
                if (ordinal == HollowConstants.OrdinalNone || counted.Get(ordinal))
                {
                    continue;
                }

                long count = 1;
                counted.Set(ordinal);

                foreach (int compareOrdinal in EnumerateProbeRun(generation, i))
                {
                    if (records.RecordsHaveEqualKeys(ordinal, compareOrdinal))
                    {
                        count++;
                        counted.Set(compareOrdinal);
                    }
                }

                if (count > 1)
                {
                    duplicateKeys.Add(
                        new HollowPrimaryKeyIndex.DuplicateKeyInfo(records.GetRecordKey(ordinal), count));
                }
            }

            return duplicateKeys;
        }
    }

    /// <summary>Returns the table's storage to the recycler.</summary>
    internal void Destroy()
    {
        lock (_lock)
        {
            _current?.Table.Destroy(memoryRecycler);
            _current = null;
        }
    }

    /// <summary>
    /// Walks the occupied buckets following <paramref name="bucket"/>, which is where anything that
    /// collided with its occupant ended up.
    /// </summary>
    private static IEnumerable<int> EnumerateProbeRun(Generation generation, long bucket)
    {
        long compareBucket = (bucket + 1) & generation.HashMask;
        int compareOrdinal = generation.ReadOrdinal(compareBucket);

        while (compareOrdinal != HollowConstants.OrdinalNone)
        {
            yield return compareOrdinal;

            compareBucket = (compareBucket + 1) & generation.HashMask;
            compareOrdinal = generation.ReadOrdinal(compareBucket);
        }
    }

    /// <summary>
    /// Rebuilds the table by removing only the ordinals the transition dropped and adding only those it
    /// introduced, which is cheaper than a full rebuild when little changed.
    /// </summary>
    /// <exception cref="OrdinalNotFoundException">
    /// An ordinal that should have been in the table was not, which means the table no longer reflects
    /// the data.
    /// </exception>
    private void DeltaUpdate(long hashTableSize, int bitsPerElement)
    {
        Generation generation = _current!;
        generation.Table.Destroy(memoryRecycler);

        BitSet previousOrdinals = records.PreviousOrdinals;
        BitSet ordinals = records.PopulatedOrdinals;

        long totalBits = hashTableSize * bitsPerElement;
        FixedLengthElementArray table = new(memoryRecycler, totalBits);
        table.CopyBits(generation.Table, 0, 0, totalBits);

        long hashMask = hashTableSize - 1;

        foreach (int previousOrdinal in previousOrdinals.EnumerateSetBits())
        {
            if (ordinals.Get(previousOrdinal))
            {
                continue;
            }

            long bucket = FindOrdinalBucket(
                bitsPerElement, table, records.RecordHash(previousOrdinal), hashMask, previousOrdinal);

            table.ClearElementValue(bucket * bitsPerElement, bitsPerElement);

            // Clearing a bucket breaks the probe run through it, so every following entry that hashed
            // at or before the hole has to move back into it.
            long emptyBucket = bucket;
            bucket = (bucket + 1) & hashMask;
            int moveOrdinal = (int)table.GetElementValue(bucket * bitsPerElement, bitsPerElement) - 1;

            while (moveOrdinal != HollowConstants.OrdinalNone)
            {
                long naturalBucket = records.RecordHash(moveOrdinal) & hashMask;

                if (!BucketInRange(emptyBucket, bucket, naturalBucket))
                {
                    table.SetElementValue(emptyBucket * bitsPerElement, bitsPerElement, moveOrdinal + 1);
                    table.ClearElementValue(bucket * bitsPerElement, bitsPerElement);
                    emptyBucket = bucket;
                }

                bucket = (bucket + 1) & hashMask;
                moveOrdinal = (int)table.GetElementValue(bucket * bitsPerElement, bitsPerElement) - 1;
            }
        }

        foreach (int ordinal in ordinals.EnumerateSetBits())
        {
            if (previousOrdinals.Get(ordinal))
            {
                continue;
            }

            long bucket = records.RecordHash(ordinal) & hashMask;

            while (table.GetElementValue(bucket * bitsPerElement, bitsPerElement) != 0)
            {
                bucket = (bucket + 1) & hashMask;
            }

            table.SetElementValue(bucket * bitsPerElement, bitsPerElement, ordinal + 1);
        }

        _current = new Generation(table, hashTableSize, hashMask, bitsPerElement);
        memoryRecycler.Swap();
    }

    private static long FindOrdinalBucket(
        int bitsPerElement, FixedLengthElementArray table, int hashCode, long hashMask, int ordinal)
    {
        long startBucket = hashCode & hashMask;
        long bucket = startBucket;
        long value;

        do
        {
            value = table.GetElementValue(bucket * bitsPerElement, bitsPerElement);
            if (value == ordinal + 1)
            {
                return bucket;
            }

            bucket = (bucket + 1) & hashMask;
        }
        while (value != 0 && bucket != startBucket);

        throw new OrdinalNotFoundException(
            value == 0
                ? $"ordinal not found (found empty entry): ordinal={ordinal} startBucket={startBucket}"
                : $"ordinal not found (wrapped around table): ordinal={ordinal} startBucket={startBucket}");
    }

    /// <summary>
    /// Whether <paramref name="testBucket"/> falls in the probe run from <paramref name="fromBucket"/>
    /// to <paramref name="toBucket"/>, which may wrap past the end of the table.
    /// </summary>
    private static bool BucketInRange(long fromBucket, long toBucket, long testBucket) =>
        toBucket > fromBucket
            ? testBucket > fromBucket && testBucket <= toBucket
            : testBucket > fromBucket || testBucket <= toBucket;

    /// <summary>
    /// A full rebuild is cheaper once a sizeable share of the previous records has gone, because each
    /// removal has to repair the probe run it breaks.
    /// </summary>
    private bool ShouldPerformDeltaUpdate()
    {
        BitSet previousOrdinals = records.PreviousOrdinals;
        BitSet ordinals = records.PopulatedOrdinals;

        int previousCardinality = 0;
        int removedRecords = 0;

        foreach (int previousOrdinal in previousOrdinals.EnumerateSetBits())
        {
            previousCardinality++;
            if (!ordinals.Get(previousOrdinal))
            {
                removedRecords++;
            }
        }

        return removedRecords <= previousCardinality * 0.1d;
    }

    /// <summary>
    /// One immutable generation of the table.
    /// </summary>
    internal sealed class Generation(
        FixedLengthElementArray table, long size, long hashMask, int bitsPerElement)
    {
        internal FixedLengthElementArray Table { get; } = table;

        internal long Size { get; } = size;

        internal long HashMask { get; } = hashMask;

        internal int BitsPerElement { get; } = bitsPerElement;

        /// <summary>
        /// The ordinal in <paramref name="bucket"/>, or <see cref="HollowConstants.OrdinalNone"/> when
        /// the bucket is empty.
        /// </summary>
        internal int ReadOrdinal(long bucket) =>
            (int)Table.GetElementValue(bucket * BitsPerElement, BitsPerElement) - 1;
    }

    /// <summary>
    /// Thrown when an incremental delta update cannot find an ordinal the table should contain.
    /// </summary>
    private sealed class OrdinalNotFoundException(string message) : InvalidOperationException(message);
}
