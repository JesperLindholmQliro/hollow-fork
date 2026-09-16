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
using Hollow.Core.Index.Traversal;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Tools.Diff;
using Hollow.Core.Util;

namespace Hollow.Tools.Diff.Specific;

/// <summary>
/// Counts how a few named fields differ between two states, across the records the two states have
/// in common.
/// </summary>
/// <remarks>
/// <para>
/// A full diff walks everything and reports on everything. This walks only the paths it is given, so
/// a question like "how many ratings changed" gets an answer without the cost of the whole dataset.
/// </para>
/// <para>
/// The paths matter twice over. The record match paths pair records across the two states by key.
/// The element match paths then name the values to compare within each paired record — and because a
/// path crossing a collection reaches many values, one record generally contributes many elements.
/// Naming some of those paths as element key paths turns the comparison from "these values went and
/// those arrived" into "this value changed", by pairing elements within the record first.
/// </para>
/// </remarks>
public sealed class HollowSpecificDiff
{
    private readonly HollowReadStateEngine _from;
    private readonly HollowReadStateEngine _to;
    private readonly HollowDiffMatcher _matcher;
    private readonly string _type;

    private string[] _elementPaths = [];
    private BitSet? _elementKeyPaths;
    private BitSet? _elementNonKeyPaths;

    private long _totalUnmatchedFromElements;
    private long _totalUnmatchedToElements;
    private long _totalModifiedElements;
    private long _totalMatchedEqualElements;

