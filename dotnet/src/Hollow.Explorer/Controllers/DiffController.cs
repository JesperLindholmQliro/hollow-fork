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
using System.Text;
using Hollow.Core.Tools.Diff;
using Hollow.Explorer.Diff;
using Hollow.Explorer.Diff.Models;
using Hollow.Explorer.Diff.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace Hollow.Explorer.Controllers;

/// <summary>
/// The diff's four pages, and the two requests the open-and-close script makes.
/// </summary>
/// <remarks>
/// Java gives each page a class of its own, because each has to build a Velocity context and merge
/// three templates. Here the framework does both, so what is left of each page is one method.
/// </remarks>
/// <param name="diffUI">The diff being shown.</param>
/// <param name="options">Where the diff UI is mounted.</param>
/// <param name="sessions">Where a reader's place in each list, and their open record pair, are kept.</param>
public sealed class DiffController(
    HollowDiffUI diffUI,
    HollowDiffUIOptions options,
    DiffSessionStore sessions) : Controller
{
    /// <summary>How many entries a list shows at once.</summary>
    private const int DefaultPageSize = 25;

    /// <summary>Every type in the diff, and how far apart the two states are in each.</summary>
    /// <param name="sortBy">Which column to order by.</param>
    [HttpGet]
    public IActionResult Overview(string? sortBy)
    {
        DiffSession session = sessions.Get(HttpContext);
        string sort = session.Parameter("overview", "sortBy", sortBy, "diffs");

        List<DiffOverviewTypeEntry> entries = [.. diffUI.Diff.TypeDiffs.Select(Describe)];
        SortOverview(entries, sort);

        return View(Fill(new DiffOverviewModel { TypeOverviewEntries = entries }));
    }

    /// <summary>One type: which of its fields moved, which records moved, and which are unmatched.</summary>
    [HttpGet]
    public IActionResult TypeDiff(
        string? type,
        string? diffPairBeginIdx,
        string? diffPairPageSize,
        string? unmatchedFromBeginIdx,
        string? unmatchedToBeginIdx,
        string? unmatchedPageSize,
        string? showFields)
    {
        if (type is null || diffUI.GetTypeDiff(type) is not { } typeDiff)
        {
            return NotFound();
        }

        DiffSession session = sessions.Get(HttpContext);

        int pairBegin = session.IntParameter(type, "diffPairBeginIdx", diffPairBeginIdx, 0);
        int pairPageSize = session.IntParameter(type, "diffPairPageSize", diffPairPageSize, DefaultPageSize);
        int fromBegin = session.IntParameter(type, "unmatchedFromBeginIdx", unmatchedFromBeginIdx, 0);
        int toBegin = session.IntParameter(type, "unmatchedToBeginIdx", unmatchedToBeginIdx, 0);
        int unmatchedSize = session.IntParameter(type, "unmatchedPageSize", unmatchedPageSize, DefaultPageSize);
        bool fieldsShown = session.BoolParameter(type, "showFields", showFields, true);

        IReadOnlyList<ObjectPairDiffScore> pairs = diffUI.GetObjectPairDiffScores(typeDiff);
        IReadOnlyList<UnmatchedObject> unmatchedFrom = diffUI.GetUnmatchedInFrom(typeDiff);
        IReadOnlyList<UnmatchedObject> unmatchedTo = diffUI.GetUnmatchedInTo(typeDiff);

        List<FieldDiffScore> fieldDiffs =
        [
            .. typeDiff.FieldDiffs.Select((fieldDiff, index) => new FieldDiffScore(
                typeDiff.TypeName,
                index,
                fieldDiff.FieldIdentifier.ToString(),
                fieldDiff.NumDiffs,
                typeDiff.TotalNumberOfMatches,
                fieldDiff.TotalDiffScore)),
        ];

        fieldDiffs.Sort();

        return View(Fill(
            new DiffTypeModel
            {
                TypeName = typeDiff.TypeName,
                UnmatchedInFromCount = typeDiff.UnmatchedOrdinalsInFrom.Count,
                UnmatchedInToCount = typeDiff.UnmatchedOrdinalsInTo.Count,
                TotalInFrom = typeDiff.TotalItemsInFromState,
                TotalInTo = typeDiff.TotalItemsInToState,
                NumObjectsDiff = pairs.Count,
                NumFieldsWithDiffs = typeDiff.FieldDiffs.Count,
                ShowFields = fieldsShown,
                FieldDiffs = fieldDiffs,
                ObjectScorePairs = Page(pairs, pairBegin, pairPageSize),
                UnmatchedFromObjects = Page(unmatchedFrom, fromBegin, unmatchedSize),
                UnmatchedToObjects = Page(unmatchedTo, toBegin, unmatchedSize),
                PreviousDiffPairPageBeginIndex = PreviousBegin(pairBegin, pairPageSize),
                NextDiffPairPageBeginIndex = NextBegin(pairBegin, pairPageSize, pairs.Count),
                PreviousUnmatchedFromPageBeginIndex = PreviousBegin(fromBegin, unmatchedSize),
                NextUnmatchedFromPageBeginIndex = NextBegin(fromBegin, unmatchedSize, unmatchedFrom.Count),
                PreviousUnmatchedToPageBeginIndex = PreviousBegin(toBegin, unmatchedSize),
                NextUnmatchedToPageBeginIndex = NextBegin(toBegin, unmatchedSize, unmatchedTo.Count),
            },
            Breadcrumbs(typeDiff.TypeName)));
    }

    /// <summary>One field: the record pairs it differs in, widest apart first.</summary>
    [HttpGet]
    public IActionResult FieldDiff(string? type, int fieldIdx, string? diffPairBeginIdx, string? diffPairPageSize)
    {
        if (type is null
            || diffUI.GetTypeDiff(type) is not { } typeDiff
            || fieldIdx < 0
            || fieldIdx >= typeDiff.FieldDiffs.Count)
        {
            return NotFound();
        }

        HollowFieldDiff fieldDiff = typeDiff.FieldDiffs[fieldIdx];
        DiffSession session = sessions.Get(HttpContext);

        string pageContext = type + ":" + fieldIdx.ToString(CultureInfo.InvariantCulture);
        int pairBegin = session.IntParameter(pageContext, "diffPairBeginIdx", diffPairBeginIdx, 0);
        int pairPageSize = session.IntParameter(pageContext, "diffPairPageSize", diffPairPageSize, DefaultPageSize);

        List<ObjectPairDiffScore> pairs = [];

        for (int i = 0; i < fieldDiff.NumDiffs; i++)
        {
            int fromOrdinal = fieldDiff.GetFromOrdinal(i);
            ObjectPairDiffScore pair = new(
                typeDiff.FromTypeState is { } fromState
                    ? typeDiff.Matcher.GetKeyDisplayString(fromState, fromOrdinal)
                    : "",
                fromOrdinal,
                fieldDiff.GetToOrdinal(i));

            pair.IncrementDiffScore(fieldDiff.GetPairScore(i));
            pairs.Add(pair);
        }

        pairs.Sort();

        return View(Fill(
            new DiffFieldModel
            {
                TypeName = typeDiff.TypeName,
                FieldIndex = fieldIdx,
                ObjectScorePairs = Page(pairs, pairBegin, pairPageSize),
                PreviousDiffPairPageBeginIndex = PreviousBegin(pairBegin, pairPageSize),
                NextDiffPairPageBeginIndex = NextBegin(pairBegin, pairPageSize, fieldDiff.NumDiffs),
            },
            Breadcrumbs(typeDiff.TypeName, fieldDiff.FieldIdentifier.ToString())));
    }

    /// <summary>Two records, side by side.</summary>
    [HttpGet]
    public IActionResult ObjectDiff(string? type, int fromOrdinal, int toOrdinal, int? fieldIdx)
    {
        if (type is null || diffUI.GetTypeDiff(type) is not { } typeDiff)
        {
            return NotFound();
        }

        HollowDiffView view = GetDiffView(type, fromOrdinal, toOrdinal);

        string? fieldName = fieldIdx is { } index && index >= 0 && index < typeDiff.FieldDiffs.Count
            ? typeDiff.FieldDiffs[index].FieldIdentifier.ToString()
            : null;

        return View(Fill(
            new DiffObjectModel
            {
                TypeName = type,
                FromOrdinal = fromOrdinal,
                ToOrdinal = toOrdinal,
                Rows = [.. DiffViewRowRenderer.VisibleRows(view.RootRow)],
            },
            Breadcrumbs(
                typeDiff.TypeName,
                fieldName,
                fieldIdx,
                diffUI.GetDisplayKey(typeDiff, fromOrdinal, toOrdinal))));
    }

    /// <summary>
    /// Opens a row, answering with the rows that became visible.
    /// </summary>
    /// <remarks>
    /// The answer is the pipe-delimited form the page's script reads, not HTML: the script builds the
    /// cells itself, so sending markup would mean sending the same thing twice.
    /// </remarks>
    [HttpGet]
    public IActionResult DiffRowData(string? type, int fromOrdinal, int toOrdinal, string? row)
    {
        if (type is null || diffUI.GetTypeDiff(type) is null)
        {
            return NotFound();
        }

        HollowDiffView view = GetDiffView(type, fromOrdinal, toOrdinal);

        if (DiffViewRowRenderer.FindRow(view.RootRow, row) is not { } parentRow)
        {
            return NotFound();
        }

        foreach (HollowDiffViewRow child in parentRow.Children)
        {
            child.IsVisible = true;
        }

        StringBuilder builder = new();

        using (StringWriter writer = new(builder, CultureInfo.InvariantCulture))
        {
            DiffViewRowRenderer.WriteDelimited(DiffViewRowRenderer.VisibleRows(parentRow), writer);
        }

        return Content(builder.ToString(), "text/plain", Encoding.UTF8);
    }

    /// <summary>Closes a row, hiding everything under it.</summary>
    [HttpGet]
    public IActionResult CollapseDiffRow(string? type, int fromOrdinal, int toOrdinal, string? row)
    {
        if (type is null || diffUI.GetTypeDiff(type) is null)
        {
            return NotFound();
        }

        HollowDiffView view = GetDiffView(type, fromOrdinal, toOrdinal);

        if (DiffViewRowRenderer.FindRow(view.RootRow, row) is not { } parentRow)
        {
            return NotFound();
        }

        foreach (HollowDiffViewRow child in parentRow.Children)
        {
            child.IsVisible = false;
        }

        return Content("ok", "text/plain", Encoding.UTF8);
    }

    /// <summary>
    /// The stylesheet and the three images the record view draws its margins with.
    /// </summary>
    /// <remarks>
    /// These ride along in the assembly rather than sitting in a <c>wwwroot</c>, so that an application
    /// embedding the diff gets a working page without having to serve static files or copy anything
    /// into its own content root.
    /// </remarks>
    [HttpGet]
    public IActionResult Resource(string name)
    {
        // The name indexes a fixed set rather than a path, so nothing a caller writes reaches the file
        // system or the manifest as text.
        string? contentType = name switch
        {
            "diffview.css" => "text/css",
            "expand.png" or "collapse.png" or "partial_expand.png" => "image/png",
            _ => null,
        };

        if (contentType is null)
        {
            return NotFound();
        }

        Stream? stream = typeof(DiffController).Assembly
            .GetManifestResourceStream("Hollow.Explorer.Diff.Resources." + name);

        return stream is null ? NotFound() : File(stream, contentType);
    }

    private static IReadOnlyList<T> Page<T>(IReadOnlyList<T> list, int begin, int pageSize)
    {
        // A begin index past the end usually means the list shrank under a remembered position; showing
        // the first page is more use than showing nothing.
        int from = begin >= list.Count || begin < 0 ? 0 : begin;
        int count = Math.Clamp(pageSize, 0, list.Count - from);

        return [.. list.Skip(from).Take(count)];
    }

    private static int? PreviousBegin(int begin, int pageSize) => begin > 0 ? Math.Max(0, begin - pageSize) : null;

    private static int? NextBegin(int begin, int pageSize, int total) =>
        begin + pageSize < total ? begin + pageSize : null;

    private static DiffOverviewTypeEntry Describe(HollowTypeDiff typeDiff) =>
        new()
        {
            TypeName = typeDiff.TypeName,
            HasUniqueKey = typeDiff.HasMatchPaths,
            TotalDiffScore = typeDiff.TotalDiffScore,
            UnmatchedInFrom = typeDiff.UnmatchedOrdinalsInFrom.Count,
            UnmatchedInTo = typeDiff.UnmatchedOrdinalsInTo.Count,
            TotalInFrom = typeDiff.TotalItemsInFromState,
            TotalInTo = typeDiff.TotalItemsInToState,
            HeapInFrom = typeDiff.FromTypeState?.ApproxHeapFootprintInBytes ?? 0,
            HeapInTo = typeDiff.ToTypeState?.ApproxHeapFootprintInBytes ?? 0,
            HoleInFrom = typeDiff.FromTypeState?.ApproxHoleCostInBytes ?? 0,
            HoleInTo = typeDiff.ToTypeState?.ApproxHoleCostInBytes ?? 0,
        };

    /// <summary>
    /// Orders the overview, which is the one place the reader chooses what "worth looking at" means.
    /// </summary>
    /// <remarks>
    /// The default ordering is the interesting one: it ranks by how far apart the two states are, and
    /// falls through a chain of tie-breakers so that a type with no key or no records at all sinks
    /// below one that genuinely differs. Every other ordering is a single column, largest first.
    /// </remarks>
    private static void SortOverview(List<DiffOverviewTypeEntry> entries, string sortBy)
    {
        switch (sortBy)
        {
            case "unmatchedFrom":
                entries.Sort((a, b) => b.UnmatchedInFrom.CompareTo(a.UnmatchedInFrom));
                break;
            case "unmatchedTo":
                entries.Sort((a, b) => b.UnmatchedInTo.CompareTo(a.UnmatchedInTo));
                break;
            case "fromCount":
                entries.Sort((a, b) => b.TotalInFrom.CompareTo(a.TotalInFrom));
                break;
            case "toCount":
                entries.Sort((a, b) => b.TotalInTo.CompareTo(a.TotalInTo));
                break;
            case "fromHeap":
                entries.Sort((a, b) => b.HeapInFrom.CompareTo(a.HeapInFrom));
                break;
            case "toHeap":
                entries.Sort((a, b) => b.HeapInTo.CompareTo(a.HeapInTo));
                break;
            case "fromHole":
                entries.Sort((a, b) => b.HoleInFrom.CompareTo(a.HoleInFrom));
                break;
            case "toHole":
                entries.Sort((a, b) => b.HoleInTo.CompareTo(a.HoleInTo));
                break;
            default:
                entries.Sort(CompareByDiffs);
                break;
        }
    }

    private static int CompareByDiffs(DiffOverviewTypeEntry a, DiffOverviewTypeEntry b)
    {
        int result = b.TotalDiffScore.CompareTo(a.TotalDiffScore);

        if (result == 0)
        {
            result = b.DeltaSize.CompareTo(a.DeltaSize);
        }

        if (result == 0)
        {
            result = b.HasData.CompareTo(a.HasData);
        }

        if (result == 0)
        {
            result = b.HasUnmatched.CompareTo(a.HasUnmatched);
        }

        if (result == 0)
        {
            result = b.HasUniqueKey.CompareTo(a.HasUniqueKey);
        }

        // Two types that are equally worth looking at are shown in a stable order rather than whichever
        // the sort happened to produce.
        return result != 0 ? result : string.CompareOrdinal(a.TypeName, b.TypeName);
    }

    private HollowDiffView GetDiffView(string type, int fromOrdinal, int toOrdinal) =>
        sessions.Get(HttpContext).GetOrCreateDiffView(
            type,
            fromOrdinal,
            toOrdinal,
            () => new HollowDiffView(
                type,
                fromOrdinal,
                toOrdinal,
                new HollowObjectDiffViewGenerator(
                    diffUI.Diff.FromStateEngine,
                    diffUI.Diff.ToStateEngine,
                    diffUI,
                    type,
                    fromOrdinal,
                    toOrdinal).GetHollowDiffViewRows(),
                diffUI.ExactRecordMatcher));

    private List<DiffBreadcrumb> Breadcrumbs(
        string? typeName = null,
        string? fieldName = null,
        int? fieldIndex = null,
        string? displayKey = null)
    {
        string path = options.BasePath;
        List<DiffBreadcrumb> breadcrumbs = [new DiffBreadcrumb(path.Length == 0 ? "/" : path, "Overview")];

        if (typeName is null)
        {
            return breadcrumbs;
        }

        breadcrumbs.Add(new DiffBreadcrumb(
            $"{path}/typediff?type={Uri.EscapeDataString(typeName)}",
            typeName));

        if (fieldName is not null && fieldIndex is { } index)
        {
            breadcrumbs.Add(new DiffBreadcrumb(
                $"{path}/fielddiff?type={Uri.EscapeDataString(typeName)}&fieldIdx={index.ToString(CultureInfo.InvariantCulture)}",
                fieldName));
        }
        else if (fieldName is not null)
        {
            breadcrumbs.Add(new DiffBreadcrumb(null, fieldName));
        }

        if (displayKey is not null)
        {
            breadcrumbs.Add(new DiffBreadcrumb(null, displayKey));
        }

        return breadcrumbs;
    }

    private TModel Fill<TModel>(TModel model, IReadOnlyList<DiffBreadcrumb>? breadcrumbs = null)
        where TModel : DiffPageModel
    {
        long heapFrom = diffUI.Diff.FromStateEngine.ApproxDataSize;
        long heapTo = diffUI.Diff.ToStateEngine.ApproxDataSize;
        long heapDiff = heapTo - heapFrom;

        model.BasePath = options.BasePath;
        model.FromBlobName = diffUI.FromBlobName;
        model.ToBlobName = diffUI.ToBlobName;
        model.FromHeap = ByteSize.Format(heapFrom);
        model.ToHeap = ByteSize.Format(heapTo);
        model.DiffHeap = (heapDiff > 0 ? "+" : "") + ByteSize.Format(heapDiff);
        model.DiffHeapIncreased = heapDiff > 0;
        model.Breadcrumbs = breadcrumbs ?? [];
        model.HeaderEntries = diffUI.GetHeaderEntries();
        model.Environment = Request.Cookies["env"] ?? "";
        model.IsHeaderEnabled = bool.TryParse(Request.Cookies["isHeaderEnabled"], out bool enabled) && enabled;

        return model;
    }
}
