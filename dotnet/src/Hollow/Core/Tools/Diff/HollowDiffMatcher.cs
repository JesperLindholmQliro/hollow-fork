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

using System.Globalization;
using Hollow.Core.Index;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Util;

namespace Hollow.Core.Tools.Diff;

/// <summary>
/// Pairs the records of one type across two states by their primary key.
/// </summary>
/// <remarks>
/// This is the step that decides what a diff is <em>about</em>. An ordinal means nothing across two
/// states, so without a key there is no such thing as "the same record changed" — only records that
/// went and records that arrived. With one, every pair that shares a key becomes a comparison and
/// everything left over becomes an addition or a removal.
/// </remarks>
/// <param name="fromTypeState">The type as the earlier state holds it, or nothing if it had none.</param>
/// <param name="toTypeState">The type as the later state holds it.</param>
public sealed class HollowDiffMatcher(
    HollowObjectTypeReadState? fromTypeState, HollowObjectTypeReadState? toTypeState)
{
    private readonly List<string> _matchPaths = [];

    private HollowPrimaryKeyIndex? _fromIndex;
    private HollowPrimaryKeyIndex? _toIndex;

    /// <summary>The field paths making up the key records are paired by.</summary>
    public IReadOnlyList<string> MatchPaths => _matchPaths;

    /// <summary>Every pair, as the <c>from</c> ordinal packed above the <c>to</c> ordinal.</summary>
    public LongList MatchedOrdinals { get; } = new();

    /// <summary>The records only the earlier state has.</summary>
    public IntList ExtraInFrom { get; } = new();

    /// <summary>The records only the later state has.</summary>
    public IntList ExtraInTo { get; } = new();

    /// <summary>Adds a field path to the key.</summary>
    public void AddMatchPath(string path) => _matchPaths.Add(path);

    /// <summary>Works out the pairs.</summary>
    public void CalculateMatches()
    {
        // Without both sides, or without a key, nothing can be paired — so everything is an addition or
        // a removal and the diff reports counts rather than changes.
        if (fromTypeState is null || toTypeState is null || _matchPaths.Count == 0)
        {
            AddAll(fromTypeState, ExtraInFrom);
            AddAll(toTypeState, ExtraInTo);

            return;
        }

        _fromIndex = new HollowPrimaryKeyIndex(
            fromTypeState.StateEngine, fromTypeState.TypeName, [.. _matchPaths]);

        _toIndex = new HollowPrimaryKeyIndex(
            toTypeState.StateEngine, toTypeState.TypeName, [.. _matchPaths]);

        BitSet fromPopulatedOrdinals = fromTypeState.PopulatedOrdinals;
        BitSet fromUnmatchedOrdinals = new(fromPopulatedOrdinals.Length);
        fromUnmatchedOrdinals.Or(fromPopulatedOrdinals);

        BitSet toPopulatedOrdinals = toTypeState.PopulatedOrdinals;

        for (int toOrdinal = toPopulatedOrdinals.NextSetBit(0);
            toOrdinal != HollowConstants.OrdinalNone;
            toOrdinal = toPopulatedOrdinals.NextSetBit(toOrdinal + 1))
        {
            object?[] key = _toIndex.GetRecordKey(toOrdinal);
            int matchedOrdinal = _fromIndex.GetMatchingOrdinal(key);

            if (matchedOrdinal != HollowConstants.OrdinalNone)
            {
                MatchedOrdinals.Add(((long)matchedOrdinal << 32) | (uint)toOrdinal);
                fromUnmatchedOrdinals.Clear(matchedOrdinal);
            }
            else
            {
                ExtraInTo.Add(toOrdinal);
            }
        }

        for (int fromOrdinal = fromUnmatchedOrdinals.NextSetBit(0);
            fromOrdinal != HollowConstants.OrdinalNone;
            fromOrdinal = fromUnmatchedOrdinals.NextSetBit(fromOrdinal + 1))
        {
            ExtraInFrom.Add(fromOrdinal);
        }
    }

    /// <summary>
    /// The key of one record, as text — or <c>ORDINAL:n</c> for a type with no key, which has nothing
    /// else to be called by.
    /// </summary>
    public string GetKeyDisplayString(HollowObjectTypeReadState state, int ordinal)
    {
        object?[]? key = null;

        if (ReferenceEquals(state, fromTypeState))
        {
            key = _fromIndex?.GetRecordKey(ordinal);
        }
        else if (ReferenceEquals(state, toTypeState))
        {
            key = _toIndex?.GetRecordKey(ordinal);
        }

        return key is null
            ? string.Create(CultureInfo.InvariantCulture, $"ORDINAL:{ordinal}")
            : string.Join(' ', key.Select(part => part.Invariant()));
    }

    private static void AddAll(HollowObjectTypeReadState? typeState, IntList fillList)
    {
        if (typeState is null)
        {
            return;
        }

        BitSet populatedOrdinals = typeState.PopulatedOrdinals;

        for (int ordinal = populatedOrdinals.NextSetBit(0);
            ordinal != HollowConstants.OrdinalNone;
            ordinal = populatedOrdinals.NextSetBit(ordinal + 1))
        {
            fillList.Add(ordinal);
        }
    }
}
