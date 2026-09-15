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

using System.Collections;
using Hollow.Core.Index.Key;
using Hollow.Core.Read.Engine;
using Hollow.Core.Util;

namespace Hollow.Api.Consumer.Data;

/// <summary>
/// One record a cycle replaced, as it was before and as it is now.
/// </summary>
/// <typeparam name="T">The record type.</typeparam>
/// <param name="Before">The record as the previous cycle held it.</param>
/// <param name="After">The record as this cycle holds it.</param>
public readonly record struct UpdatedRecord<T>(T Before, T After);

/// <summary>
/// What the last transition did to one type's records, as records rather than as ordinals.
/// </summary>
/// <typeparam name="T">
/// What a record of this type reads back as — a generated record class, or a value for one of the
/// scalar types.
/// </typeparam>
/// <remarks>
/// <para>
/// Named <c>AbstractHollowDataAccessor</c> in Java; .NET does not spell the abstractness into the
/// name. Subclass it and implement <see cref="GetRecord"/>, or let the code generator emit a subclass
/// per keyed type — see <c>HollowCodeGeneratorOptions.GenerateDataAccessors</c>.
/// </para>
/// <para>
/// The work of telling an addition from a replacement is <see cref="RecordChangeSet"/>'s, not this
/// class's. Java does it inside this base class; here it stands on its own, so a caller who only wants
/// the ordinals — a validator counting them, say — need not materialise a record per change.
/// </para>
/// <para>
/// A record is read at the moment it is asked for, and the ordinals are the ones the state held when
/// the change was computed. Reading a <em>removed</em> record therefore works only while the storage
/// behind it is still there, which is what object longevity exists to guarantee; without it, read
/// them before the next transition.
/// </para>
/// </remarks>
public abstract class HollowDataAccessor<T>
{
    private readonly HollowReadStateEngine _stateEngine;
    private readonly PrimaryKey? _declaredKey;

    private RecordChangeSet? _changes;

    /// <summary>Reads the <paramref name="type"/> records <paramref name="consumer"/> holds.</summary>
    /// <exception cref="InvalidOperationException">The consumer holds no data yet.</exception>
    protected HollowDataAccessor(HollowConsumer consumer, string type)
        : this(
            (consumer ?? throw new ArgumentNullException(nameof(consumer))).StateEngine
                ?? throw new InvalidOperationException("the consumer holds no data yet"),
            type)
    {
    }

    /// <summary>
    /// Reads the <paramref name="type"/> records of <paramref name="stateEngine"/>, matching them by
    /// the key the type declares.
    /// </summary>
    protected HollowDataAccessor(HollowReadStateEngine stateEngine, string type)
        : this(stateEngine, type, primaryKey: null)
    {
    }

    /// <summary>
    /// Reads them matching on <paramref name="fieldPaths"/> rather than on the declared key.
    /// </summary>
    protected HollowDataAccessor(HollowReadStateEngine stateEngine, string type, params string[] fieldPaths)
        : this(
            stateEngine,
            type,
            fieldPaths is { Length: > 0 }
                ? new PrimaryKey(type, fieldPaths)
                : throw new ArgumentException("name at least one field path to match on", nameof(fieldPaths)))
    {
    }

    /// <summary>Reads them matching on <paramref name="primaryKey"/>.</summary>
    protected HollowDataAccessor(HollowReadStateEngine stateEngine, string type, PrimaryKey? primaryKey)
    {
        ArgumentNullException.ThrowIfNull(stateEngine);
        ArgumentException.ThrowIfNullOrEmpty(type);

        _stateEngine = stateEngine;
        _declaredKey = primaryKey;

        Type = type;
    }

    /// <summary>The Hollow type being read.</summary>
    public string Type { get; }

    /// <summary>The key a record is recognised across a transition by.</summary>
    public PrimaryKey PrimaryKey => Changes.PrimaryKey;

    /// <summary>
    /// Whether there was a previous cycle to compare against.
    /// </summary>
    /// <remarks>
    /// <see langword="false"/> after a snapshot load, which carries no memory of what came before — so
    /// every record would otherwise read as added.
    /// </remarks>
    public bool HasPriorState => Changes.HasPriorState;

    /// <summary>Whether the change has been worked out yet.</summary>
    public bool IsDataChangeComputed => _changes is not null;

    /// <summary>Every record the type holds now.</summary>
    /// <remarks>Answerable without working the change out, and it does not.</remarks>
    public IReadOnlyCollection<T> AllRecords => new OrdinalRecords(PopulatedOrdinals, GetRecord);

    /// <summary>The records this transition brought in.</summary>
    public IReadOnlyCollection<T> AddedRecords => new OrdinalRecords(Changes.Added, GetRecord);

    /// <summary>The records this transition took away.</summary>
    public IReadOnlyCollection<T> RemovedRecords => new OrdinalRecords(Changes.Removed, GetRecord);

    /// <summary>
    /// The records this transition replaced, each as it was and as it is.
    /// </summary>
    /// <remarks>
    /// A record is only ever added or removed as far as the blob is concerned — an ordinal holds one
    /// value forever. A replacement is a removal and an addition that share a key, which is why a key
    /// is needed to answer this at all.
    /// </remarks>
    public IReadOnlyCollection<UpdatedRecord<T>> UpdatedRecords =>
        [.. Changes.Updated.Select(pair =>
            new UpdatedRecord<T>(GetRecord(pair.FromOrdinal), GetRecord(pair.ToOrdinal)))];

    private RecordChangeSet Changes
    {
        get
        {
            ComputeDataChange();

            return _changes!;
        }
    }

    private BitSet PopulatedOrdinals =>
        _stateEngine.GetTypeState(Type)?.PopulatedOrdinals
        ?? throw new InvalidOperationException($"{Type} is not a type of this dataset");

    /// <summary>
    /// Works the change out now, rather than when something first asks for it.
    /// </summary>
    /// <remarks>
    /// Matching the additions against the removals means building an index over the removals, so this
    /// is worth doing off the thread that is about to want the answer. Calling it twice does the work
    /// once.
    /// </remarks>
    public void ComputeDataChange() =>
        _changes ??= RecordChangeSet.Compute(_stateEngine, Type, _declaredKey);

    /// <summary>The record at <paramref name="ordinal"/>.</summary>
    public abstract T GetRecord(int ordinal);

    /// <summary>
    /// The records at a set of ordinals, read one at a time as they are asked for.
    /// </summary>
    /// <remarks>
    /// Java's <c>HollowRecordCollection</c>, which is <c>core.util</c> there and has no other caller.
    /// Nothing is materialised: a change of a million records costs a bit set until someone enumerates
    /// it.
    /// </remarks>
    private sealed class OrdinalRecords(BitSet ordinals, Func<int, T> getRecord) : IReadOnlyCollection<T>
    {
        public int Count => ordinals.Cardinality();

        public IEnumerator<T> GetEnumerator()
        {
            for (int ordinal = ordinals.NextSetBit(0);
                ordinal != -1;
                ordinal = ordinals.NextSetBit(ordinal + 1))
            {
                yield return getRecord(ordinal);
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
