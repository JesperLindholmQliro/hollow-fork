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
using Hollow.Core.Util;

namespace Hollow.Core.Tools.Diff.Exact;

/// <summary>
/// Which records of one type in the <c>from</c> state are exactly equal to which in the <c>to</c>
/// state.
/// </summary>
/// <remarks>
/// <para>
/// Two records being identical is worth knowing cheaply, because the diff can then skip both of them
/// and everything they reference. This is the table that answers it.
/// </para>
/// <para>
/// A record may equal several on the other side — two different ordinals can hold the same value — so
/// the map also gives each group of equal records one <em>identity</em>: the lowest <c>to</c> ordinal
/// in the group, standing for all of them. Comparing identities is then how a field two levels down is
/// judged equal without reading it.
/// </para>
/// <para>
/// Both directions are open-addressed tables of packed longs, because a diff over a large dataset
/// builds one of these per type and the boxing a dictionary would cost is the whole budget.
/// </para>
/// </remarks>
public sealed class DiffEqualOrdinalMap
{
    /// <summary>The map for a type that is not in both states, which matches nothing.</summary>
    public static readonly DiffEqualOrdinalMap Empty = new(0);

    /// <summary>
    /// Keyed by <c>from</c> ordinal in the low 32 bits. The high bits hold either the single matching
    /// <c>to</c> ordinal, or — when the sign bit is set — where this record's matches start in
    /// <see cref="_pivotedToOrdinalClusters"/>.
    /// </summary>
    private readonly long[] _fromOrdinalsMap;

    /// <summary>Keyed by <c>to</c> ordinal in the low 32 bits, with its group's identity above.</summary>
    private readonly long[] _toOrdinalsIdentityMap;

    /// <summary>
    /// The matches of every record that has more than one, run after run. The last of each run is
    /// marked by its sign bit, so a run needs no length stored beside it.
    /// </summary>
    private readonly IntList _pivotedToOrdinalClusters = new();

    /// <summary>Initialises a map sized for <paramref name="numMatches"/> matched pairs.</summary>
    public DiffEqualOrdinalMap(int numMatches)
    {
        int hashTableSize = HashTableSize(numMatches);

        _fromOrdinalsMap = new long[hashTableSize];
        _toOrdinalsIdentityMap = new long[hashTableSize];

        Array.Fill(_fromOrdinalsMap, -1L);
        Array.Fill(_toOrdinalsIdentityMap, -1L);
    }

    /// <summary>Records that <paramref name="fromOrdinal"/> equals exactly <paramref name="toOrdinal"/>.</summary>
    public void PutEqualOrdinal(int fromOrdinal, int toOrdinal) =>
        PutFromOrdinalEntry(fromOrdinal, ((long)toOrdinal << 32) | (uint)fromOrdinal);

    /// <summary>Records that <paramref name="fromOrdinal"/> equals every one of <paramref name="toOrdinals"/>.</summary>
    public void PutEqualOrdinals(int fromOrdinal, IntList toOrdinals)
    {
        ArgumentNullException.ThrowIfNull(toOrdinals);

        long entry = ((long)toOrdinals.Get(0) << 32) | (uint)fromOrdinal;

        if (toOrdinals.Count > 1)
        {
            // The sign bit says "look in the cluster list", and what sits where the single match would
            // have been is the position this record's run starts at.
            entry = long.MinValue | ((long)_pivotedToOrdinalClusters.Count << 32) | (uint)fromOrdinal;

            for (int i = 0; i < toOrdinals.Count; i++)
            {
                int value = toOrdinals.Get(i);

                if (i == toOrdinals.Count - 1)
                {
                    value |= int.MinValue;
                }

                _pivotedToOrdinalClusters.Add(value);
            }
        }

        PutFromOrdinalEntry(fromOrdinal, entry);
    }

    /// <summary>
    /// Works out each <c>to</c> ordinal's identity, which cannot be known until every match is in.
    /// </summary>
    public void BuildToOrdinalIdentityMapping()
    {
        foreach (long entry in _fromOrdinalsMap)
        {
            // A non-negative entry is a single match, so that record is its own identity.
            if (entry >= 0)
            {
                int toOrdinal = (int)(entry >> 32);
                AddToOrdinalIdentity(toOrdinal, toOrdinal);
            }
        }

        bool newCluster = true;
        int currentIdentity = 0;

        for (int i = 0; i < _pivotedToOrdinalClusters.Count; i++)
        {
            int value = _pivotedToOrdinalClusters.Get(i);

            // The first of a run stands for the whole run.
            if (newCluster)
            {
                currentIdentity = value;
            }

            AddToOrdinalIdentity(value & int.MaxValue, currentIdentity);
            newCluster = (value & int.MinValue) != 0;
        }
    }

