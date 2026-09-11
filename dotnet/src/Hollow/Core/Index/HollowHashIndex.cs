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

using Hollow.Core.Memory.Encoding;
using Hollow.Core.Read;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Schema;

namespace Hollow.Core.Index;

/// <summary>
/// Indexes a type by fields that are not a unique key, so one key may match many records and one record
/// may be reached by many keys.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="HollowPrimaryKeyIndex"/>, the field paths here may cross collections: a path such
/// as <c>actors.element.actorId</c> traverses a list or set field, its elements, and each element's own
/// field. A record with three actors is therefore reachable by three different keys.
/// </para>
/// <para>
/// The query also selects at a path, which may itself cross a collection, so a query returns the
/// records that path reaches rather than the records the paths started from. Selecting at <c>""</c>
/// returns the records of the indexed type itself.
/// </para>
/// </remarks>
public sealed class HollowHashIndex : IHollowTypeStateListener
{
    private readonly IHollowDataAccess _dataAccess;
    private readonly IHollowTypeDataAccess? _typeState;
    private readonly string[] _matchFields;

    private volatile HashIndexState? _hashState;

    /// <summary>
    /// Indexes <paramref name="type"/>, selecting at <paramref name="selectField"/> and matching on
    /// <paramref name="matchFields"/>.
    /// </summary>
    /// <param name="dataAccess">The state to index.</param>
    /// <param name="type">The type the paths start from.</param>
    /// <param name="selectField">
    /// The path whose records a query returns, or <c>""</c> to return the records of
    /// <paramref name="type"/>.
    /// </param>
    /// <param name="matchFields">The paths a query matches on.</param>
    /// <exception cref="FieldPathException">
    /// One of the paths is ill-formed. A path that merely names a type absent from the state leaves the
    /// index unpopulated instead.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="matchFields"/> is empty. An index that matches on nothing cannot be built: the
    /// key would be zero bits wide, and the tables use a bit of the key to tell an occupied bucket from
    /// an empty one.
    /// </exception>
    public HollowHashIndex(
        IHollowDataAccess dataAccess, string type, string selectField, params string[] matchFields)
    {
        ArgumentNullException.ThrowIfNull(dataAccess);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(selectField);
        ArgumentNullException.ThrowIfNull(matchFields);

        // Java builds this index without complaint and then returns only some of the matching records,
        // because with a zero-width key every group looks like an empty bucket. Refusing is better.
        if (matchFields.Length == 0)
        {
            throw new ArgumentException(
                "a hash index must match on at least one field; to select every record of a type, "
                + "iterate its populated ordinals instead",
                nameof(matchFields));
        }

        _dataAccess = dataAccess;
        _matchFields = matchFields;

        Type = type;
        SelectField = selectField;

        _typeState = dataAccess.GetTypeDataAccess(type);
        if (_typeState is null)
        {
            // The type is absent from this state; the index stays empty rather than failing.
            return;
        }

        Reindex();
    }

    /// <summary>The type the paths start from.</summary>
    public string Type { get; }

    /// <summary>The path whose records a query returns.</summary>
    public string SelectField { get; }

    /// <summary>The paths a query matches on.</summary>
    public IReadOnlyList<string> MatchFields => _matchFields;

    /// <summary>Whether the index found a type to index and bound every field path.</summary>
    public bool IsInitialized => _hashState is not null;

    /// <summary>An approximation of the memory the index occupies, in bytes.</summary>
    public long ApproxHeapFootprintInBytes =>
        _hashState is { } state
            ? state.MatchHashTable.ApproxHeapFootprintInBytes + state.SelectHashArray.ApproxHeapFootprintInBytes
            : 0;

