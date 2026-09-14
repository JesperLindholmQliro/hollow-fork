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

using Hollow.Explorer.Diff;
using Hollow.Explorer.Diff.Models;
using Hollow.Explorer.History.Models;

namespace Hollow.Explorer.History.ViewModels;

/// <summary>
/// What every history page needs, whatever else it shows.
/// </summary>
/// <remarks>
/// Java merges each page between <c>history-header.vm</c> and <c>history-footer.vm</c> and puts these
/// into the shared context. A layout and a base model say the same thing without every page having to
/// name the two halves.
/// </remarks>
public abstract class HistoryPageModel
{
    /// <summary>Where the history UI is mounted, which every link on the page is relative to.</summary>
    public string BasePath { get; set; } = "";

    /// <summary>Whether to offer a way back to the overview, which the overview itself does not.</summary>
    public bool ShowHomeLink { get; set; } = true;

    /// <summary>Extra cells a host put in the header bar, already in the order they go in.</summary>
    public IReadOnlyList<string> CommonHeaderEntries { get; set; } = [];

    /// <summary>
    /// Every header tag either side of the transition recorded, or empty where the page has no one
    /// state to show them for.
    /// </summary>
    public IReadOnlyList<DiffHeaderEntry> HeaderEntries { get; set; } = [];
}

/// <summary>The overview: every version the history holds, and what each changed.</summary>
public sealed class HistoryOverviewModel : HistoryPageModel
{
    /// <summary>The header tags to show a column of, in column order.</summary>
    public IReadOnlyList<string> OverviewDisplayHeaders { get; set; } = [];

    /// <summary>One line per version, newest first.</summary>
    public IReadOnlyList<HistoryOverviewRow> OverviewRows { get; set; } = [];
}

/// <summary>One version: which types changed in it, and by how much.</summary>
public sealed class HistoryStateModel : HistoryPageModel
{
    /// <summary>The version being shown.</summary>
    public long CurrentStateVersion { get; set; }

    /// <summary>The version after this one, or none.</summary>
    public long NextStateVersion { get; set; }

    /// <summary>The version before this one, or none.</summary>
    public long PreviousStateVersion { get; set; }

    /// <summary>One line per type that changed.</summary>
    public IReadOnlyList<HistoryStateTypeChangeSummary> TypeChanges { get; set; } = [];
}

/// <summary>One type in one version: every record of it that changed.</summary>
public sealed class HistoryStateTypeModel : HistoryPageModel
{
    /// <summary>The records that changed, grouped as the reader asked.</summary>
    public required HistoryStateTypeChanges TypeChange { get; set; }

    /// <summary>The grouping in force, as it goes into a link.</summary>
    public string GroupBy { get; set; } = "";

    /// <summary>The key fields not already grouped by, which the reader can add.</summary>
    public IReadOnlyList<string> GroupByOptions { get; set; } = [];
}

/// <summary>
/// One group of a type's changed records, fetched on its own when the reader opens it.
/// </summary>
/// <remarks>This is a fragment rather than a page: no header, no footer, no layout.</remarks>
public sealed class HistoryStateTypeExpandGroupModel : HistoryPageModel
{
    /// <summary>The group being opened.</summary>
    public required RecordDiffTreeNode ExpandedNode { get; set; }

    /// <summary>The version the group is from.</summary>
    public long Version { get; set; }

    /// <summary>The type the group is of.</summary>
    public string TypeName { get; set; } = "";
}

/// <summary>What a search for a key turned up, version by version.</summary>
public sealed class HistoryQueryModel : HistoryPageModel
{
    /// <summary>What was searched for.</summary>
    public string Query { get; set; } = "";

    /// <summary>One entry per version that has a match, newest first.</summary>
    public IReadOnlyList<HistoryStateQueryMatches> StateQueryMatchesList { get; set; } = [];
}

/// <summary>
/// A group of changed records, as the grid of links the pages draw them as.
/// </summary>
/// <remarks>
/// The same grid appears on the type page, in an opened group and in a search result, so it is a
/// partial rather than the three copies Java's templates carry.
/// </remarks>
/// <param name="BasePath">Where the history UI is mounted.</param>
/// <param name="Version">The version the records changed in.</param>
/// <param name="TypeName">The records' type.</param>
/// <param name="Records">The records themselves.</param>
public sealed record RecordDiffListModel(
    string BasePath, long Version, string TypeName, IReadOnlyList<RecordDiff> Records);

/// <summary>
/// A group's subgroups, as the list of openable links the pages draw them as.
/// </summary>
/// <param name="SubGroups">The subgroups.</param>
public sealed record RecordDiffGroupsModel(IReadOnlyCollection<RecordDiffTreeNode> SubGroups);

/// <summary>One record as it stood on either side of one transition.</summary>
public sealed class HistoricalObjectModel : HistoryPageModel
{
    /// <summary>The version being shown.</summary>
    public long Version { get; set; }

    /// <summary>The record's type.</summary>
    public string TypeName { get; set; } = "";

    /// <summary>The record's key.</summary>
    public int KeyOrdinal { get; set; }

    /// <summary>The rows visible when the page is first drawn.</summary>
    public IReadOnlyList<DiffViewRowDisplay> Rows { get; set; } = [];

    /// <summary>Every version that changed this record, newest first.</summary>
    public IReadOnlyList<HistoricalObjectChangeVersion> ChangeVersions { get; set; } = [];
}
