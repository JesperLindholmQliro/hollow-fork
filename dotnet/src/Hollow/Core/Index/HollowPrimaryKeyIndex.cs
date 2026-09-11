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
using Hollow.Core.Index.Key;
using Hollow.Core.Memory.Encoding;
using Hollow.Core.Memory.Pool;
using Hollow.Core.Read;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Schema;
using Hollow.Core.Util;

namespace Hollow.Core.Index;

/// <summary>
/// Indexes a type by a <see cref="PrimaryKey"/>, so records can be looked up by their key values
/// instead of by ordinal.
/// </summary>
/// <remarks>
/// <para>
/// The key indexed need not be the one declared on the type's schema.
/// </para>
/// <para>
/// <strong>Port note.</strong> Java's <c>getDuplicateKeys</c> pair, the incremental delta update of the
/// hash table and the <c>allowDeltaUpdate</c> system property are ported; object longevity, on which
/// Java's own docs warn this class is unsafe, is not part of this port at all.
/// </para>
/// </remarks>
public sealed class HollowPrimaryKeyIndex : IHollowTypeStateListener, IDisposable
{
    private readonly HollowObjectTypeReadState? _typeState;
    private readonly int[][] _fieldPathIndexes;
    private readonly FieldType[] _fieldTypes;
    private readonly HollowPrimaryKeyValueDeriver? _keyDeriver;
    private readonly IArraySegmentRecycler _memoryRecycler;
    private readonly BitSet? _specificOrdinalsToIndex;
    private readonly Lock _lock = new();

    private volatile HashTable? _hashTable;

    /// <summary>
    /// Indexes <paramref name="type"/> of <paramref name="stateEngine"/> by
    /// <paramref name="fieldPaths"/>, or by the type's declared primary key when none are given.
    /// </summary>
    public HollowPrimaryKeyIndex(HollowReadStateEngine stateEngine, string type, params string[] fieldPaths)
        : this(stateEngine, ResolveKey(stateEngine, type, fieldPaths))
    {
    }

    /// <summary>
    /// Indexes the type named by <paramref name="primaryKey"/>.
    /// </summary>
    /// <param name="stateEngine">The state to index.</param>
    /// <param name="primaryKey">The key to index by.</param>
    /// <param name="memoryRecycler">The pool to draw the hash table from.</param>
    /// <param name="specificOrdinalsToIndex">
    /// The only ordinals to index, or <see langword="null"/> to index every populated record. An index
    /// restricted this way cannot listen for delta updates.
    /// </param>
    /// <exception cref="FieldPathException">
    /// One of the key's field paths is ill-formed. A path that merely names a type absent from the state
    /// leaves the index unpopulated instead.
    /// </exception>
    public HollowPrimaryKeyIndex(
        HollowReadStateEngine stateEngine,
        PrimaryKey primaryKey,
        IArraySegmentRecycler? memoryRecycler = null,
        BitSet? specificOrdinalsToIndex = null)
    {
        ArgumentNullException.ThrowIfNull(stateEngine);
        ArgumentNullException.ThrowIfNull(primaryKey);

        PrimaryKey = primaryKey;
        _memoryRecycler = memoryRecycler ?? WastefulRecycler.DefaultInstance;
        _specificOrdinalsToIndex = specificOrdinalsToIndex;
        _fieldPathIndexes = new int[primaryKey.FieldCount][];
        _fieldTypes = new FieldType[primaryKey.FieldCount];

        _typeState = stateEngine.GetTypeState(primaryKey.Type) as HollowObjectTypeReadState;
        if (_typeState is null)
        {
            // The type is absent from this state; the index stays empty rather than failing, matching
            // Java, which logs and returns.
            return;
        }

        try
        {
            for (int i = 0; i < primaryKey.FieldCount; i++)
            {
                _fieldPathIndexes[i] = primaryKey.GetFieldPathIndex(stateEngine, i);
                _fieldTypes[i] = primaryKey.GetFieldType(stateEngine, i);
            }
        }
        catch (FieldPathException e) when (e.Error == FieldPathError.NotBindable)
        {
            // A path naming a type this state does not hold leaves the index unpopulated, which reads
            // as "no match" rather than throwing at every query.
            return;
        }

        _keyDeriver = new HollowPrimaryKeyValueDeriver(_typeState, _fieldPathIndexes, _fieldTypes);
        Reindex();
    }

    /// <summary>The key this index is built on.</summary>
    public PrimaryKey PrimaryKey { get; }

