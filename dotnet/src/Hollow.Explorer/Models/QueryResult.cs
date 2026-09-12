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

using Hollow.Core.Read.Engine;
using Hollow.Core.Tools.Query;
using Hollow.Core.Tools.Traverse;
using Hollow.Core.Util;

namespace Hollow.Explorer.Models;

/// <summary>
/// A search the reader is building up, and what currently matches it.
/// </summary>
/// <remarks>
/// <para>
/// A search is a list of clauses combined with AND, added one at a time — so the result narrows as the
/// reader goes, and the clauses have to be kept rather than just their result: a new state means the
/// whole search has to be run again, and the ordinals that matched the old one mean nothing against it.
/// </para>
/// <para>
/// Each clause matches more than the records that literally hold the value: everything referencing a
/// match is pulled in too, so that searching an actor's name finds the films they are in and not just
/// the name record.
/// </para>
/// </remarks>
public sealed class QueryResult
{
    private readonly List<QueryClause> _queryClauses = [];
    private readonly Dictionary<string, BitSet> _queryMatches = new(StringComparer.Ordinal);

    private long _randomizedStateTag;

    /// <summary>Starts an empty search against the state tagged <paramref name="randomizedStateTag"/>.</summary>
    public QueryResult(long randomizedStateTag) => _randomizedStateTag = randomizedStateTag;

    /// <summary>The clauses, in the order they were added.</summary>
    public IReadOnlyList<QueryClause> QueryClauses => _queryClauses;

    /// <summary>The matching ordinals per type.</summary>
    public IReadOnlyDictionary<string, BitSet> QueryMatches => _queryMatches;

    /// <summary>The whole search, written out the way it reads.</summary>
    public string QueryDisplayString => string.Join(" AND ", _queryClauses);

    /// <summary>
    /// The types something matched in, the ones matching most first.
    /// </summary>
    public IReadOnlyList<QueryTypeMatches> TypeMatches =>
    [
        .. _queryMatches
            .Select(entry => new QueryTypeMatches(entry.Key, entry.Value.Cardinality()))
            .Where(match => match.NumMatches > 0)
            .OrderByDescending(match => match.NumMatches),
    ];

    /// <summary>
    /// Runs the search again if <paramref name="stateEngine"/> has moved on since it last was.
    /// </summary>
    /// <remarks>
    /// An ordinal identifies a record only within one state, so a result held across an update is not
    /// stale so much as meaningless. The clauses are the part that survives.
    /// </remarks>
    public void RecalculateIfNotCurrent(HollowReadStateEngine stateEngine)
    {
        ArgumentNullException.ThrowIfNull(stateEngine);

        if (stateEngine.RandomizedTag == _randomizedStateTag)
        {
            return;
        }

        List<QueryClause> requeryClauses = [.. _queryClauses];

        _queryMatches.Clear();
        _queryClauses.Clear();

        foreach (QueryClause clause in requeryClauses)
        {
            AugmentQuery(clause, stateEngine);
        }

        _randomizedStateTag = stateEngine.RandomizedTag;
    }

    /// <summary>Narrows the search by one more clause.</summary>
    public void AugmentQuery(QueryClause clause, HollowReadStateEngine stateEngine)
    {
        ArgumentNullException.ThrowIfNull(clause);
        ArgumentNullException.ThrowIfNull(stateEngine);

        HollowFieldMatchQuery query = new(stateEngine);

        Dictionary<string, BitSet> clauseMatches = new(
            clause.Type is null
                ? query.FindMatchingRecords(clause.Field, clause.Value)
                : query.FindMatchingRecords(clause.Type, clause.Field, clause.Value),
            StringComparer.Ordinal);

        TransitiveSetTraverser.AddReferencingOutsideClosure(stateEngine, clauseMatches);

        if (_queryClauses.Count == 0)
        {
            foreach ((string type, BitSet matches) in clauseMatches)
            {
                _queryMatches[type] = matches;
            }
        }
        else
        {
            BooleanAndQueryMatches(clauseMatches);
        }

        _queryClauses.Add(clause);
    }

    /// <summary>
    /// Keeps only what also matches <paramref name="newQueryMatches"/>.
    /// </summary>
    /// <remarks>
    /// A type the new clause did not match at all drops out entirely, rather than being left with an
    /// empty bit set — which is the same thing to a reader but shorter to write out.
    /// </remarks>
    private void BooleanAndQueryMatches(Dictionary<string, BitSet> newQueryMatches)
    {
        foreach (string type in _queryMatches.Keys.ToList())
        {
            if (newQueryMatches.TryGetValue(type, out BitSet? newTypeMatches))
            {
                _queryMatches[type].And(newTypeMatches);
            }
            else
            {
                _queryMatches.Remove(type);
            }
        }
    }
}

/// <summary>
/// One clause of a search: a field holding a value, optionally in a named type.
/// </summary>
/// <param name="Type">The type to look in, or <see langword="null"/> for every type having the field.</param>
/// <param name="Field">The field to look at.</param>
/// <param name="Value">The value as it was typed.</param>
public sealed record QueryClause(string? Type, string Field, string Value)
{
    /// <summary>The clause the way it reads, which is how the page shows a search back.</summary>
    public override string ToString() => $"{(Type is null ? "" : Type + ".")}{Field}=\"{Value}\"";
}

/// <summary>How much of one type a search matched.</summary>
/// <param name="TypeName">The type.</param>
/// <param name="NumMatches">How many of its records matched.</param>
public sealed record QueryTypeMatches(string TypeName, int NumMatches);
