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
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Schema;
using Hollow.Core.Util;

namespace Hollow.Core.Index;

/// <summary>
/// Indexes a type by a <see cref="PrimaryKey"/>, binding each step of the key's paths to a type access
/// once, when the index is built.
/// </summary>
/// <remarks>
/// <para>
/// This serves the same purpose as <see cref="HollowPrimaryKeyIndex"/> and answers the same queries.
/// The difference is where the type accesses come from: this index resolves them through the data
/// access it was given and keeps them, whereas the primary key index walks the schema's referenced
/// type states on every lookup.
/// </para>
/// <para>
/// <strong>Port note.</strong> In Java that distinction is what lets this index survive more than two
/// deltas under object longevity, which serves reads of older versions from a live state. Object
/// longevity is not part of this port, so here the difference is narrower: this index works against any
/// <see cref="IHollowDataAccess"/> rather than requiring a <see cref="HollowReadStateEngine"/>, and it
/// does not depend on the schema's mutable type-state wiring. See <c>PORTING.md</c>.
/// </para>
/// </remarks>
public sealed class HollowUniqueKeyIndex : IHollowTypeStateListener, IDisposable, IUniqueKeyRecords
{
    private readonly IHollowObjectTypeDataAccess? _objectTypeDataAccess;
    private readonly HollowHashIndexField[] _fields;
    private readonly UniqueKeyHashTable _hashTable;

    /// <summary>
    /// Indexes <paramref name="type"/> of <paramref name="dataAccess"/> by <paramref name="fieldPaths"/>,
    /// or by the type's declared primary key when none are given.
    /// </summary>
    public HollowUniqueKeyIndex(IHollowDataAccess dataAccess, string type, params string[] fieldPaths)
        : this(dataAccess, ResolveKey(dataAccess, type, fieldPaths))
    {
    }

    /// <summary>
    /// Indexes the type named by <paramref name="primaryKey"/>.
    /// </summary>
    /// <param name="dataAccess">The data to index.</param>
    /// <param name="primaryKey">The key to index by.</param>
    /// <param name="memoryRecycler">The pool to draw the hash table from.</param>
    /// <param name="specificOrdinalsToIndex">
    /// The only ordinals to index, or <see langword="null"/> to index every populated record. An index
    /// restricted this way cannot listen for delta updates.
    /// </param>
    /// <exception cref="FieldPathException">
    /// One of the key's field paths is ill-formed. A path that merely names a type absent from the data
    /// leaves the index unpopulated instead.
    /// </exception>
    public HollowUniqueKeyIndex(
        IHollowDataAccess dataAccess,
        PrimaryKey primaryKey,
        IArraySegmentRecycler? memoryRecycler = null,
        BitSet? specificOrdinalsToIndex = null)
    {
        ArgumentNullException.ThrowIfNull(dataAccess);
        ArgumentNullException.ThrowIfNull(primaryKey);

        PrimaryKey = primaryKey;
        SpecificOrdinalsToIndex = specificOrdinalsToIndex;

        _fields = new HollowHashIndexField[primaryKey.FieldCount];
        _hashTable = new UniqueKeyHashTable(
            this, memoryRecycler ?? WastefulRecycler.DefaultInstance, specificOrdinalsToIndex);

        _objectTypeDataAccess = dataAccess.GetTypeDataAccess(primaryKey.Type) as IHollowObjectTypeDataAccess;
        if (_objectTypeDataAccess is null)
        {
            // The type is absent from this data, or is not an object type; the index stays empty rather
            // than failing.
            return;
        }

        try
        {
            for (int i = 0; i < primaryKey.FieldCount; i++)
            {
                _fields[i] = ResolveField(dataAccess, primaryKey, i);
            }
        }
        catch (FieldPathException e) when (e.Error == FieldPathError.NotBindable)
        {
            // A path naming a type this data does not hold leaves the index unpopulated, which reads as
            // "no match" rather than throwing at every query.
            return;
        }

        _hashTable.Reindex();
    }

    /// <summary>The key this index is built on.</summary>
    public PrimaryKey PrimaryKey { get; }

    /// <summary>
    /// The data access being indexed, or <see langword="null"/> when the type is absent.
    /// </summary>
    public IHollowObjectTypeDataAccess? ObjectTypeDataAccess => _objectTypeDataAccess;

    /// <summary>The type of each key field.</summary>
    public IReadOnlyList<FieldType> FieldTypes => [.. _fields.Select(indexField => indexField.FieldType)];

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
    int IUniqueKeyRecords.MaxOrdinal => _objectTypeDataAccess!.TypeState.MaxOrdinal;

    /// <inheritdoc />
    BitSet IUniqueKeyRecords.PopulatedOrdinals => _objectTypeDataAccess!.TypeState.PopulatedOrdinals;