    /// <summary>The read state being indexed, or <see langword="null"/> when the type is absent.</summary>
    public HollowObjectTypeReadState? TypeState => _typeState;

    /// <summary>The type of each key field.</summary>
    public IReadOnlyList<FieldType> FieldTypes => _fieldTypes;

    /// <summary>Whether the index found a type to index and bound every field path.</summary>
    public bool IsInitialized => _hashTable is not null;

    /// <summary>
    /// Whether delta updates rebuild the hash table incrementally rather than from scratch.
    /// </summary>
    /// <remarks>
    /// Off by default, as in Java: an incremental rebuild that goes wrong corrupts the index silently,
    /// and queries then return no match rather than failing.
    /// </remarks>
    public bool AllowDeltaUpdate { get; set; }

    /// <summary>An approximation of the memory the hash table occupies, in bytes.</summary>
    public long ApproxHeapFootprintInBytes => _hashTable?.Table.ApproxHeapFootprintInBytes ?? 0;

    /// <summary>
    /// Keeps this index up to date as deltas are applied to the indexed state.
    /// </summary>
    /// <remarks>
    /// Call this before the first delta after creating the index, and <see cref="Dispose"/> when
    /// finished with it — a listening index is referenced by the type state and will not be collected
    /// otherwise. Snapshot transitions are not tracked: build a new index instead.
    /// </remarks>
    /// <exception cref="InvalidOperationException">This index covers only specific ordinals.</exception>
    public void ListenForDeltaUpdates()
    {
        if (_typeState is null)
        {
            return;
        }

        if (_specificOrdinalsToIndex is not null)
        {
            throw new InvalidOperationException(
                "cannot listen for delta updates when indexing only specific ordinals");
        }

        _typeState.AddListener(this);
    }

    /// <summary>
    /// Stops keeping this index up to date as deltas are applied.
    /// </summary>
    public void DetachFromDeltaUpdates() => _typeState?.RemoveListener(this);

