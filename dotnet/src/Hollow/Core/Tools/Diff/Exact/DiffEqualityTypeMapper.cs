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

namespace Hollow.Core.Tools.Diff.Exact;

/// <summary>
/// Finds every pair of exactly equal records of one type between two states.
/// </summary>
/// <remarks>
/// <para>
/// The shape is a hash join: hash every <c>to</c> record by its contents, then look each <c>from</c>
/// record up and compare in full whatever collides. What "by its contents" means is the part each
/// record kind supplies, and for a reference field it means the identity the referenced type's map
/// already worked out — which is why the maps are built leaf-first.
/// </para>
/// <para>
/// Java shards both passes across a thread pool. This runs them on one thread, as the rest of this
/// port does with Java's <c>SimultaneousExecutor</c>. The matches found are the same either way, and
/// the order they are found in becomes deterministic rather than depending on which thread reached a
/// bucket first.
/// </para>
/// </remarks>
/// <param name="fromState">The type as the <c>from</c> state holds it.</param>
/// <param name="toState">The type as the <c>to</c> state holds it.</param>
/// <param name="oneToOne">
/// Whether a <c>to</c> record may match only one <c>from</c> record. The diff wants every match; a
/// caller pairing records up for display wants each used once.
/// </param>
public abstract class DiffEqualityTypeMapper(
    HollowTypeReadState fromState, HollowTypeReadState toState, bool oneToOne)
{
    /// <summary>The type as the <c>from</c> state holds it.</summary>
    protected HollowTypeReadState FromState { get; } = fromState;

    /// <summary>The type as the <c>to</c> state holds it.</summary>
    protected HollowTypeReadState ToState { get; } = toState;

    /// <summary>
    /// Whether a record of this type can differ by a field the other state's schema does not have.
    /// </summary>
    /// <remarks>
    /// Two records can be equal on every field the schemas share and still differ, if one schema has a
    /// field the other does not. Where that is possible the diff has to walk the pair anyway rather
    /// than trusting the equality map.
    /// </remarks>
    public abstract bool RequiresTraversalForMissingFields { get; }

    /// <summary>Every pair of exactly equal records of this type.</summary>
    public DiffEqualOrdinalMap MapEqualObjects() => MapMatchingFromOrdinals(HashToOrdinals());

    /// <summary>A hash of what the record at <paramref name="ordinal"/> holds, on the <c>from</c> side.</summary>
    /// <returns>
    /// <c>-1</c> when the record references something that matched nothing, which means it cannot
    /// itself match and need not be hashed.
    /// </returns>
    protected abstract int FromRecordHashCode(int ordinal);

    /// <summary>The same on the <c>to</c> side.</summary>
    protected abstract int ToRecordHashCode(int ordinal);

    /// <summary>Whether two records are equal in full, once their hashes have collided.</summary>
    protected abstract bool RecordsAreEqual(int fromOrdinal, int toOrdinal);

    /// <summary>
    /// Every <c>to</c> ordinal, in an open-addressed table keyed by what the record holds.
    /// </summary>
    private int[] HashToOrdinals()
    {
        BitSet toPopulatedOrdinals = ToState.PopulatedOrdinals;
        int cardinality = toPopulatedOrdinals.Cardinality();

        int[] hashedToOrdinals = new int[HashTableSize(cardinality)];
        Array.Fill(hashedToOrdinals, HollowConstants.OrdinalNone);

        for (int ordinal = toPopulatedOrdinals.NextSetBit(0);
            ordinal != HollowConstants.OrdinalNone;
            ordinal = toPopulatedOrdinals.NextSetBit(ordinal + 1))
        {
            int hashCode = ToRecordHashCode(ordinal);

            if (hashCode == -1)
            {
                continue;
            }

            int bucket = hashCode & (hashedToOrdinals.Length - 1);

            while (hashedToOrdinals[bucket] != HollowConstants.OrdinalNone)
            {
                bucket = (bucket + 1) & (hashedToOrdinals.Length - 1);
            }

            hashedToOrdinals[bucket] = ordinal;
        }

        return hashedToOrdinals;
    }

    /// <summary>
    /// Looks every <c>from</c> record up in <paramref name="hashedToOrdinals"/> and records what it
    /// equals.
    /// </summary>
    private DiffEqualOrdinalMap MapMatchingFromOrdinals(int[] hashedToOrdinals)
    {
        BitSet fromPopulatedOrdinals = FromState.PopulatedOrdinals;
        LongList matchPairResults = new();

        for (int ordinal = fromPopulatedOrdinals.NextSetBit(0);
            ordinal != HollowConstants.OrdinalNone;
            ordinal = fromPopulatedOrdinals.NextSetBit(ordinal + 1))
        {
            int hashCode = FromRecordHashCode(ordinal);

            if (hashCode == -1)
            {
                continue;
            }

            int bucket = hashCode & (hashedToOrdinals.Length - 1);

            // Every colliding record is compared, not just the first: a record can equal several.
            while (hashedToOrdinals[bucket] != HollowConstants.OrdinalNone)
            {
                if (RecordsAreEqual(ordinal, hashedToOrdinals[bucket]))
                {
                    matchPairResults.Add(((long)ordinal << 32) | (uint)hashedToOrdinals[bucket]);
                }

                bucket = (bucket + 1) & (hashedToOrdinals.Length - 1);
            }
        }

        DiffEqualOrdinalMap ordinalMap = new(matchPairResults.Count);
        BitSet? alreadyMapped = oneToOne ? new BitSet(ToState.MaxOrdinal + 1) : null;
        IntList toOrdinals = new();

        // The pairs arrive grouped by from-ordinal, because they were found in ordinal order.
        int position = 0;

        while (position < matchPairResults.Count)
        {
            int fromOrdinal = (int)(matchPairResults.Get(position) >> 32);

            toOrdinals.Clear();

            while (position < matchPairResults.Count
                && (int)(matchPairResults.Get(position) >> 32) == fromOrdinal)
            {
                toOrdinals.Add((int)matchPairResults.Get(position));
                position++;
            }

            if (alreadyMapped is null)
            {
                ordinalMap.PutEqualOrdinals(fromOrdinal, toOrdinals);

                continue;
            }

            for (int i = 0; i < toOrdinals.Count; i++)
            {
                if (!alreadyMapped.Get(toOrdinals.Get(i)))
                {
                    alreadyMapped.Set(toOrdinals.Get(i));
                    ordinalMap.PutEqualOrdinal(fromOrdinal, toOrdinals.Get(i));

                    break;
                }
            }
        }

        return ordinalMap;
    }

    private static int HashTableSize(int cardinality) =>
        cardinality <= 0 ? 1 : (int)BitOperations.RoundUpToPowerOf2((uint)cardinality * 2);
}
