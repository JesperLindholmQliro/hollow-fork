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

using Hollow.Core.Index;
using Hollow.Core.Index.Key;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Schema;
using Hollow.Core.Util;

namespace Hollow.Core.Read.Engine;

/// <summary>
/// One record that a cycle replaced, as the pair of ordinals it had before and after.
/// </summary>
/// <param name="FromOrdinal">Where the record was in the previous cycle.</param>
/// <param name="ToOrdinal">Where the record is now.</param>
public readonly record struct UpdatedRecord(int FromOrdinal, int ToOrdinal);

/// <summary>
/// What a cycle did to one type's records: which were added, which removed, and which replaced.
/// </summary>
/// <remarks>
/// <para>
/// A record is only ever added or removed as far as the blob format is concerned — an ordinal holds one
/// value forever. What a caller usually means by "updated" is a record that went away and came back
/// with a different value under the same primary key, so telling the three apart needs the key.
/// </para>
/// <para>
/// Java computes this inside <c>AbstractHollowDataAccessor</c>, which also serves as the base of the
/// record-collection classes in <c>api.consumer.data</c>. Those are not ported, so the computation
/// stands on its own here rather than being buried in a base class — which is also what lets a caller
/// use it without materialising a record per change.
/// </para>
/// </remarks>
public sealed class RecordChangeSet
{
    private RecordChangeSet(
        string typeName,
        PrimaryKey primaryKey,
        BitSet added,
        BitSet removed,
        IReadOnlyList<UpdatedRecord> updated)
    {
        TypeName = typeName;
        PrimaryKey = primaryKey;
        Added = added;
        Removed = removed;
        Updated = updated;
    }

    /// <summary>The type whose changes these are.</summary>
    public string TypeName { get; }

    /// <summary>The key the three sets were told apart by.</summary>
    public PrimaryKey PrimaryKey { get; }

    /// <summary>
    /// The ordinals of records this cycle added, excluding those that replaced a record.
    /// </summary>
    public BitSet Added { get; }

    /// <summary>
    /// The ordinals of records this cycle removed, excluding those that were replaced.
    /// </summary>
    public BitSet Removed { get; }

    /// <summary>The records this cycle replaced, as before-and-after ordinal pairs.</summary>
    public IReadOnlyList<UpdatedRecord> Updated { get; }

    /// <summary>
    /// Whether there was a previous cycle to compare against at all.
    /// </summary>
    /// <remarks>
    /// <see langword="false"/> after a snapshot load, which carries no memory of what came before — so
    /// everything would otherwise look added.
    /// </remarks>
    public bool HasPriorState { get; private init; }

    /// <summary>
    /// Works out what <paramref name="stateEngine"/>'s last cycle did to <paramref name="typeName"/>.
    /// </summary>
    /// <param name="stateEngine">The state to inspect.</param>
    /// <param name="typeName">The type to inspect.</param>
    /// <param name="primaryKey">
    /// The key to tell a replacement from an add and a remove, or <see langword="null"/> to use the one
    /// the type declares.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The type is absent, is not an object type, or has no primary key to match on.
    /// </exception>
    public static RecordChangeSet Compute(
        HollowReadStateEngine stateEngine, string typeName, PrimaryKey? primaryKey = null)
    {
        ArgumentNullException.ThrowIfNull(stateEngine);

        if (stateEngine.GetTypeState(typeName) is not HollowObjectTypeReadState typeState)
        {
            throw new ArgumentException(
                $"{typeName} is not an object type of this dataset, so its records cannot be matched by key",
                nameof(typeName));
        }

        PrimaryKey key = primaryKey
            ?? ((HollowObjectSchema)typeState.Schema).PrimaryKey
            ?? throw new ArgumentException(
                $"{typeName} declares no primary key, so an added record cannot be matched to the one it "
                + "replaced",
                nameof(typeName));

        BitSet current = typeState.PopulatedOrdinals;
        BitSet previous = typeState.PreviousOrdinals;

        BitSet added = current.Clone();
        added.AndNot(previous);

        BitSet removed = previous.Clone();
        removed.AndNot(current);

        List<UpdatedRecord> updated = [];

        // Indexing only the removals is what makes the match mean "replaced": a key found there
        // belonged to a record that went away in this very cycle.
        using HollowPrimaryKeyIndex removals = new(
            stateEngine, key, stateEngine.MemoryRecycler, removed.Clone());

        for (int ordinal = added.NextSetBit(0);
            ordinal != HollowConstants.OrdinalNone;
            ordinal = added.NextSetBit(ordinal + 1))
        {
            int replaced = removals.GetMatchingOrdinal(removals.GetRecordKey(ordinal));

            if (replaced == HollowConstants.OrdinalNone)
            {
                continue;
            }

            updated.Add(new UpdatedRecord(replaced, ordinal));

            // It is one change, not two, so it leaves both of the other sets.
            added.Clear(ordinal);
            removed.Clear(replaced);
        }

        return new RecordChangeSet(typeName, key, added, removed, updated)
        {
            HasPriorState = previous.Cardinality() > 0,
        };
    }
}