    /// <summary>Prepares a diff of one type across two states.</summary>
    /// <param name="from">The earlier state.</param>
    /// <param name="to">The later state.</param>
    /// <param name="type">The object type to diff.</param>
    public HollowSpecificDiff(HollowReadStateEngine from, HollowReadStateEngine to, string type)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        _from = from;
        _to = to;
        _type = type;
        _matcher = new HollowDiffMatcher(
            (HollowObjectTypeReadState?)from.GetTypeState(type),
            (HollowObjectTypeReadState?)to.GetTypeState(type));
    }

    /// <summary>Values the earlier state reached that the later state does not.</summary>
    public long TotalUnmatchedFromElements => _totalUnmatchedFromElements;

    /// <summary>Values the later state reached that the earlier state does not.</summary>
    public long TotalUnmatchedToElements => _totalUnmatchedToElements;

    /// <summary>
    /// Values paired by their element key that disagree about everything else.
    /// </summary>
    /// <remarks>Always zero where no element key paths were set: without a key there is no "changed".</remarks>
    public long TotalModifiedElements => _totalModifiedElements;

    /// <summary>Values both states reached and agree about.</summary>
    public long TotalMatchedEqualElements => _totalMatchedEqualElements;

    /// <summary>Sets the key paths records are paired by across the two states.</summary>
    public void SetRecordMatchPaths(params string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        foreach (string path in paths)
        {
            _matcher.AddMatchPath(path);
        }
    }

    /// <summary>Sets the paths whose values are compared within each paired record.</summary>
    public void SetElementMatchPaths(params string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        ResetResults();

        _elementPaths = paths;
        _elementKeyPaths = null;
        _elementNonKeyPaths = null;
    }

    /// <summary>
    /// Sets the element paths that identify a value within one record, so that a difference in the
    /// remaining paths reads as a modification rather than as a removal and an addition.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A key path was not also given to <see cref="SetElementMatchPaths"/>.
    /// </exception>
    public void SetElementKeyPaths(params string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        ResetResults();

        BitSet keyPaths = new(_elementPaths.Length);

        foreach (string path in paths)
        {
            int index = GetElementPathIndex(path);

            if (index == -1)
            {
                throw new ArgumentException(
                    $"a key path must also be an element match path, and '{path}' is not",
                    nameof(paths));
            }

            keyPaths.Set(index);
        }

        BitSet nonKeyPaths = new(_elementPaths.Length);

        for (int i = 0; i < _elementPaths.Length; i++)
        {
            nonKeyPaths.Set(i);
        }

        nonKeyPaths.AndNot(keyPaths);

        _elementKeyPaths = keyPaths;
        _elementNonKeyPaths = nonKeyPaths;
    }

    /// <summary>Where a path sits among the element match paths, or -1 if it is not one of them.</summary>
    public int GetElementPathIndex(string path) =>
        Array.FindIndex(_elementPaths, candidate => string.Equals(candidate, path, StringComparison.Ordinal));

    /// <summary>Pairs the records of the two states by their record match paths.</summary>
    public void PrepareMatches() => _matcher.CalculateMatches();

    /// <summary>
    /// Walks the paired records and counts the values that agree, changed, went and arrived.
    /// </summary>
    /// <remarks>
    /// Java hands the paired records out to one thread per core. This runs them on the caller's
    /// thread: the four counts are the whole result, and a diff of a few named paths is small enough
    /// that the co-ordination would cost more than it saves.
    /// </remarks>
    public void Calculate()
    {
        ResetResults();

        HollowIndexerValueTraverser fromTraverser = new(_from, _type, _elementPaths);
        HollowIndexerValueTraverser toTraverser = new(_to, _type, _elementPaths);

        int[] hashedResults = new int[16];

        for (int i = 0; i < _matcher.MatchedOrdinals.Count; i++)
        {
            long ordinalPair = _matcher.MatchedOrdinals.Get(i);

            fromTraverser.Traverse((int)((ulong)ordinalPair >> 32));
            toTraverser.Traverse((int)ordinalPair);

            if (fromTraverser.MatchCount * 2 > hashedResults.Length)
            {
                hashedResults = new int[HashTableSize(fromTraverser.MatchCount)];
            }

            PopulateHashTable(fromTraverser, hashedResults);
            CountMatches(fromTraverser, toTraverser, hashedResults);
        }

        // A record only one state has contributes every value it reaches, with nothing to compare.
        for (int i = 0; i < _matcher.ExtraInFrom.Count; i++)
        {
            fromTraverser.Traverse(_matcher.ExtraInFrom.Get(i));
            _totalUnmatchedFromElements += fromTraverser.MatchCount;
        }

        for (int i = 0; i < _matcher.ExtraInTo.Count; i++)
        {
            toTraverser.Traverse(_matcher.ExtraInTo.Get(i));
            _totalUnmatchedToElements += toTraverser.MatchCount;
        }
    }

    private void CountMatches(
        HollowIndexerValueTraverser fromTraverser,
        HollowIndexerValueTraverser toTraverser,
        int[] hashedResults)
    {
        int matchedEqual = 0;
        int modified = 0;
        int hashMask = hashedResults.Length - 1;

        for (int j = 0; j < toTraverser.MatchCount; j++)
        {
            int hash = _elementKeyPaths is null
                ? toTraverser.GetMatchHash(j)
                : toTraverser.GetMatchHash(j, _elementKeyPaths);

            int bucket = hash & hashMask;

            while (hashedResults[bucket] != -1)
            {
                if (_elementKeyPaths is null)
                {
                    if (fromTraverser.IsMatchEqual(hashedResults[bucket], toTraverser, j))
                    {
                        matchedEqual++;
                        break;
                    }
                }
                else if (fromTraverser.IsMatchEqual(hashedResults[bucket], toTraverser, j, _elementKeyPaths))
                {
                    if (fromTraverser.IsMatchEqual(hashedResults[bucket], toTraverser, j, _elementNonKeyPaths!))
                    {
                        matchedEqual++;
                    }
                    else
                    {
                        modified++;
                    }

                    break;
                }

                bucket = (bucket + 1) & hashMask;
            }
        }

        int common = matchedEqual + modified;

        _totalMatchedEqualElements += matchedEqual;
        _totalModifiedElements += modified;
        _totalUnmatchedFromElements += fromTraverser.MatchCount - common;
        _totalUnmatchedToElements += toTraverser.MatchCount - common;
    }

    private void PopulateHashTable(HollowIndexerValueTraverser fromTraverser, int[] hashedResults)
    {
        Array.Fill(hashedResults, -1);

        int hashMask = hashedResults.Length - 1;

        for (int j = 0; j < fromTraverser.MatchCount; j++)
        {
            int hash = _elementKeyPaths is null
                ? fromTraverser.GetMatchHash(j)
                : fromTraverser.GetMatchHash(j, _elementKeyPaths);

            int bucket = hash & hashMask;

            while (hashedResults[bucket] != -1)
            {
                bucket = (bucket + 1) & hashMask;
            }

            hashedResults[bucket] = j;
        }
    }

    /// <summary>The smallest power of two that leaves the table at most half full.</summary>
    private static int HashTableSize(int matches)
    {
        if (matches <= 0)
        {
            // Java computes (0 * 2) - 1 here, and the leading-zero count of -1 lands it on 1 by
            // accident. A record that reaches no values needs no table; return the same size on purpose.
            return 1;
        }

        return 1 << (32 - BitOperations.LeadingZeroCount((uint)((matches * 2) - 1)));
    }

    private void ResetResults()
    {
        _totalUnmatchedFromElements = 0;
        _totalUnmatchedToElements = 0;
        _totalMatchedEqualElements = 0;
        _totalModifiedElements = 0;
    }
}
