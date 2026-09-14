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
using Hollow.Core;
using Hollow.Core.Tools.History;
using Hollow.Core.Tools.History.KeyIndex;
using Hollow.Core.Util;
using Hollow.Explorer.Diff;
using Hollow.Explorer.History;
using Hollow.Explorer.History.Models;
using Hollow.Explorer.History.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace Hollow.Explorer.Controllers;

/// <summary>
/// The history's pages: the versions, one version, one type in it, one record, and a search.
/// </summary>
/// <remarks>
/// Java has a page class per template and a router dispatching on the first path segment. An MVC
/// controller is the same arrangement with the routing already written.
/// </remarks>
/// <param name="historyUI">The history being shown.</param>
/// <param name="options">Where the pages are mounted.</param>
/// <param name="sessions">Where a reader's place is kept.</param>
public sealed class HistoryController(
    HollowHistoryUI historyUI, HollowHistoryUIOptions options, HistorySessionStore sessions) : Controller
{
    /// <summary>
    /// The header tag a producer leaves when a transition resharded a type.
    /// </summary>
    private const string ReshardingInvokedHeaderTag = "hollow.type.resharding.invoked";

    /// <summary>Every version the history holds, newest first.</summary>
    [HttpGet]
    public IActionResult Overview()
    {
        List<HistoryOverviewRow> rows = [];

        foreach (HollowHistoricalState state in historyUI.History.HistoricalStates)
        {
            ChangeBreakdown total = new();
            Dictionary<string, ChangeBreakdown> byType = new(StringComparer.Ordinal);

            foreach ((string type, HollowHistoricalStateTypeKeyOrdinalMapping mapping)
                in state.KeyOrdinalMapping.TypeMappings)
            {
                byType[type] = new ChangeBreakdown(mapping);
                total.Add(mapping);
            }

            IReadOnlyDictionary<string, string> nextTags = historyUI.NextStateHeaderTags(state);

            rows.Add(new HistoryOverviewRow(
                VersionTimestampConverter.GetTimestamp(state.Version, historyUI.TimeZone),
                state.Version,
                total,
                byType,
                [.. historyUI.OverviewDisplayHeaders.Select(nextTags.GetValueOrDefault)],
                nextTags.GetValueOrDefault(ReshardingInvokedHeaderTag)));
        }

        return View(Fill(
            new HistoryOverviewModel
            {
                OverviewDisplayHeaders = historyUI.OverviewDisplayHeaders,
                OverviewRows = rows,
            },
            showHomeLink: false));
    }

    /// <summary>One version: which types changed in it.</summary>
    [HttpGet]
    public IActionResult State(long version)
    {
        if (historyUI.History.GetHistoricalState(version) is not { } state)
        {
            return NotFound();
        }

        return View(Fill(
            new HistoryStateModel
            {
                CurrentStateVersion = state.Version,
                NextStateVersion = state.NextState?.Version ?? HollowConstants.VersionNone,
                PreviousStateVersion = PreviousStateVersion(state),
                TypeChanges =
                [
                    .. state.KeyOrdinalMapping.TypeMappings
                        .Select(entry => new HistoryStateTypeChangeSummary(
                            state.Version, entry.Key, entry.Value))
                        .Where(summary => !summary.IsEmpty),
                ],
            },
            state));
    }

    /// <summary>One type in one version: every record of it that changed.</summary>
    [HttpGet]
    public IActionResult StateType(long version, string? type, string? groupBy)
    {
        if (type is null || historyUI.History.GetHistoricalState(version) is not { } state)
        {
            return NotFound();
        }

        if (state.KeyOrdinalMapping.GetTypeMapping(type) is not { } typeMapping)
        {
            return NotFound();
        }

        HistoryStateTypeChanges typeChange = GetStateTypeChanges(state, type, groupBy);

        return View(Fill(
            new HistoryStateTypeModel
            {
                TypeChange = typeChange,
                GroupBy = groupBy ?? "",
                GroupByOptions =
                [
                    .. typeMapping.KeyIndex.KeyFields
                        .Except(typeChange.GroupedFieldNames, StringComparer.Ordinal),
                ],
            },
            state));
    }

    /// <summary>
    /// One group of a type's changed records, fetched on its own when the reader opens it.
    /// </summary>
    [HttpGet]
    public IActionResult StateTypeExpand(long version, string? type, string? groupBy, string? expandGroupId)
    {
        if (type is null
            || expandGroupId is null
            || historyUI.History.GetHistoricalState(version) is not { } state)
        {
            return NotFound();
        }

        HistoryStateTypeChanges typeChange = GetStateTypeChanges(state, type, groupBy);

        if (typeChange.FindTreeNode(expandGroupId) is not { } node)
        {
            return NotFound();
        }

        return PartialView(
            "StateTypeExpandGroup",
            Fill(
                new HistoryStateTypeExpandGroupModel
                {
                    ExpandedNode = node,
                    Version = version,
                    TypeName = type,
                }));
    }

    /// <summary>What a search for a key turned up, version by version.</summary>
    [HttpGet]
    public IActionResult Query(string? query)
    {
        string text = query ?? "";

        Dictionary<string, IntList> matchesByType = new(StringComparer.Ordinal);

        foreach ((string type, HollowHistoryTypeKeyIndex index) in historyUI.History.KeyIndex.TypeKeyIndexes)
        {
            IntList matches = index.QueryIndexedFields(text);

            if (matches.Count > 0)
            {
                matchesByType[type] = matches;
            }
        }

        return View(Fill(
            new HistoryQueryModel
            {
                Query = text,
                StateQueryMatchesList =
                [
                    .. historyUI.History.HistoricalStates
                        .Select(state => new HistoryStateQueryMatches(
                            state,
                            historyUI.GetHistoryRecordNamer,
                            VersionTimestampConverter.GetTimestamp(state.Version, historyUI.TimeZone),
                            matchesByType))
                        .Where(matches => matches.HasMatches),
                ],
            }));
    }

    /// <summary>One record as it stood on either side of one transition.</summary>
    [HttpGet]
    public IActionResult HistoricalObject(long version, string? type, int keyOrdinal)
    {
        if (type is null || historyUI.History.GetHistoricalState(version) is not { } state)
        {
            return NotFound();
        }

        if (GetHistoryView(state, type, keyOrdinal) is not { } view)
        {
            return NotFound();
        }

        return View(Fill(
            new HistoricalObjectModel
            {
                Version = version,
                TypeName = type,
                KeyOrdinal = keyOrdinal,
                Rows = [.. DiffViewRowRenderer.VisibleRows(view.RootRow)],
                ChangeVersions = ChangeVersions(type, keyOrdinal),
            },
            state));
    }

    /// <summary>Opens a row, returning the rows that became visible.</summary>
    [HttpGet]
    public IActionResult DiffRowData(long version, string? type, int keyOrdinal, string? row)
    {
        if (type is null || historyUI.History.GetHistoricalState(version) is not { } state)
        {
            return NotFound();
        }

        if (GetHistoryView(state, type, keyOrdinal) is not { } view
            || DiffViewRowRenderer.FindRow(view.RootRow, row) is not { } parentRow)
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
    public IActionResult CollapseDiffRow(long version, string? type, int keyOrdinal, string? row)
    {
        if (type is null || historyUI.History.GetHistoricalState(version) is not { } state)
        {
            return NotFound();
        }

        if (GetHistoryView(state, type, keyOrdinal) is not { } view
            || DiffViewRowRenderer.FindRow(view.RootRow, row) is not { } parentRow)
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
    /// The same embedded files the diff UI serves — the record page is the same page, over two states
    /// of one chain rather than two unrelated ones.
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

    private HistoryStateTypeChanges GetStateTypeChanges(
        HollowHistoricalState state, string type, string? groupBy)
    {
        string[] groupedFieldNames = groupBy is null or ""
            ? []
            : groupBy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return sessions.Get(HttpContext).GetOrCreateStateTypeChanges(
            state.Version,
            type,
            groupedFieldNames,
            () => new HistoryStateTypeChanges(
                state, type, historyUI.GetHistoryRecordNamer(type), groupedFieldNames));
    }

    private HollowHistoryView? GetHistoryView(HollowHistoricalState state, string type, int keyOrdinal)
    {
        if (state.KeyOrdinalMapping.GetTypeMapping(type) is not { } typeMapping)
        {
            return null;
        }

        int fromOrdinal = typeMapping.FindRemovedOrdinal(keyOrdinal);
        int toOrdinal = typeMapping.FindAddedOrdinal(keyOrdinal);

        // A key this state did not touch names no record here; the reader has followed a stale link.
        if (fromOrdinal == HollowConstants.OrdinalNone && toOrdinal == HollowConstants.OrdinalNone)
        {
            return null;
        }

        return sessions.Get(HttpContext).GetOrCreateHistoryView(
            state.Version,
            type,
            keyOrdinal,
            historyUI.History.LatestState.RandomizedTag,
            () => new HollowHistoryView(
                state.Version,
                type,
                keyOrdinal,
                historyUI.History.LatestState.RandomizedTag,

                // Both sides come from the same state: it holds what went, and forwards to the live
                // state for what stayed.
                new HollowObjectDiffViewGenerator(
                        state.DataAccess, state.DataAccess, historyUI, type, fromOrdinal, toOrdinal)
                    .GetHollowDiffViewRows(),
                historyUI.ExactRecordMatcher));
    }

    private IReadOnlyList<HistoricalObjectChangeVersion> ChangeVersions(string type, int keyOrdinal) =>
        [
            .. historyUI.History.HistoricalStates
                .Where(state =>
                    state.KeyOrdinalMapping.GetTypeMapping(type) is { } mapping
                    && (mapping.FindAddedOrdinal(keyOrdinal) != HollowConstants.OrdinalNone
                        || mapping.FindRemovedOrdinal(keyOrdinal) != HollowConstants.OrdinalNone))
                .Select(state => new HistoricalObjectChangeVersion(
                    state.Version,
                    VersionTimestampConverter.GetTimestamp(state.Version, historyUI.TimeZone))),
        ];

    /// <summary>
    /// The version whose transition led into <paramref name="state"/>, or none.
    /// </summary>
    private long PreviousStateVersion(HollowHistoricalState state) =>
        historyUI.History.HistoricalStates
            .FirstOrDefault(candidate => ReferenceEquals(candidate.NextState, state))
            ?.Version
        ?? HollowConstants.VersionNone;

    private TModel Fill<TModel>(TModel model, HollowHistoricalState state)
        where TModel : HistoryPageModel
    {
        Fill(model);
        model.HeaderEntries = historyUI.GetHeaderEntries(state);

        return model;
    }

    private TModel Fill<TModel>(TModel model, bool showHomeLink = true)
        where TModel : HistoryPageModel
    {
        model.BasePath = options.BasePath;
        model.ShowHomeLink = showHomeLink;
        model.CommonHeaderEntries =
        [
            .. historyUI.CommonHeaderEntries.Values
                .OrderBy(entry => entry.Position)
                .Select(entry => entry.Value),
        ];

        return model;
    }
}
