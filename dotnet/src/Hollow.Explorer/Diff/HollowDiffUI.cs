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

using System.Collections.Concurrent;
using Hollow.Core.Index.Key;
using Hollow.Core.Tools.Diff;
using Hollow.Core.Util;
using Hollow.Explorer.Diff.Effigy;
using Hollow.Explorer.Diff.Effigy.Pairer;
using Hollow.Explorer.Diff.Models;

namespace Hollow.Explorer.Diff;

/// <summary>
/// One diff, mounted as a set of pages: the two states, what they are called, and how records in them
/// should be lined up.
/// </summary>
/// <remarks>
/// <para>
/// Java's <c>HollowDiffUI</c> also owns the four page objects, the template engine and the request
/// routing, because Velocity and the servlet API give it nowhere else to put them. Here the framework
/// owns all three, so what is left is the diff itself and the answers the pages need about it.
/// </para>
/// <para>
/// The per-type caches are the reason this outlives a request. Scoring every matched pair of a large
/// type takes long enough that doing it again on each page of results would be the slowest thing the
/// UI does.
/// </para>
/// </remarks>
public sealed class HollowDiffUI : IHollowRecordDiffUI
{
    private readonly ConcurrentDictionary<string, ICustomHollowEffigyFactory> _customEffigyFactories =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, PrimaryKey> _matchHints = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, IReadOnlyList<ObjectPairDiffScore>> _pairScores =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, IReadOnlyList<UnmatchedObject>> _unmatchedInFrom =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, IReadOnlyList<UnmatchedObject>> _unmatchedInTo =
        new(StringComparer.Ordinal);

    /// <summary>Mounts <paramref name="diff"/> as a set of pages.</summary>
    /// <param name="diff">The diff to show, already calculated.</param>
    /// <param name="fromBlobName">What to call the earlier state.</param>
    /// <param name="toBlobName">What to call the later state.</param>
    public HollowDiffUI(HollowDiff diff, string fromBlobName, string toBlobName)
    {
        ArgumentNullException.ThrowIfNull(diff);

        Diff = diff;
        FromBlobName = fromBlobName;
        ToBlobName = toBlobName;
        ExactRecordMatcher = new DiffExactRecordMatcher(diff.EqualityMapping);
    }

    /// <summary>The diff being shown.</summary>
    public HollowDiff Diff { get; }

    /// <summary>What the earlier state is called.</summary>
    public string FromBlobName { get; }

    /// <summary>What the later state is called.</summary>
    public string ToBlobName { get; }