    /// <inheritdoc />
    BitSet IUniqueKeyRecords.PreviousOrdinals => _objectTypeDataAccess!.TypeState.PreviousOrdinals;

    /// <summary>
    /// Keeps this index up to date as deltas are applied to the indexed state.
    /// </summary>
    /// <remarks>
    /// Call this before the first delta after creating the index, and <see cref="Dispose"/> when
    /// finished with it — a listening index is referenced by the type state and will not be collected
    /// otherwise.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// This index covers only specific ordinals, or was built over something other than a read state.
    /// </exception>
    public void ListenForDeltaUpdates()
    {
        if (_objectTypeDataAccess is null)
        {
            return;
        }

        if (SpecificOrdinalsToIndex is not null)
        {
            throw new InvalidOperationException(
                "cannot listen for delta updates when indexing only specific ordinals");
        }

        if (_objectTypeDataAccess is not HollowObjectTypeReadState readState)
        {
            throw new InvalidOperationException(
                $"cannot listen for delta updates on a {_objectTypeDataAccess.GetType().Name}, which is "
                + "not a read state");
        }

        readState.AddListener(this);
    }

    /// <summary>Stops keeping this index up to date as deltas are applied.</summary>
    public void DetachFromDeltaUpdates()
    {
        if (_objectTypeDataAccess is HollowObjectTypeReadState readState)
        {
            readState.RemoveListener(this);
        }
    }

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

        if (_fields.Length != keys.Length || generation.BitsPerElement == 0)
        {
            return HollowConstants.OrdinalNone;
        }