    /// <summary>Every <c>to</c> ordinal equal to <paramref name="fromOrdinal"/>.</summary>
    public IEnumerable<int> GetEqualOrdinals(int fromOrdinal)
    {
        if (!TryFindFromOrdinalEntry(fromOrdinal, out long entry))
        {
            yield break;
        }

        if ((entry & long.MinValue) == 0)
        {
            yield return (int)(entry >> 32);

            yield break;
        }

        int position = (int)((entry & long.MaxValue) >> 32);

        while (true)
        {
            int value = _pivotedToOrdinalClusters.Get(position++);

            yield return value & int.MaxValue;

            if ((value & int.MinValue) != 0)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// The identity of the group <paramref name="fromOrdinal"/> belongs to, or
    /// <see cref="HollowConstants.OrdinalNone"/> when it equals nothing on the other side.
    /// </summary>
    public int GetIdentityFromOrdinal(int fromOrdinal)
    {
        if (!TryFindFromOrdinalEntry(fromOrdinal, out long entry))
        {
            return HollowConstants.OrdinalNone;
        }

        return (entry & long.MinValue) != 0
            ? _pivotedToOrdinalClusters.Get((int)((entry & long.MaxValue) >> 32)) & int.MaxValue
            : (int)(entry >> 32);
    }

    /// <summary>The identity of the group <paramref name="toOrdinal"/> belongs to.</summary>
    public int GetIdentityToOrdinal(int toOrdinal)
    {
        int bucket = HashCodes.HashInt(toOrdinal) & (_toOrdinalsIdentityMap.Length - 1);

        while (_toOrdinalsIdentityMap[bucket] != -1L)
        {
            if ((int)_toOrdinalsIdentityMap[bucket] == toOrdinal)
            {
                return (int)(_toOrdinalsIdentityMap[bucket] >> 32);
            }

            bucket = (bucket + 1) & (_toOrdinalsIdentityMap.Length - 1);
        }

        return HollowConstants.OrdinalNone;
    }

    /// <summary>Reading identities on the <c>from</c> side.</summary>
    public Func<int, int> FromOrdinalIdentityTranslator => GetIdentityFromOrdinal;

    /// <summary>Reading identities on the <c>to</c> side.</summary>
    public Func<int, int> ToOrdinalIdentityTranslator => GetIdentityToOrdinal;

    private void PutFromOrdinalEntry(int fromOrdinal, long entry)
    {
        int bucket = HashCodes.HashInt(fromOrdinal) & (_fromOrdinalsMap.Length - 1);

        while (_fromOrdinalsMap[bucket] != -1L)
        {
            bucket = (bucket + 1) & (_fromOrdinalsMap.Length - 1);
        }

        _fromOrdinalsMap[bucket] = entry;
    }

    private bool TryFindFromOrdinalEntry(int fromOrdinal, out long entry)
    {
        int bucket = HashCodes.HashInt(fromOrdinal) & (_fromOrdinalsMap.Length - 1);

        while (_fromOrdinalsMap[bucket] != -1L)
        {
            if ((int)_fromOrdinalsMap[bucket] == fromOrdinal)
            {
                entry = _fromOrdinalsMap[bucket];

                return true;
            }

            bucket = (bucket + 1) & (_fromOrdinalsMap.Length - 1);
        }

        entry = -1L;

        return false;
    }

    private void AddToOrdinalIdentity(int toOrdinal, int identity)
    {
        int bucket = HashCodes.HashInt(toOrdinal) & (_toOrdinalsIdentityMap.Length - 1);

        while (_toOrdinalsIdentityMap[bucket] != -1L)
        {
            bucket = (bucket + 1) & (_toOrdinalsIdentityMap.Length - 1);
        }

        _toOrdinalsIdentityMap[bucket] = ((long)identity << 32) | (uint)toOrdinal;
    }

    /// <summary>
    /// The next power of two at or above twice <paramref name="numMatches"/>, and at least one.
    /// </summary>
    /// <remarks>
    /// Java computes this as a shift over the leading-zero count, which for zero matches gives a table
    /// of one bucket. Saying so directly avoids the negative shift that arithmetic relies on.
    /// </remarks>
    private static int HashTableSize(int numMatches) =>
        numMatches <= 0 ? 1 : (int)BitOperations.RoundUpToPowerOf2((uint)numMatches * 2);
}