    /// <summary>
    /// Finds the record whose key is <paramref name="keys"/>.
    /// </summary>
    /// <returns>
    /// The matching ordinal, or <see cref="HollowConstants.OrdinalNone"/> when there is no such record.
    /// </returns>
    /// <exception cref="InvalidOperationException">This index was never populated.</exception>
    public int GetMatchingOrdinal(params object?[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        HashTable hashTable = _hashTable
            ?? throw new InvalidOperationException($"index {PrimaryKey} was not initialized");

        if (_fieldPathIndexes.Length != keys.Length || hashTable.BitsPerElement == 0)
        {
            return HollowConstants.OrdinalNone;
        }

        int hashCode = 0;
        for (int i = 0; i < keys.Length; i++)
        {
            hashCode ^= KeyHashCode(keys[i], i);
        }

        int ordinal;

        // The table is replaced wholesale by a delta update, so a probe that spans one re-runs against
        // the new table rather than reading a mix of the two.
        do
        {
            hashTable = _hashTable!;

            long bucket = hashCode & hashTable.HashMask;
            ordinal = ReadOrdinal(hashTable, bucket);

            while (ordinal != HollowConstants.OrdinalNone)
            {
                if (_keyDeriver!.KeyMatches(ordinal, keys))
                {
                    break;
                }

                bucket = (bucket + 1) & hashTable.HashMask;
                ordinal = ReadOrdinal(hashTable, bucket);
            }
        }
        while (!ReferenceEquals(_hashTable, hashTable));

        return ordinal;
    }

    /// <summary>
    /// Reads the key of <paramref name="ordinal"/>'s record as boxed values, one per key field.
    /// </summary>
    /// <exception cref="InvalidOperationException">This index was never populated.</exception>
    public object?[] GetRecordKey(int ordinal) =>
        _keyDeriver?.GetRecordKey(ordinal)
        ?? throw new InvalidOperationException($"index {PrimaryKey} was not initialized");

    /// <summary>
    /// Whether two or more records share a key.
    /// </summary>
    public bool ContainsDuplicates() => GetDuplicateKeys().Count != 0;

    /// <summary>
    /// The keys held by two or more records.
    /// </summary>
    public IReadOnlyList<object?[]> GetDuplicateKeys()
    {
        lock (_lock)
        {
            HashTable? hashTable = _hashTable;
            if (hashTable is null || hashTable.BitsPerElement == 0)
            {
                return [];
            }

            List<object?[]> duplicateKeys = [];

            for (long i = 0; i < hashTable.Size; i++)
            {
                int ordinal = ReadOrdinal(hashTable, i);
                if (ordinal == HollowConstants.OrdinalNone)
                {
                    continue;
                }

                // A duplicate always lands in the run of occupied buckets following its twin, because
                // both hash to the same bucket and probe linearly from there.
                long compareBucket = (i + 1) & hashTable.HashMask;
                int compareOrdinal = ReadOrdinal(hashTable, compareBucket);

                while (compareOrdinal != HollowConstants.OrdinalNone)
                {
                    if (RecordsHaveEqualKeys(ordinal, compareOrdinal))
                    {
                        duplicateKeys.Add(_keyDeriver!.GetRecordKey(ordinal));
                    }

                    compareBucket = (compareBucket + 1) & hashTable.HashMask;
                    compareOrdinal = ReadOrdinal(hashTable, compareBucket);
                }
            }

            return duplicateKeys;
        }
    }

    /// <summary>
    /// The keys held by two or more records, with the number of records sharing each, up to
    /// <paramref name="maxDuplicateKeys"/> of them.
    /// </summary>
    public IReadOnlyList<DuplicateKeyInfo> GetDuplicateKeys(int maxDuplicateKeys)
    {
        lock (_lock)
        {
            HashTable? hashTable = _hashTable;
            if (hashTable is null || hashTable.BitsPerElement == 0 || maxDuplicateKeys <= 0)
            {
                return [];
            }

            BitSet counted = new();
            List<DuplicateKeyInfo> duplicateKeys = [];

            for (long i = 0; i < hashTable.Size && duplicateKeys.Count < maxDuplicateKeys; i++)
            {
                int ordinal = ReadOrdinal(hashTable, i);
                if (ordinal == HollowConstants.OrdinalNone || counted.Get(ordinal))
                {
                    continue;
                }

                long count = 1;
                counted.Set(ordinal);

                long compareBucket = (i + 1) & hashTable.HashMask;
                int compareOrdinal = ReadOrdinal(hashTable, compareBucket);

                while (compareOrdinal != HollowConstants.OrdinalNone)
                {
                    if (RecordsHaveEqualKeys(ordinal, compareOrdinal))
                    {
                        count++;
                        counted.Set(compareOrdinal);
                    }

                    compareBucket = (compareBucket + 1) & hashTable.HashMask;
                    compareOrdinal = ReadOrdinal(hashTable, compareBucket);
                }

                if (count > 1)
                {
                    duplicateKeys.Add(new DuplicateKeyInfo(_keyDeriver!.GetRecordKey(ordinal), count));
                }
            }

            return duplicateKeys;
        }
    }

    /// <inheritdoc />
    void IHollowTypeStateListener.BeginUpdate()
    {
        // The index is rebuilt from the populated ordinals once the transition has finished.
    }

    /// <inheritdoc />
    void IHollowTypeStateListener.AddedOrdinal(int ordinal)
    {
        // As above.
    }

    /// <inheritdoc />
    void IHollowTypeStateListener.RemovedOrdinal(int ordinal)
    {
        // As above.
    }

    /// <inheritdoc />
    void IHollowTypeStateListener.EndUpdate()
    {
        lock (_lock)
        {
            if (_hashTable is not { } hashTable)
            {
                return;
            }

            BitSet ordinals = _typeState!.PopulatedOrdinals;

            long hashTableSize = HashCodes.IndexHashTableSize(ordinals.Cardinality());
            int bitsPerElement = 32 - BitOperations.LeadingZeroCount((uint)(_typeState.MaxOrdinal + 1));

            if (AllowDeltaUpdate
                && hashTableSize == hashTable.Size
                && bitsPerElement == hashTable.BitsPerElement
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

    /// <summary>
    /// Returns the hash table's storage to the recycler and stops listening for delta updates.
    /// </summary>
    public void Dispose()
    {
        DetachFromDeltaUpdates();

        lock (_lock)
        {
            _hashTable?.Table.Destroy(_memoryRecycler);
            _hashTable = null;
        }
    }

    private static PrimaryKey ResolveKey(HollowReadStateEngine stateEngine, string type, string[] fieldPaths)
    {
        ArgumentNullException.ThrowIfNull(stateEngine);

        return PrimaryKey.Create(stateEngine, type, fieldPaths)
            ?? throw new ArgumentException(
                $"no field paths were given and type {type} declares no primary key", nameof(fieldPaths));
    }

    private static int ReadOrdinal(HashTable hashTable, long bucket) =>
        (int)hashTable.Table.GetElementValue(bucket * hashTable.BitsPerElement, hashTable.BitsPerElement) - 1;

    /// <summary>
    /// Rebuilds the whole hash table from the ordinals currently populated.
    /// </summary>
    private void Reindex()
    {
        lock (_lock)
        {
            _hashTable?.Table.Destroy(_memoryRecycler);

            BitSet ordinals = _specificOrdinalsToIndex ?? _typeState!.PopulatedOrdinals;

            long hashTableSize = HashCodes.IndexHashTableSize(ordinals.Cardinality());
            int bitsPerElement = 32 - BitOperations.LeadingZeroCount((uint)(_typeState!.MaxOrdinal + 1));

            FixedLengthElementArray table = new(_memoryRecycler, hashTableSize * bitsPerElement);
            long hashMask = hashTableSize - 1;

            // Zero means "empty", so an ordinal is stored one higher than it is.
            foreach (int ordinal in ordinals.EnumerateSetBits())
            {
                long bucket = RecordHash(ordinal) & hashMask;

                while (table.GetElementValue(bucket * bitsPerElement, bitsPerElement) != 0)
                {
                    bucket = (bucket + 1) & hashMask;
                }

                table.SetElementValue(bucket * bitsPerElement, bitsPerElement, ordinal + 1);
            }

            _hashTable = new HashTable(table, hashTableSize, hashMask, bitsPerElement);
            _memoryRecycler.Swap();
        }
    }

    /// <summary>
    /// Rebuilds the hash table by removing only the ordinals the transition dropped and adding only
    /// those it introduced, which is cheaper than a full rebuild when little changed.
    /// </summary>
    /// <exception cref="OrdinalNotFoundException">
    /// An ordinal that should have been in the table was not, which means the table no longer reflects
    /// the data.
    /// </exception>
    private void DeltaUpdate(long hashTableSize, int bitsPerElement)
    {
        HashTable hashTable = _hashTable!;
        hashTable.Table.Destroy(_memoryRecycler);

        BitSet previousOrdinals = _typeState!.PreviousOrdinals;
        BitSet ordinals = _typeState.PopulatedOrdinals;

        long totalBits = hashTableSize * bitsPerElement;
        FixedLengthElementArray table = new(_memoryRecycler, totalBits);
        table.CopyBits(hashTable.Table, 0, 0, totalBits);

        long hashMask = hashTableSize - 1;

        foreach (int previousOrdinal in previousOrdinals.EnumerateSetBits())
        {
            if (ordinals.Get(previousOrdinal))
            {
                continue;
            }

            long bucket = FindOrdinalBucket(bitsPerElement, table, RecordHash(previousOrdinal), hashMask, previousOrdinal);

            table.ClearElementValue(bucket * bitsPerElement, bitsPerElement);

            // Clearing a bucket breaks the probe run through it, so every following entry that hashed
            // at or before the hole has to move back into it.
            long emptyBucket = bucket;
            bucket = (bucket + 1) & hashMask;
            int moveOrdinal = (int)table.GetElementValue(bucket * bitsPerElement, bitsPerElement) - 1;

            while (moveOrdinal != HollowConstants.OrdinalNone)
            {
                long naturalBucket = RecordHash(moveOrdinal) & hashMask;

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

            long bucket = RecordHash(ordinal) & hashMask;

            while (table.GetElementValue(bucket * bitsPerElement, bitsPerElement) != 0)
            {
                bucket = (bucket + 1) & hashMask;
            }

            table.SetElementValue(bucket * bitsPerElement, bitsPerElement, ordinal + 1);
        }

        _hashTable = new HashTable(table, hashTableSize, hashMask, bitsPerElement);
        _memoryRecycler.Swap();
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
        BitSet previousOrdinals = _typeState!.PreviousOrdinals;
        BitSet ordinals = _typeState.PopulatedOrdinals;

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

    private int RecordHash(int ordinal)
    {
        int hashCode = 0;
        for (int i = 0; i < _fieldPathIndexes.Length; i++)
        {
            hashCode ^= FieldHash(ordinal, i);
        }

        return hashCode;
    }

    /// <exception cref="InvalidOperationException">A key field traverses a null reference.</exception>
    private int FieldHash(int ordinal, int fieldIndex)
    {
        HollowObjectTypeReadState typeState = _typeState!;
        int[] path = _fieldPathIndexes[fieldIndex];
        int rootOrdinal = ordinal;

        for (int i = 0; i < path.Length - 1; i++)
        {
            ordinal = typeState.ReadOrdinal(ordinal, path[i]);
            typeState = (HollowObjectTypeReadState)typeState.Schema.GetReferencedTypeState(path[i])!;
        }

        if (ordinal == HollowConstants.OrdinalNone)
        {
            throw new InvalidOperationException(
                $"cannot hash null primary-key field \"{PrimaryKey.GetFieldPath(fieldIndex)}\" in type "
                + $"{PrimaryKey.Type} at ordinal {rootOrdinal}");
        }

        int hashCode = HollowReadFieldUtils.FieldHashCode(typeState, ordinal, path[^1]);

        // Variable-length fields are already hashed with the mixing function; the fixed-length ones are
        // raw values and need it applied.
        return _fieldTypes[fieldIndex] is FieldType.String or FieldType.Bytes
            ? hashCode
            : HashCodes.HashInt(hashCode);
    }

    private bool RecordsHaveEqualKeys(int ordinal1, int ordinal2)
    {
        for (int i = 0; i < _fieldPathIndexes.Length; i++)
        {
            if (!FieldsAreEqual(ordinal1, ordinal2, i))
            {
                return false;
            }
        }

        return true;
    }

    private bool FieldsAreEqual(int ordinal1, int ordinal2, int fieldIndex)
    {
        HollowObjectTypeReadState typeState = _typeState!;
        int[] path = _fieldPathIndexes[fieldIndex];

        for (int i = 0; i < path.Length - 1; i++)
        {
            ordinal1 = typeState.ReadOrdinal(ordinal1, path[i]);
            ordinal2 = typeState.ReadOrdinal(ordinal2, path[i]);
            typeState = (HollowObjectTypeReadState)typeState.Schema.GetReferencedTypeState(path[i])!;
        }

        return _fieldTypes[fieldIndex] == FieldType.Reference
            ? typeState.ReadOrdinal(ordinal1, path[^1]) == typeState.ReadOrdinal(ordinal2, path[^1])
            : HollowReadFieldUtils.FieldsAreEqual(typeState, ordinal1, path[^1], typeState, ordinal2, path[^1]);
    }

    /// <exception cref="ArgumentException">The field type cannot be hashed.</exception>
    private int KeyHashCode(object? key, int fieldIndex)
    {
        FieldType fieldType = _fieldTypes[fieldIndex];

        return fieldType switch
        {
            FieldType.Boolean => HashCodes.HashInt(HollowReadFieldUtils.BooleanHashCode((bool?)key)),
            FieldType.Double => HashCodes.HashInt(HollowReadFieldUtils.DoubleHashCode((double)key!)),
            FieldType.Float => HashCodes.HashInt(HollowReadFieldUtils.FloatHashCode((float)key!)),
            FieldType.Int => HashCodes.HashInt(HollowReadFieldUtils.IntHashCode((int)key!)),
            FieldType.Long => HashCodes.HashInt(HollowReadFieldUtils.LongHashCode((long)key!)),
            FieldType.Reference => HashCodes.HashInt((int)key!),
            FieldType.Bytes => HashCodes.Compute((byte[])key!),
            FieldType.String => HashCodes.Compute((string?)key),
            FieldType.Decimal => HashCodes.HashInt(HollowReadFieldUtils.DecimalHashCode((decimal?)key)),
            _ => throw new ArgumentException($"cannot hash a {fieldType} key field", nameof(fieldIndex)),
        };
    }

    /// <summary>
    /// A key held by more than one record, with the number of records holding it.
    /// </summary>
    public sealed class DuplicateKeyInfo(object?[] key, long count)
    {
        /// <summary>The shared key.</summary>
        public IReadOnlyList<object?> Key { get; } = key;

        /// <summary>How many records hold it.</summary>
        public long Count { get; } = count;

        /// <inheritdoc />
        public override string ToString() => $"[{string.Join(", ", Key)}] (count={Count})";
    }

    /// <summary>
    /// Thrown when an incremental delta update cannot find an ordinal the table should contain.
    /// </summary>
    private sealed class OrdinalNotFoundException(string message) : InvalidOperationException(message);

    /// <summary>
    /// One immutable generation of the hash table. Replaced wholesale rather than mutated, so a query
    /// running concurrently with an update sees one generation or the other.
    /// </summary>
    private sealed class HashTable(
        FixedLengthElementArray table, long size, long hashMask, int bitsPerElement)
    {
        internal FixedLengthElementArray Table { get; } = table;

        internal long Size { get; } = size;

        internal long HashMask { get; } = hashMask;

        internal int BitsPerElement { get; } = bitsPerElement;
    }
}