    /// <inheritdoc />
    public IExactRecordMatcher ExactRecordMatcher { get; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, PrimaryKey> MatchHints => _matchHints;

    /// <inheritdoc />
    public ICustomHollowEffigyFactory? GetCustomHollowEffigyFactory(string typeName) =>
        _customEffigyFactories.GetValueOrDefault(typeName);

    /// <summary>Lays <paramref name="typeName"/> out the caller's way rather than the default one.</summary>
    public void AddCustomHollowEffigyFactory(string typeName, ICustomHollowEffigyFactory factory) =>
        _customEffigyFactories[typeName] = factory;

    /// <summary>
    /// Says which element of one collection is which of the other, for the type
    /// <paramref name="matchHint"/> names.
    /// </summary>
    public void AddMatchHint(PrimaryKey matchHint)
    {
        ArgumentNullException.ThrowIfNull(matchHint);
        _matchHints[matchHint.Type] = matchHint;
    }

    /// <summary>The diff of <paramref name="typeName"/>, or <see langword="null"/> if it has none.</summary>
    public HollowTypeDiff? GetTypeDiff(string typeName) => Diff.GetTypeDiff(typeName);

    /// <summary>
    /// Every matched pair of <paramref name="typeDiff"/> that differs at all, widest apart first.
    /// </summary>
    /// <remarks>
    /// The engine records differences per field, so a record appearing in several field diffs is one
    /// pair whose score is the sum. Indexing by the earlier ordinal turns that regrouping into a single
    /// pass rather than a dictionary lookup per difference.
    /// </remarks>
    public IReadOnlyList<ObjectPairDiffScore> GetObjectPairDiffScores(HollowTypeDiff typeDiff)
    {
        ArgumentNullException.ThrowIfNull(typeDiff);

        return _pairScores.GetOrAdd(typeDiff.TypeName, _ => AggregateFieldDiffScores(typeDiff));
    }

    /// <summary>Every record of <paramref name="typeDiff"/> the later state does not have.</summary>
    public IReadOnlyList<UnmatchedObject> GetUnmatchedInFrom(HollowTypeDiff typeDiff)
    {
        ArgumentNullException.ThrowIfNull(typeDiff);

        return _unmatchedInFrom.GetOrAdd(
            typeDiff.TypeName,
            _ => DescribeUnmatched(typeDiff, fromSide: true));
    }

    /// <summary>Every record of <paramref name="typeDiff"/> the earlier state does not have.</summary>
    public IReadOnlyList<UnmatchedObject> GetUnmatchedInTo(HollowTypeDiff typeDiff)
    {
        ArgumentNullException.ThrowIfNull(typeDiff);

        return _unmatchedInTo.GetOrAdd(
            typeDiff.TypeName,
            _ => DescribeUnmatched(typeDiff, fromSide: false));
    }

    /// <summary>
    /// The key of the record at <paramref name="fromOrdinal"/>, or of the one at
    /// <paramref name="toOrdinal"/> where there is no earlier record.
    /// </summary>
    public string GetDisplayKey(HollowTypeDiff typeDiff, int fromOrdinal, int toOrdinal)
    {
        ArgumentNullException.ThrowIfNull(typeDiff);

        return fromOrdinal != -1
            ? typeDiff.FromTypeState is { } fromState
                ? typeDiff.Matcher.GetKeyDisplayString(fromState, fromOrdinal)
                : ""
            : typeDiff.ToTypeState is { } toState
                ? typeDiff.Matcher.GetKeyDisplayString(toState, toOrdinal)
                : "";
    }

    /// <summary>
    /// Every header tag either blob recorded, in name order.
    /// </summary>
    public IReadOnlyList<DiffHeaderEntry> GetHeaderEntries()
    {
        IReadOnlyDictionary<string, string> fromTags = Diff.FromStateEngine.HeaderTags;
        IReadOnlyDictionary<string, string> toTags = Diff.ToStateEngine.HeaderTags;

        return
        [
            .. fromTags.Keys
                .Concat(toTags.Keys)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Select((key, index) => new DiffHeaderEntry(
                    index,
                    key,
                    fromTags.GetValueOrDefault(key),
                    toTags.GetValueOrDefault(key))),
        ];
    }

    private static IReadOnlyList<ObjectPairDiffScore> AggregateFieldDiffScores(HollowTypeDiff typeDiff)
    {
        // Without an earlier state there is nothing to have matched, so there are no pairs.
        if (typeDiff.FromTypeState is not { } fromState)
        {
            return [];
        }

        ObjectPairDiffScore?[] pairsByFromOrdinal = new ObjectPairDiffScore?[fromState.MaxOrdinal + 1];

        foreach (HollowFieldDiff fieldDiff in typeDiff.FieldDiffs)
        {
            for (int i = 0; i < fieldDiff.NumDiffs; i++)
            {
                int fromOrdinal = fieldDiff.GetFromOrdinal(i);

                pairsByFromOrdinal[fromOrdinal] ??= new ObjectPairDiffScore(
                    typeDiff.Matcher.GetKeyDisplayString(fromState, fromOrdinal),
                    fromOrdinal,
                    fieldDiff.GetToOrdinal(i));

                pairsByFromOrdinal[fromOrdinal]!.IncrementDiffScore(fieldDiff.GetPairScore(i));
            }
        }

        List<ObjectPairDiffScore> scores = [.. pairsByFromOrdinal.OfType<ObjectPairDiffScore>()];
        scores.Sort();

        return scores;
    }

    private static IReadOnlyList<UnmatchedObject> DescribeUnmatched(HollowTypeDiff typeDiff, bool fromSide)
    {
        // A type only one state has is entirely unmatched, but there is no state to read keys out of.
        if ((fromSide ? typeDiff.FromTypeState : typeDiff.ToTypeState) is not { } typeState)
        {
            return [];
        }

        IntList ordinals = fromSide ? typeDiff.UnmatchedOrdinalsInFrom : typeDiff.UnmatchedOrdinalsInTo;
        List<UnmatchedObject> unmatched = new(ordinals.Count);

        for (int i = 0; i < ordinals.Count; i++)
        {
            int ordinal = ordinals.Get(i);
            unmatched.Add(new UnmatchedObject(typeDiff.Matcher.GetKeyDisplayString(typeState, ordinal), ordinal));
        }

        return unmatched;
    }
}