    /// <summary>
    /// Finds the records matching <paramref name="query"/>, one value per match field.
    /// </summary>
    /// <returns>The matches, or <see langword="null"/> when the query matches nothing.</returns>
    /// <exception cref="InvalidOperationException">This index was never populated.</exception>
    /// <exception cref="ArgumentException">A query value is null, which cannot be matched.</exception>
    public HollowHashIndexResult? FindMatches(params object?[] query)
    {
        ArgumentNullException.ThrowIfNull(query);

        HashIndexState hashState = _hashState
            ?? throw new InvalidOperationException($"{this} was not initialized");

        if (query.Length != _matchFields.Length)
        {
            throw new ArgumentException(
                $"{this} matches on {_matchFields.Length} fields, but {query.Length} were given",
                nameof(query));
        }

        int hashCode = 0;
        for (int i = 0; i < query.Length; i++)
        {
            if (query[i] is null)
            {
                throw new ArgumentException($"querying by null is unsupported; field {i}", nameof(query));
            }

            hashCode ^= HashCodes.HashInt(KeyHashCode(hashState, query[i]!, i));
        }

        HollowHashIndexResult? result;

        // The state is replaced wholesale by a delta update, so a probe that spans one re-runs against
        // the new tables rather than reading a mix of the two.
        do
        {
            result = null;
            hashState = _hashState!;

            long bucket = hashCode & hashState.MatchHashMask;
            long hashBucketBit = bucket * hashState.BitsPerMatchHashEntry;

            while (!BucketIsEmpty(hashState, hashBucketBit))
            {
                if (MatchIsEqual(hashState, hashBucketBit, query))
                {
                    int selectSize = (int)hashState.MatchHashTable.GetElementValue(
                        hashBucketBit + hashState.BitsPerMatchHashKey, hashState.BitsPerSelectTableSize);

                    long selectBucketPointer = hashState.MatchHashTable.GetElementValue(
                        hashBucketBit + hashState.BitsPerMatchHashKey + hashState.BitsPerSelectTableSize,
                        hashState.BitsPerSelectTablePointer);

                    result = new HollowHashIndexResult(hashState, selectBucketPointer, selectSize);
                    break;
                }

                bucket = (bucket + 1) & hashState.MatchHashMask;
                hashBucketBit = bucket * hashState.BitsPerMatchHashEntry;
            }
        }
        while (!ReferenceEquals(hashState, _hashState));

        return result;
    }

    /// <summary>
    /// Keeps this index up to date as deltas are applied to the indexed state.
    /// </summary>
    /// <remarks>
    /// Each delta rebuilds the index from scratch, since a change anywhere along a traversed path can
    /// change which keys reach which records. Call <see cref="DetachFromDeltaUpdates"/> when finished
    /// with the index; a listening index is referenced by the type state and will not be collected
    /// otherwise. Snapshot transitions are not tracked: build a new index instead.
    /// </remarks>
    public void ListenForDeltaUpdates()
    {
        if (_typeState is HollowObjectTypeReadState readState)
        {
            readState.AddListener(this);
        }
    }

