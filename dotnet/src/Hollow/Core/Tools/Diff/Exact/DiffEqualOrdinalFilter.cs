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
/// Splits two sets of ordinals into the ones that pair off exactly and the ones left over.
/// </summary>
/// <remarks>
/// <para>
/// This is what the diff does at every branch of a record: given the records reached on each side,
/// set aside the identical ones and carry on with the rest. Only the remainder is worth reading, and
/// only the remainder counts towards the difference.
/// </para>
/// <para>
/// Pairing is by identity and by <em>count</em>: three equal records on the left and two on the right
/// leaves one unmatched, because a difference in how many times a value occurs is a difference. The
/// instance holds its tables between calls, since the diff runs this once per branch per record pair.
/// </para>
/// </remarks>
/// <param name="equalOrdinalMap">Which records of this type are equal across the two states.</param>
public sealed class DiffEqualOrdinalFilter(DiffEqualOrdinalMap equalOrdinalMap)
{
    private int[] _hashedIdentityOrdinals = [];
    private int[] _hashedIdentityOrdinalCounts = [];
    private int[] _matchedOrdinalCounts = [];

    /// <summary>The <c>from</c> ordinals that paired off.</summary>
    public IntList MatchedFromOrdinals { get; } = new();

    /// <summary>The <c>to</c> ordinals that paired off.</summary>
    public IntList MatchedToOrdinals { get; } = new();

    /// <summary>The <c>from</c> ordinals left over.</summary>
    public IntList UnmatchedFromOrdinals { get; } = new();

    /// <summary>The <c>to</c> ordinals left over.</summary>
    public IntList UnmatchedToOrdinals { get; } = new();

    /// <summary>
    /// Pairs <paramref name="fromOrdinals"/> off against <paramref name="toOrdinals"/>, leaving the
    /// result in the four lists.
    /// </summary>
    public void Filter(IntList fromOrdinals, IntList toOrdinals)
    {
        ArgumentNullException.ThrowIfNull(fromOrdinals);
        ArgumentNullException.ThrowIfNull(toOrdinals);

        MatchedFromOrdinals.Clear();
        MatchedToOrdinals.Clear();
        UnmatchedFromOrdinals.Clear();
        UnmatchedToOrdinals.Clear();

        EnsureTables(fromOrdinals.Count);

        // Pass one: how many times each identity occurs on the from side.
        for (int i = 0; i < fromOrdinals.Count; i++)
        {
            int identity = equalOrdinalMap.GetIdentityFromOrdinal(fromOrdinals.Get(i));

            if (identity != HollowConstants.OrdinalNone)
            {
                _hashedIdentityOrdinalCounts[FindOrClaimBucket(identity)]++;
            }
        }

        // Pass two: a to-ordinal pairs off while the from side still has one of its identity spare.
        for (int i = 0; i < toOrdinals.Count; i++)
        {
            int identity = equalOrdinalMap.GetIdentityToOrdinal(toOrdinals.Get(i));

            if (identity == HollowConstants.OrdinalNone)
            {
                UnmatchedToOrdinals.Add(toOrdinals.Get(i));

                continue;
            }

            int bucket = FindOrClaimBucket(identity);

            if (_hashedIdentityOrdinals[bucket] == identity
                && _matchedOrdinalCounts[bucket] < _hashedIdentityOrdinalCounts[bucket])
            {
                _matchedOrdinalCounts[bucket]++;
                MatchedToOrdinals.Add(toOrdinals.Get(i));
            }
            else
            {
                UnmatchedToOrdinals.Add(toOrdinals.Get(i));
            }
        }

        // Pass three: spend those pairings back on the from side, in the order they were seen.
        for (int i = 0; i < fromOrdinals.Count; i++)
        {
            int identity = equalOrdinalMap.GetIdentityFromOrdinal(fromOrdinals.Get(i));

            if (identity == HollowConstants.OrdinalNone)
            {
                UnmatchedFromOrdinals.Add(fromOrdinals.Get(i));

                continue;
            }

            int bucket = FindOrClaimBucket(identity);

            if (_matchedOrdinalCounts[bucket] > 0)
            {
                _matchedOrdinalCounts[bucket]--;
                MatchedFromOrdinals.Add(fromOrdinals.Get(i));
            }
            else
            {
                UnmatchedFromOrdinals.Add(fromOrdinals.Get(i));
            }
        }
    }

    private int FindOrClaimBucket(int identity)
    {
        int bucket = HashCodes.HashInt(identity) & (_hashedIdentityOrdinals.Length - 1);

        while (_hashedIdentityOrdinals[bucket] != HollowConstants.OrdinalNone
            && _hashedIdentityOrdinals[bucket] != identity)
        {
            bucket = (bucket + 1) & (_hashedIdentityOrdinals.Length - 1);
        }

        _hashedIdentityOrdinals[bucket] = identity;

        return bucket;
    }

    private void EnsureTables(int fromCount)
    {
        int hashSize = fromCount <= 0 ? 1 : (int)BitOperations.RoundUpToPowerOf2((uint)fromCount * 2);

        // Grown but never shrunk, since the diff calls this once per branch of every record pair.
        if (_hashedIdentityOrdinals.Length < hashSize)
        {
            _hashedIdentityOrdinals = new int[hashSize];
            _hashedIdentityOrdinalCounts = new int[hashSize];
            _matchedOrdinalCounts = new int[hashSize];
        }

        Array.Fill(_hashedIdentityOrdinals, HollowConstants.OrdinalNone);
        Array.Clear(_hashedIdentityOrdinalCounts);
        Array.Clear(_matchedOrdinalCounts);
    }
}
