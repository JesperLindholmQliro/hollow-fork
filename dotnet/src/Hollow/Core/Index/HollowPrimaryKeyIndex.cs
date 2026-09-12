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
/// This index resolves each step of a key's path through the schema's referenced type states, which
/// are shared mutable state. <see cref="HollowUniqueKeyIndex"/> serves the same purpose but binds its
/// type accesses once, at construction, which is what Java relies on for object longevity.
/// </para>
/// </remarks>
public sealed class HollowPrimaryKeyIndex : IHollowTypeStateListener, IDisposable, IUniqueKeyRecords
{
    private readonly HollowObjectTypeReadState? _typeState;
    private readonly int[][] _fieldPathIndexes;
    private readonly FieldType[] _fieldTypes;
    private readonly HollowPrimaryKeyValueDeriver? _keyDeriver;
    private readonly UniqueKeyHashTable _hashTable;

    /// <summary>
    /// Indexes <paramref name="stateEngine"/> by <paramref name="fieldPaths"/>, which carry the type
    /// they start at.
    /// </summary>
    public HollowPrimaryKeyIndex(HollowReadStateEngine stateEngine, params FieldPath[] fieldPaths)
        : this(stateEngine, new PrimaryKey(fieldPaths))
    {
    }

    /// <inheritdoc cref="HollowPrimaryKeyIndex(HollowReadStateEngine, FieldPath[])" />
    /// <param name="stateEngine">The state to index.</param>
    /// <param name="type">The type whose records are indexed.</param>
    /// <param name="fieldPaths">The key's field paths, written out as text.</param>
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
        SpecificOrdinalsToIndex = specificOrdinalsToIndex;

        _fieldPathIndexes = new int[primaryKey.FieldCount][];
        _fieldTypes = new FieldType[primaryKey.FieldCount];
        _hashTable = new UniqueKeyHashTable(
            this, memoryRecycler ?? WastefulRecycler.DefaultInstance, specificOrdinalsToIndex);

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
        _hashTable.Reindex();
    }

    /// <summary>The key this index is built on.</summary>
    public PrimaryKey PrimaryKey { get; }

    /// <summary>The read state being indexed, or <see langword="null"/> when the type is absent.</summary>
    public HollowObjectTypeReadState? TypeState => _typeState;

    /// <summary>The type of each key field.</summary>
    public IReadOnlyList<FieldType> FieldTypes => _fieldTypes;

    /// <summary>Whether the index found a type to index and bound every field path.</summary>
    public bool IsInitialized => _hashTable.Current is not null;

    /// <summary>
    /// Whether delta updates rebuild the hash table incrementally rather than from scratch.
    /// </summary>
    /// <remarks>
    /// Off by default, as in Java: an incremental rebuild that goes wrong corrupts the index silently,
    /// and queries then return no match rather than failing.
    /// </remarks>
    public bool AllowDeltaUpdate
    {
        get => _hashTable.AllowDeltaUpdate;
        set => _hashTable.AllowDeltaUpdate = value;
    }

    /// <summary>An approximation of the memory the hash table occupies, in bytes.</summary>
    public long ApproxHeapFootprintInBytes => _hashTable.ApproxHeapFootprintInBytes;

    /// <summary>The ordinals this index covers, or null when it covers every populated record.</summary>
    internal BitSet? SpecificOrdinalsToIndex { get; }

    /// <inheritdoc />
    int IUniqueKeyRecords.MaxOrdinal => _typeState!.MaxOrdinal;

    /// <inheritdoc />
    BitSet IUniqueKeyRecords.PopulatedOrdinals => _typeState!.PopulatedOrdinals;

    /// <inheritdoc />
    BitSet IUniqueKeyRecords.PreviousOrdinals => _typeState!.PreviousOrdinals;

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

        if (SpecificOrdinalsToIndex is not null)
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

        UniqueKeyHashTable.Generation generation = _hashTable.Current
            ?? throw new InvalidOperationException($"index {PrimaryKey} was not initialized");

        if (_fieldPathIndexes.Length != keys.Length || generation.BitsPerElement == 0)
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
            generation = _hashTable.Current!;

            long bucket = hashCode & generation.HashMask;
            ordinal = generation.ReadOrdinal(bucket);

            while (ordinal != HollowConstants.OrdinalNone)
            {
                if (_keyDeriver!.KeyMatches(ordinal, keys))
                {
                    break;
                }

                bucket = (bucket + 1) & generation.HashMask;
                ordinal = generation.ReadOrdinal(bucket);
            }
        }
        while (!ReferenceEquals(_hashTable.Current, generation));

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
    public IReadOnlyList<object?[]> GetDuplicateKeys() => _hashTable.GetDuplicateKeys();

    /// <summary>
    /// The keys held by two or more records, with the number of records sharing each, up to
    /// <paramref name="maxDuplicateKeys"/> of them.
    /// </summary>
    public IReadOnlyList<DuplicateKeyInfo> GetDuplicateKeys(int maxDuplicateKeys) =>
        _hashTable.GetDuplicateKeys(maxDuplicateKeys);

    /// <inheritdoc />
    bool IUniqueKeyRecords.RecordsHaveEqualKeys(int ordinal1, int ordinal2)
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

    /// <inheritdoc />
    int IUniqueKeyRecords.RecordHash(int ordinal)
    {
        int hashCode = 0;
        for (int i = 0; i < _fieldPathIndexes.Length; i++)
        {
            hashCode ^= FieldHash(ordinal, i);
        }

        return hashCode;
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
    void IHollowTypeStateListener.EndUpdate() => _hashTable.EndUpdate();

    /// <summary>
    /// Returns the hash table's storage to the recycler and stops listening for delta updates.
    /// </summary>
    public void Dispose()
    {
        DetachFromDeltaUpdates();
        _hashTable.Destroy();
    }

    private static PrimaryKey ResolveKey(HollowReadStateEngine stateEngine, string type, string[] fieldPaths)
    {
        ArgumentNullException.ThrowIfNull(stateEngine);

        return PrimaryKey.Create(stateEngine, type, fieldPaths)
            ?? throw new ArgumentException(
                $"no field paths were given and type {type} declares no primary key", nameof(fieldPaths));
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
                + $"{PrimaryKey.Type} at ordinal {rootOrdinal.Invariant()}");
        }

        int hashCode = HollowReadFieldUtils.FieldHashCode(typeState, ordinal, path[^1]);

        // Variable-length fields are already hashed with the mixing function; the fixed-length ones are
        // raw values and need it applied.
        return _fieldTypes[fieldIndex] is FieldType.String or FieldType.Bytes
            ? hashCode
            : HashCodes.HashInt(hashCode);
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
        public override string ToString() =>
            $"[{InvariantFormatting.JoinInvariant(", ", Key)}] (count={Count.Invariant()})";
    }
}
