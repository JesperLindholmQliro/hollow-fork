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

using Hollow.Explorer.Diff.Models;

namespace Hollow.Explorer.Diff.ViewModels;

/// <summary>
/// What every page of the diff needs: what the two states are called, what they cost, and the trail
/// back to the overview.
/// </summary>
/// <remarks>
/// Java puts these into the Velocity context in a base page class, which merges a header template
/// before the page and a footer after it. A layout is the same arrangement without the page having to
/// name the two halves.
/// </remarks>
public abstract class DiffPageModel
{
    /// <summary>Where the diff UI is mounted, which every link on the page is relative to.</summary>
    public string BasePath { get; set; } = "";

    /// <summary>What the earlier state is called.</summary>
    public string FromBlobName { get; set; } = "";

    /// <summary>What the later state is called.</summary>
    public string ToBlobName { get; set; } = "";

    /// <summary>What the earlier state costs, as something a person reads at a glance.</summary>
    public string FromHeap { get; set; } = "";

    /// <summary>What the later state costs.</summary>
    public string ToHeap { get; set; } = "";

    /// <summary>What the later state costs over the earlier one, signed.</summary>
    public string DiffHeap { get; set; } = "";

    /// <summary>Whether <see cref="DiffHeap"/> should read as a gain or a saving.</summary>
    public bool DiffHeapIncreased { get; set; }

    /// <summary>The trail back to the overview.</summary>
    public IReadOnlyList<DiffBreadcrumb> Breadcrumbs { get; set; } = [];

    /// <summary>Every header tag either blob recorded.</summary>
    public IReadOnlyList<DiffHeaderEntry> HeaderEntries { get; set; } = [];

    /// <summary>
    /// The environment the reader said they are looking at, which colours the banner.
    /// </summary>
    /// <remarks>Taken from a cookie, as Java does, so that it survives across pages.</remarks>
    public string Environment { get; set; } = "";

    /// <summary>Whether the banner naming the environment is shown at all.</summary>
    public bool IsHeaderEnabled { get; set; }
}

/// <summary>Every type in the diff, and how far apart the two states are in each.</summary>
public sealed class DiffOverviewModel : DiffPageModel
{
    /// <summary>The types, in the order the reader asked for.</summary>
    public required IReadOnlyList<DiffOverviewTypeEntry> TypeOverviewEntries { get; init; }
}

/// <summary>One type: which of its fields moved, which records moved, and which have no counterpart.</summary>
public sealed class DiffTypeModel : DiffPageModel
{
    /// <summary>The type this page is about.</summary>
    public required string TypeName { get; init; }

    /// <summary>Records in the earlier state with no counterpart.</summary>
    public required int UnmatchedInFromCount { get; init; }

    /// <summary>Records in the later state with no counterpart.</summary>
    public required int UnmatchedInToCount { get; init; }

    /// <summary>Records in the earlier state.</summary>
    public required int TotalInFrom { get; init; }

    /// <summary>Records in the later state.</summary>
    public required int TotalInTo { get; init; }

    /// <summary>Matched pairs that differ at all.</summary>
    public required int NumObjectsDiff { get; init; }

    /// <summary>Fields that differ in at least one pair.</summary>
    public required int NumFieldsWithDiffs { get; init; }

    /// <summary>Whether the list of fields is expanded.</summary>
    public required bool ShowFields { get; init; }

    /// <summary>Those fields, the most widely affected first.</summary>
    public required IReadOnlyList<FieldDiffScore> FieldDiffs { get; init; }

    /// <summary>The page of differing pairs being shown.</summary>
    public required IReadOnlyList<ObjectPairDiffScore> ObjectScorePairs { get; init; }

    /// <summary>The page of records the later state does not have.</summary>
    public required IReadOnlyList<UnmatchedObject> UnmatchedFromObjects { get; init; }

    /// <summary>The page of records the earlier state does not have.</summary>
    public required IReadOnlyList<UnmatchedObject> UnmatchedToObjects { get; init; }

    /// <summary>Where the previous page of pairs starts, if there is one.</summary>
    public int? PreviousDiffPairPageBeginIndex { get; init; }

    /// <summary>Where the next page of pairs starts, if there is one.</summary>
    public int? NextDiffPairPageBeginIndex { get; init; }

    /// <summary>Where the previous page of earlier-only records starts, if there is one.</summary>
    public int? PreviousUnmatchedFromPageBeginIndex { get; init; }

    /// <summary>Where the next page of earlier-only records starts, if there is one.</summary>
    public int? NextUnmatchedFromPageBeginIndex { get; init; }

    /// <summary>Where the previous page of later-only records starts, if there is one.</summary>
    public int? PreviousUnmatchedToPageBeginIndex { get; init; }

    /// <summary>Where the next page of later-only records starts, if there is one.</summary>
    public int? NextUnmatchedToPageBeginIndex { get; init; }
}

/// <summary>One field: the record pairs it differs in, widest apart first.</summary>
public sealed class DiffFieldModel : DiffPageModel
{
    /// <summary>The type the field belongs to.</summary>
    public required string TypeName { get; init; }

    /// <summary>Where the field sits in the type's list of differing fields.</summary>
    public required int FieldIndex { get; init; }

    /// <summary>The page of pairs being shown.</summary>
    public required IReadOnlyList<ObjectPairDiffScore> ObjectScorePairs { get; init; }

    /// <summary>Where the previous page starts, if there is one.</summary>
    public int? PreviousDiffPairPageBeginIndex { get; init; }

    /// <summary>Where the next page starts, if there is one.</summary>
    public int? NextDiffPairPageBeginIndex { get; init; }
}

/// <summary>Two records, side by side.</summary>
public sealed class DiffObjectModel : DiffPageModel
{
    /// <summary>The type both records are of.</summary>
    public required string TypeName { get; init; }

    /// <summary>The record in the earlier state, or -1.</summary>
    public required int FromOrdinal { get; init; }

    /// <summary>The record in the later state, or -1.</summary>
    public required int ToOrdinal { get; init; }

    /// <summary>The rows the view starts open at.</summary>
    public required IReadOnlyList<DiffViewRowDisplay> Rows { get; init; }
}
