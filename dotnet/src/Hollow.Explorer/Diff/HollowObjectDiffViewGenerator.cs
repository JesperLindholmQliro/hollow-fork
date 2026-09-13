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

using Hollow.Core.Index.Key;
using Hollow.Core.Read.DataAccess;
using Hollow.Explorer.Diff.Effigy;
using Hollow.Explorer.Diff.Effigy.Pairer;

namespace Hollow.Explorer.Diff;

/// <summary>
/// What a page showing records side by side needs from whoever is showing them.
/// </summary>
/// <remarks>
/// The diff UI and the history UI both lay records out this way but answer these differently — one
/// from a <see cref="Core.Tools.Diff.HollowDiff"/>, the other from a chain of states — which is why
/// this is an interface rather than a class.
/// </remarks>
public interface IHollowRecordDiffUI
{
    /// <summary>
    /// Per element type, the key saying which element of one collection is which of the other.
    /// </summary>
    /// <remarks>
    /// Without a hint a collection is paired by guessing, which is quadratic and sometimes wrong. This
    /// is the single most useful thing an embedder can supply.
    /// </remarks>
    IReadOnlyDictionary<string, PrimaryKey> MatchHints { get; }

    /// <summary>A caller's own way of laying out <paramref name="typeName"/>, if it has one.</summary>
    ICustomHollowEffigyFactory? GetCustomHollowEffigyFactory(string typeName);

    /// <summary>What knows whether two records are identical.</summary>
    IExactRecordMatcher ExactRecordMatcher { get; }
}

/// <summary>
/// Builds the row tree for one pair of records.
/// </summary>
/// <remarks>
/// Only the root and its immediate children are built up front. Everything below is built when a row
/// is opened, which is what makes a page over a record reaching thousands of others load at all.
/// </remarks>
/// <param name="fromDataAccess">The earlier state.</param>
/// <param name="toDataAccess">The later state.</param>
/// <param name="diffUI">Who is showing the records.</param>
/// <param name="typeName">The type both records are of.</param>
/// <param name="fromOrdinal">The record in the earlier state, or none.</param>
/// <param name="toOrdinal">The record in the later state, or none.</param>
public sealed class HollowObjectDiffViewGenerator(
    IHollowDataAccess fromDataAccess,
    IHollowDataAccess toDataAccess,
    IHollowRecordDiffUI diffUI,
    string typeName,
    int fromOrdinal,
    int toOrdinal)
{
    /// <summary>The root of the row tree, with its immediate children built.</summary>
    public HollowDiffViewRow GetHollowDiffViewRows()
    {
        (HollowEffigy? from, HollowEffigy? to) = CreateEffigies();

        HollowDiffViewRow rootRow = CreateRootRow(from, to);

        // Touching the children is what builds them; the page needs the first level to draw anything.
        _ = rootRow.Children;

        return rootRow;
    }

    internal IReadOnlyList<HollowDiffViewRow> TraverseEffigyToCreateViewRows(HollowDiffViewRow parent)
    {
        // A value has nothing beneath it.
        if (parent.FieldPair.IsLeafNode)
        {
            return [];
        }

        HollowEffigy? from = parent.FieldPair.From?.Value as HollowEffigy;
        HollowEffigy? to = parent.FieldPair.To?.Value as HollowEffigy;

        IReadOnlyList<EffigyFieldPair> pairs = HollowEffigyFieldPairer.Pair(from, to, diffUI.MatchHints);
        List<HollowDiffViewRow> childRows = new(pairs.Count);

        for (int i = 0; i < pairs.Count; i++)
        {
            int[] rowPath = new int[parent.RowPath.Length + 1];
            parent.RowPath.CopyTo(rowPath, 0);
            rowPath[^1] = i;

            childRows.Add(new HollowDiffViewRow(pairs[i], rowPath, parent, this));
        }

        return childRows;
    }

    private (HollowEffigy? From, HollowEffigy? To) CreateEffigies()
    {
        if (diffUI.GetCustomHollowEffigyFactory(typeName) is { } customFactory)
        {
            // A custom factory is allowed to lay the two records out against each other rather than
            // independently, so both are set before either is read — and it is shared, so one caller
            // does that at a time.
            lock (customFactory)
            {
                customFactory.SetFromHollowRecord(fromDataAccess.GetTypeDataAccess(typeName)!, fromOrdinal);
                customFactory.SetToHollowRecord(toDataAccess.GetTypeDataAccess(typeName)!, toOrdinal);
                customFactory.GenerateEffigies();

                return (customFactory.FromEffigy, customFactory.ToEffigy);
            }
        }

        // One factory for both sides, so its memoised leaves are shared between the two trees — which
        // is most of what makes the pairers' comparisons cheap.
        HollowEffigyFactory effigyFactory = new();

        return (
            effigyFactory.Effigy(fromDataAccess, typeName, fromOrdinal),
            effigyFactory.Effigy(toDataAccess, typeName, toOrdinal));
    }

    private HollowDiffViewRow CreateRootRow(HollowEffigy? fromEffigy, HollowEffigy? toEffigy)
    {
        // The root stands for the records themselves, so its field has no name.
        HollowEffigyField? fromField =
            fromEffigy is null ? null : new HollowEffigyField(null, fromEffigy.ObjectType, fromEffigy);

        HollowEffigyField? toField =
            toEffigy is null ? null : new HollowEffigyField(null, toEffigy.ObjectType, toEffigy);

        return new HollowDiffViewRow(new EffigyFieldPair(fromField, toField, -1, -1), [], null, this);
    }
}
