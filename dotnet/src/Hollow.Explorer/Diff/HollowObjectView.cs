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

using Hollow.Explorer.Diff.Effigy;
using Hollow.Explorer.Diff.Effigy.Pairer;

namespace Hollow.Explorer.Diff;

/// <summary>
/// A row tree together with what of it the reader is currently being shown.
/// </summary>
/// <remarks>
/// Opening the whole of a record is useless — most of it is identical, and the change is buried. So a
/// view starts by showing only what differs and the branches leading down to it, and the reader opens
/// the rest if they want it.
/// </remarks>
public abstract class HollowObjectView
{
    /// <summary>
    /// How many rows may be shown before a view stops opening branches on the reader's behalf.
    /// </summary>
    /// <remarks>
    /// Past this, a record with differences everywhere would open into thousands of lines. Better to
    /// show where the changes are and let the reader open what they want.
    /// </remarks>
    private const int MaxInitialVisibleRowsBeforeCollapsingDiffs = 300;

    private readonly IExactRecordMatcher _exactRecordMatcher;

    private int _totalVisibilityCount;

    /// <summary>Builds a view over <paramref name="rootRow"/>.</summary>
    protected HollowObjectView(HollowDiffViewRow rootRow, IExactRecordMatcher exactRecordMatcher)
    {
        RootRow = rootRow;
        _exactRecordMatcher = exactRecordMatcher;
    }

    /// <summary>The root of the row tree.</summary>
    public HollowDiffViewRow RootRow { get; }

    /// <summary>
    /// Shows what differs, and the branches leading to it, hiding the rest.
    /// </summary>
    /// <remarks>
    /// Where nothing differs at all, ordering changes are shown instead — for a record whose collection
    /// was merely reordered, that is the only thing there is to see.
    /// </remarks>
    public void ResetView()
    {
        _totalVisibilityCount = 0;

        int totalVisibleRows = ResetViewForDiff(RootRow, 0);

        // The record's own top-level fields are always shown, so the view is never blank.
        foreach (HollowDiffViewRow child in RootRow.Children)
        {
            child.IsVisible = true;
        }

        if (totalVisibleRows > MaxInitialVisibleRowsBeforeCollapsingDiffs)
        {
            CollapseChildrenUnderRootRows(RootRow, pair => pair.IsDiff);

            return;
        }

        if (totalVisibleRows != 0)
        {
            return;
        }

        if (ResetViewForOrderingChanges(RootRow, 0) > MaxInitialVisibleRowsBeforeCollapsingDiffs)
        {
            CollapseChildrenUnderRootRows(RootRow, pair => pair.IsOrderingDiff);
        }
    }

    private int ResetViewForDiff(HollowDiffViewRow row, int runningVisibilityCount)
    {
        // A subtree already known to be identical cannot contain a difference, so it is not walked at
        // all — which is what keeps this affordable on a record reaching thousands of others.
        if (RowIsExactMatch(row))
        {
            return 0;
        }

        int branchVisibilityCount = 0;

        if (row.FieldPair.IsDiff)
        {
            row.IsVisible = true;
            _totalVisibilityCount++;
            branchVisibilityCount++;

            branchVisibilityCount += MakeAllChildrenVisible(row);

            return branchVisibilityCount;
        }

        foreach (HollowDiffViewRow child in row.Children)
        {
            branchVisibilityCount += ResetViewForDiff(child, branchVisibilityCount + runningVisibilityCount);

            // Something below differs, so this row has to be shown for the reader to reach it.
            if (branchVisibilityCount > 0)
            {
                row.IsVisible = true;
                _totalVisibilityCount++;
                branchVisibilityCount++;
            }
        }

        return branchVisibilityCount;
    }

    private int MakeAllChildrenVisible(HollowDiffViewRow row)
    {
        if (_totalVisibilityCount > MaxInitialVisibleRowsBeforeCollapsingDiffs)
        {
            return 0;
        }

        int branchVisibilityCount = 0;

        foreach (HollowDiffViewRow child in row.Children)
        {
            child.IsVisible = true;
            _totalVisibilityCount++;
            branchVisibilityCount++;

            branchVisibilityCount += MakeAllChildrenVisible(child);
        }

        return branchVisibilityCount;
    }

    private int ResetViewForOrderingChanges(HollowDiffViewRow row, int runningVisibilityCount)
    {
        if (RowIsExactMatch(row))
        {
            return 0;
        }

        int branchVisibilityCount = 0;

        if (row.FieldPair.IsOrderingDiff)
        {
            row.IsVisible = true;

            return branchVisibilityCount + 1;
        }

        foreach (HollowDiffViewRow child in row.Children)
        {
            int childCount = ResetViewForOrderingChanges(child, runningVisibilityCount + branchVisibilityCount);

            if (childCount > 0)
            {
                row.IsVisible = true;
                branchVisibilityCount += childCount;
            }
        }

        return branchVisibilityCount;
    }

    /// <summary>
    /// Closes everything under the rows that are themselves the change, leaving the change visible but
    /// not its contents.
    /// </summary>
    private static void CollapseChildrenUnderRootRows(
        HollowDiffViewRow row, Func<EffigyFieldPair, bool> isRootOfChange)
    {
        // Only walks branches already built, since an unbuilt one has nothing visible to close.
        if (!row.AreChildrenPopulated)
        {
            return;
        }

        foreach (HollowDiffViewRow child in row.Children)
        {
            if (isRootOfChange(child.FieldPair))
            {
                MakeAllChildrenInvisible(child);
            }
            else
            {
                CollapseChildrenUnderRootRows(child, isRootOfChange);
            }
        }
    }

    private static void MakeAllChildrenInvisible(HollowDiffViewRow row)
    {
        if (!row.AreChildrenPopulated)
        {
            return;
        }

        foreach (HollowDiffViewRow child in row.Children)
        {
            child.IsVisible = false;
            MakeAllChildrenInvisible(child);
        }
    }

    private bool RowIsExactMatch(HollowDiffViewRow row)
    {
        EffigyFieldPair pair = row.FieldPair;

        if (pair.From is null || pair.To is null || pair.IsLeafNode)
        {
            return false;
        }

        return pair.From.Value is HollowEffigy from
            && pair.To.Value is HollowEffigy to
            && _exactRecordMatcher.IsExactMatch(from.DataAccess, from.Ordinal, to.DataAccess, to.Ordinal);
    }
}

/// <summary>
/// Two records of one type, from two states, laid out against each other.
/// </summary>
/// <param name="type">The type both records are of.</param>
/// <param name="fromOrdinal">The record in the earlier state, or none.</param>
/// <param name="toOrdinal">The record in the later state, or none.</param>
/// <param name="rootRow">The root of the row tree.</param>
/// <param name="exactRecordMatcher">What knows whether two records are identical.</param>
public sealed class HollowDiffView(
    string type,
    int fromOrdinal,
    int toOrdinal,
    HollowDiffViewRow rootRow,
    IExactRecordMatcher exactRecordMatcher)
    : HollowObjectView(rootRow, exactRecordMatcher)
{
    /// <summary>The type both records are of.</summary>
    public string Type { get; } = type;

    /// <summary>The record in the earlier state, or none.</summary>
    public int FromOrdinal { get; } = fromOrdinal;

    /// <summary>The record in the later state, or none.</summary>
    public int ToOrdinal { get; } = toOrdinal;
}