        int hashCode = 0;
        for (int i = 0; i < keys.Length; i++)
        {
            hashCode ^= KeyHashCode(keys[i], _fields[i].FieldType);
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
                if (KeysAllMatch(ordinal, keys))
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
    public object?[] GetRecordKey(int ordinal)
    {
        object?[] results = new object?[_fields.Length];

        for (int i = 0; i < _fields.Length; i++)
        {
            HollowHashIndexField field = _fields[i];
            int lastPathOrdinal = NavigateToLastRecord(field, ordinal);
            HollowHashIndexField.FieldPathSegment last = field.LastFieldPositionPathElement;

            results[i] = lastPathOrdinal == HollowConstants.OrdinalNone
                ? null
                : HollowReadFieldUtils.FieldValueObject(
                    last.ObjectTypeDataAccess, lastPathOrdinal, last.SegmentFieldPosition);
        }

        return results;
    }

    /// <summary>Whether two or more records share a key.</summary>
    public bool ContainsDuplicates() => GetDuplicateKeys().Count != 0;

    /// <summary>The keys held by two or more records.</summary>
    public IReadOnlyList<object?[]> GetDuplicateKeys() => _hashTable.GetDuplicateKeys();

    /// <summary>
    /// The keys held by two or more records, with the number of records sharing each, up to
    /// <paramref name="maxDuplicateKeys"/> of them.
    /// </summary>
    public IReadOnlyList<HollowPrimaryKeyIndex.DuplicateKeyInfo> GetDuplicateKeys(int maxDuplicateKeys) =>
        _hashTable.GetDuplicateKeys(maxDuplicateKeys);

    /// <inheritdoc />
    int IUniqueKeyRecords.RecordHash(int ordinal)
    {
        int hashCode = 0;
        for (int i = 0; i < _fields.Length; i++)
        {
            hashCode ^= FieldHash(ordinal, i);
        }

        return hashCode;
    }

    /// <inheritdoc />
    bool IUniqueKeyRecords.RecordsHaveEqualKeys(int ordinal1, int ordinal2)
    {
        for (int i = 0; i < _fields.Length; i++)
        {
            if (!FieldsAreEqual(ordinal1, ordinal2, i))
            {
                return false;
            }
        }

        return true;
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

    private static PrimaryKey ResolveKey(IHollowDataAccess dataAccess, string type, string[] fieldPaths)
    {
        ArgumentNullException.ThrowIfNull(dataAccess);

        return PrimaryKey.Create(dataAccess, type, fieldPaths)
            ?? throw new ArgumentException(
                $"no field paths were given and type {type} declares no primary key", nameof(fieldPaths));
    }

    /// <summary>
    /// Resolves one key path into the steps to walk, binding each step's type access now rather than
    /// looking it up through the schema at query time.
    /// </summary>
    /// <exception cref="ArgumentException">The path traverses a value field.</exception>
    private HollowHashIndexField ResolveField(
        IHollowDataAccess dataAccess, PrimaryKey primaryKey, int fieldIndex)
    {
        FieldType fieldType = primaryKey.GetFieldType(dataAccess, fieldIndex);
        int[] fieldPathPositions = primaryKey.GetFieldPathIndex(dataAccess, fieldIndex);

        HollowHashIndexField.FieldPathSegment[] segments =
            new HollowHashIndexField.FieldPathSegment[fieldPathPositions.Length];

        IHollowObjectTypeDataAccess? currentDataAccess = _objectTypeDataAccess;

        for (int i = 0; i < fieldPathPositions.Length; i++)
        {
            if (currentDataAccess is null)
            {
                throw new ArgumentException(
                    $"path {primaryKey.GetFieldPath(fieldIndex)} traverses a value field, which may only "
                    + "be the last element of a path",
                    nameof(primaryKey));
            }

            segments[i] = new HollowHashIndexField.FieldPathSegment(fieldPathPositions[i], currentDataAccess);

            // Resolved through the data access rather than through the schema's referenced type state,
            // which is what keeps this index independent of that mutable wiring.
            string? referencedType = currentDataAccess.Schema.GetReferencedType(fieldPathPositions[i]);

            currentDataAccess = referencedType is null
                ? null
                : dataAccess.GetTypeDataAccess(referencedType) as IHollowObjectTypeDataAccess;
        }

        return new HollowHashIndexField(fieldIndex, segments, currentDataAccess!, fieldType);
    }

    /// <summary>
    /// Follows all but the last step of a key path, returning the record the last step reads from.
    /// </summary>
    private static int NavigateToLastRecord(HollowHashIndexField field, int ordinal)
    {
        HollowHashIndexField.FieldPathSegment[] segments = field.SchemaFieldPositionPath;

        for (int i = 0; i < segments.Length - 1; i++)
        {
            ordinal = segments[i].GetOrdinalForField(ordinal);

            if (ordinal == HollowConstants.OrdinalNone)
            {
                return HollowConstants.OrdinalNone;
            }
        }

        return ordinal;
    }

    /// <exception cref="InvalidOperationException">A key field traverses a null reference.</exception>
    private int FieldHash(int ordinal, int fieldIndex)
    {
        HollowHashIndexField field = _fields[fieldIndex];
        int lastRecordOrdinal = NavigateToLastRecord(field, ordinal);

        if (lastRecordOrdinal == HollowConstants.OrdinalNone)
        {
            throw new InvalidOperationException(
                $"cannot hash null primary-key field \"{PrimaryKey.GetFieldPath(fieldIndex)}\" in type "
                + $"{PrimaryKey.Type} at ordinal {ordinal}");
        }

        HollowHashIndexField.FieldPathSegment last = field.LastFieldPositionPathElement;
        int hashCode = HollowReadFieldUtils.FieldHashCode(
            last.ObjectTypeDataAccess, lastRecordOrdinal, last.SegmentFieldPosition);

        // Variable-length fields are already hashed with the mixing function; the fixed-length ones are
        // raw values and need it applied.
        return field.FieldType is FieldType.String or FieldType.Bytes
            ? hashCode
            : HashCodes.HashInt(hashCode);
    }

    private bool KeysAllMatch(int ordinal, object?[] keys)
    {
        for (int i = 0; i < keys.Length; i++)
        {
            HollowHashIndexField field = _fields[i];
            int lastRecordOrdinal = NavigateToLastRecord(field, ordinal);

            if (lastRecordOrdinal == HollowConstants.OrdinalNone)
            {
                return false;
            }

            HollowHashIndexField.FieldPathSegment last = field.LastFieldPositionPathElement;

            if (!HollowPrimaryKeyValueDeriver.KeyMatches(
                keys[i],
                field.FieldType,
                last.SegmentFieldPosition,
                lastRecordOrdinal,
                last.ObjectTypeDataAccess))
            {
                return false;
            }
        }

        return true;
    }

    private bool FieldsAreEqual(int ordinal1, int ordinal2, int fieldIndex)
    {
        HollowHashIndexField field = _fields[fieldIndex];
        HollowHashIndexField.FieldPathSegment[] segments = field.SchemaFieldPositionPath;

        for (int i = 0; i < segments.Length - 1; i++)
        {
            ordinal1 = segments[i].GetOrdinalForField(ordinal1);
            ordinal2 = segments[i].GetOrdinalForField(ordinal2);
        }

        HollowHashIndexField.FieldPathSegment last = field.LastFieldPositionPathElement;

        return field.FieldType == FieldType.Reference
            ? ordinal1 == ordinal2
            : HollowReadFieldUtils.FieldsAreEqual(
                last.ObjectTypeDataAccess,
                ordinal1,
                last.SegmentFieldPosition,
                last.ObjectTypeDataAccess,
                ordinal2,
                last.SegmentFieldPosition);
    }

    /// <exception cref="ArgumentException">The field type cannot be hashed.</exception>
    private static int KeyHashCode(object? key, FieldType fieldType) =>
        fieldType switch
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
            _ => throw new ArgumentException($"cannot hash a {fieldType} key field", nameof(fieldType)),
        };
}