    /// <summary>Stops keeping this index up to date as deltas are applied.</summary>
    public void DetachFromDeltaUpdates()
    {
        if (_typeState is HollowObjectTypeReadState readState)
        {
            readState.RemoveListener(this);
        }
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"HollowHashIndex [type={Type}, selectField={SelectField}, "
        + $"matchFields=[{string.Join(", ", _matchFields)}]]";

    /// <inheritdoc />
    void IHollowTypeStateListener.BeginUpdate()
    {
        // The index is rebuilt once the transition has finished.
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
        if (_hashState is not null)
        {
            Reindex();
        }
    }

    private static bool BucketIsEmpty(HashIndexState hashState, long hashBucketBit) =>
        hashState.MatchHashTable.GetElementValue(hashBucketBit, hashState.BitsPerTraverserField[0]) == 0;

    private void Reindex()
    {
        HollowHashIndexBuilder builder;

        try
        {
            builder = new HollowHashIndexBuilder(_dataAccess, Type, SelectField, _matchFields);
        }
        catch (FieldPathException e) when (e.Error == FieldPathError.NotBindable)
        {
            // A path naming a type this state does not hold leaves the index unpopulated, which reads
            // as "not initialized" rather than throwing at every query.
            _hashState = null;
            return;
        }

        builder.BuildIndex();
        _hashState = new HashIndexState(builder);
    }

    /// <exception cref="ArgumentException">The field type cannot be hashed.</exception>
    private static int KeyHashCode(HashIndexState hashState, object key, int fieldIndex)
    {
        FieldType fieldType = hashState.MatchFields[fieldIndex].FieldType;

        return fieldType switch
        {
            FieldType.Boolean => HollowReadFieldUtils.BooleanHashCode((bool)key),
            FieldType.Double => HollowReadFieldUtils.DoubleHashCode((double)key),
            FieldType.Float => HollowReadFieldUtils.FloatHashCode((float)key),
            FieldType.Int => HollowReadFieldUtils.IntHashCode((int)key),
            FieldType.Long => HollowReadFieldUtils.LongHashCode((long)key),
            FieldType.Reference => (int)key,
            FieldType.Bytes => HashCodes.Compute((byte[])key),
            FieldType.String => HashCodes.Compute((string)key),
            FieldType.Decimal => HollowReadFieldUtils.DecimalHashCode((decimal)key),
            _ => throw new ArgumentException($"cannot hash a {fieldType} field", nameof(fieldIndex)),
        };
    }

    /// <summary>
    /// Whether the key stored at <paramref name="hashBucketBit"/> holds exactly the query's values.
    /// </summary>
    private static bool MatchIsEqual(HashIndexState hashState, long hashBucketBit, object?[] query)
    {
        for (int i = 0; i < hashState.MatchFields.Length; i++)
        {
            HollowHashIndexField field = hashState.MatchFields[i];

            int hashOrdinal = (int)hashState.MatchHashTable.GetElementValue(
                hashBucketBit + hashState.OffsetPerTraverserField[field.BaseIteratorFieldIndex],
                hashState.BitsPerTraverserField[field.BaseIteratorFieldIndex]) - 1;

            HollowHashIndexField.FieldPathSegment[] fieldPath = field.SchemaFieldPositionPath;

            // A path with nothing left to walk names a record, so the query value is its ordinal.
            if (fieldPath.Length == 0)
            {
                if (!Equals(query[i], hashOrdinal))
                {
                    return false;
                }

                continue;
            }

            for (int j = 0; j < fieldPath.Length - 1; j++)
            {
                hashOrdinal = fieldPath[j].GetOrdinalForField(hashOrdinal);

                if (hashOrdinal == HollowConstants.OrdinalNone)
                {
                    break;
                }
            }

            HollowHashIndexField.FieldPathSegment last = fieldPath[^1];

            if (hashOrdinal == HollowConstants.OrdinalNone
                || !HollowReadFieldUtils.FieldValueEquals(
                    last.ObjectTypeDataAccess, hashOrdinal, last.SegmentFieldPosition, query[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// One immutable generation of the index's tables. Replaced wholesale rather than mutated, so a
    /// query running concurrently with an update sees one generation or the other.
    /// </summary>
    internal sealed class HashIndexState
    {
        internal HashIndexState(HollowHashIndexBuilder builder)
        {
            MatchHashTable = builder.FinalMatchHashTable;
            SelectHashArray = builder.FinalSelectHashArray;
            MatchFields = builder.MatchFields;
            MatchHashMask = (int)builder.FinalMatchHashMask;
            BitsPerMatchHashKey = builder.BitsPerMatchHashKey;
            BitsPerMatchHashEntry = builder.FinalBitsPerMatchHashEntry;
            BitsPerTraverserField = builder.BitsPerTraverserField;
            OffsetPerTraverserField = builder.OffsetPerTraverserField;
            BitsPerSelectTableSize = builder.FinalBitsPerSelectTableSize;
            BitsPerSelectTablePointer = builder.FinalBitsPerSelectTablePointer;
            BitsPerSelectHashEntry = builder.BitsPerSelectHashEntry;
        }

        internal FixedLengthElementArray MatchHashTable { get; }

        internal FixedLengthElementArray SelectHashArray { get; }

        internal HollowHashIndexField[] MatchFields { get; }

        internal int MatchHashMask { get; }

        internal int BitsPerMatchHashKey { get; }

        internal int BitsPerMatchHashEntry { get; }

        internal int[] BitsPerTraverserField { get; }

        internal int[] OffsetPerTraverserField { get; }

        internal int BitsPerSelectTableSize { get; }

        internal int BitsPerSelectTablePointer { get; }

        internal int BitsPerSelectHashEntry { get; }
    }
}
