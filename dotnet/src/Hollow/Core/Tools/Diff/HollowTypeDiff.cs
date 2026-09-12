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

using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Tools.Diff.Count;
using Hollow.Core.Tools.Diff.Exact;
using Hollow.Core.Util;

namespace Hollow.Core.Tools.Diff;

/// <summary>
/// How one type differs between two states.
/// </summary>
/// <remarks>
/// Records are paired by primary key, the pairs that are not identical are walked, and the difference
/// is attributed to the fields it is spread across — so what comes out is which fields moved and by how
/// much, plus the records that arrived and left.
/// </remarks>
public sealed class HollowTypeDiff
{
    private readonly HollowDiff _rootDiff;
    private readonly HashSet<string> _shortcutTypes = new(StringComparer.Ordinal);

    private IReadOnlyList<HollowFieldDiff> _calculatedFieldDiffs = [];

    internal HollowTypeDiff(HollowDiff rootDiff, string type, params string[]? matchPaths)
    {
        _rootDiff = rootDiff;
        TypeName = type;

        FromTypeState = rootDiff.FromStateEngine.GetTypeState(type) as HollowObjectTypeReadState;
        ToTypeState = rootDiff.ToStateEngine.GetTypeState(type) as HollowObjectTypeReadState;

        Matcher = new HollowDiffMatcher(FromTypeState, ToTypeState);

        foreach (string matchPath in matchPaths ?? [])
        {
            Matcher.AddMatchPath(matchPath);
        }
    }

    /// <summary>The type this reports on.</summary>
    public string TypeName { get; }

    /// <summary>The type as the earlier state holds it, or nothing if it had none.</summary>
    public HollowObjectTypeReadState? FromTypeState { get; }

    /// <summary>The type as the later state holds it.</summary>
    public HollowObjectTypeReadState? ToTypeState { get; }

    /// <summary>What paired the records up.</summary>
    public HollowDiffMatcher Matcher { get; }

    /// <summary>Whether records of this type can be paired at all.</summary>
    public bool HasMatchPaths => Matcher.MatchPaths.Count > 0;

    /// <summary>Whether either state has this type.</summary>
    public bool HasAnyData => FromTypeState is not null || ToTypeState is not null;

    /// <summary>The differences, by field, once <see cref="HollowDiff.CalculateDiffs"/> has run.</summary>
    public IReadOnlyList<HollowFieldDiff> FieldDiffs => _calculatedFieldDiffs;

    /// <summary>How many records paired off.</summary>
    public int TotalNumberOfMatches => Matcher.MatchedOrdinals.Count;

    /// <summary>The records only the earlier state has.</summary>
    public IntList UnmatchedOrdinalsInFrom => Matcher.ExtraInFrom;

    /// <summary>The records only the later state has.</summary>
    public IntList UnmatchedOrdinalsInTo => Matcher.ExtraInTo;

    /// <summary>How many records of this type the earlier state holds.</summary>
    public int TotalItemsInFromState => FromTypeState?.PopulatedOrdinals.Cardinality() ?? 0;

    /// <summary>How many the later state holds.</summary>
    public int TotalItemsInToState => ToTypeState?.PopulatedOrdinals.Cardinality() ?? 0;

    /// <summary>The whole difference in this type, as a rough measure of how much moved.</summary>
    public long TotalDiffScore => _calculatedFieldDiffs.Sum(diff => diff.TotalDiffScore);

    /// <summary>Adds a field path to the key records are paired by.</summary>
    public void AddMatchPath(string path) => Matcher.AddMatchPath(path);

    /// <summary>
    /// Stops the walk at <paramref name="type"/>, counting its records rather than comparing them.
    /// </summary>
    /// <remarks>
    /// A trade: less detail under that branch, in exchange for a diff that finishes. Worth it for a
    /// type whose records are large and whose detail is beside whatever is being looked for.
    /// </remarks>
    public void AddShortcutType(string type) => _shortcutTypes.Add(type);

    /// <summary>Whether the walk stops at <paramref name="type"/>.</summary>
    public bool IsShortcutType(string type) => _shortcutTypes.Contains(type);

    internal void CalculateMatches() => Matcher.CalculateMatches();

    internal void CalculateDiffs()
    {
        HollowDiffNodeIdentifier rootId = new(TypeName);
        DiffEqualityMapping equalityMapping = _rootDiff.EqualityMapping;

        HollowDiffCountingNode rootNode =
            new HollowDiffObjectCountingNode(_rootDiff, this, rootId, FromTypeState, ToTypeState);

        DiffEqualOrdinalMap rootNodeOrdinalMap = equalityMapping.GetEqualOrdinalMap(TypeName);
        bool requiresMissingFieldTraversal = equalityMapping.RequiresMissingFieldTraversal(TypeName);

        // Reused across every pair, since the tree takes lists rather than ordinals.
        IntList fromOrdinalList = new(1);
        IntList toOrdinalList = new(1);

        for (int i = 0; i < Matcher.MatchedOrdinals.Count; i++)
        {
            int fromOrdinal = (int)(Matcher.MatchedOrdinals.Get(i) >> 32);
            int toOrdinal = (int)Matcher.MatchedOrdinals.Get(i);

            int fromIdentity = rootNodeOrdinalMap.GetIdentityFromOrdinal(fromOrdinal);

            // A pair that is already known to be identical is skipped whole — which is most of a
            // dataset, and the reason a diff over one finishes.
            bool recordsDiffer = fromIdentity == HollowConstants.OrdinalNone
                || fromIdentity != rootNodeOrdinalMap.GetIdentityToOrdinal(toOrdinal);

            if (!recordsDiffer && !requiresMissingFieldTraversal)
            {
                continue;
            }

            fromOrdinalList.Clear();
            fromOrdinalList.Add(fromOrdinal);
            toOrdinalList.Clear();
            toOrdinalList.Add(toOrdinal);

            rootNode.Prepare(fromOrdinal, toOrdinal);

            if (recordsDiffer)
            {
                rootNode.TraverseDiffs(fromOrdinalList, toOrdinalList);
            }
            else
            {
                rootNode.TraverseMissingFields(fromOrdinalList, toOrdinalList);
            }
        }

        _calculatedFieldDiffs = CombineResults(rootNode.GetFieldDiffs());
    }

    /// <summary>
    /// Folds together the accountings for one field found under different routes.
    /// </summary>
    /// <remarks>
    /// Java combines across threads here. On one thread there is still something to combine, because a
    /// field reachable by more than one route through the model gets a node per route.
    /// </remarks>
    private static List<HollowFieldDiff> CombineResults(IReadOnlyList<HollowFieldDiff> results)
    {
        Dictionary<HollowDiffNodeIdentifier, HollowFieldDiff> combined = [];

        foreach (HollowFieldDiff fieldDiff in results)
        {
            if (combined.TryGetValue(fieldDiff.FieldIdentifier, out HollowFieldDiff? existing))
            {
                existing.AddResults(fieldDiff);
            }
            else
            {
                combined[fieldDiff.FieldIdentifier] = fieldDiff;
            }
        }

        return [.. combined.Values];
    }
}
