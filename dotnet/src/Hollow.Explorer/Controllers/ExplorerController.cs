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

using Hollow.Core;
using Hollow.Core.Index.Key;
using Hollow.Core.Read.Engine;
using Hollow.Core.Schema;
using Hollow.Core.Tools.Util;
using Hollow.Core.Util;
using Hollow.Explorer.Models;
using Hollow.Explorer.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace Hollow.Explorer.Controllers;

/// <summary>
/// The explorer's four pages.
/// </summary>
/// <remarks>
/// Java gives each page a class of its own, because each has to build a Velocity context and merge
/// three templates. Here the framework does both, so what is left of each page is one method.
/// </remarks>
/// <param name="explorer">The dataset being explored.</param>
/// <param name="options">Where the explorer is mounted.</param>
/// <param name="sessions">Where a reader's search and open schema branches are kept.</param>
public sealed class ExplorerController(
    HollowExplorer explorer,
    HollowExplorerOptions options,
    ExplorerSessionStore sessions) : Controller
{
    /// <summary>Every type in the dataset, with what it holds and what it costs.</summary>
    /// <param name="sort">Which column to order by.</param>
    [HttpGet]
    public IActionResult Home(string? sort)
    {
        explorer.PrefillHeapStatsCache();

        HollowReadStateEngine stateEngine = explorer.StateEngine;
        List<TypeOverview> typeOverviews = [];

        foreach (HollowTypeReadState typeState in stateEngine.TypeStates.Values)
        {
            BitSet populatedOrdinals = typeState.PopulatedOrdinals;
            HollowExplorer.HeapStats stats = explorer.GetHeapStats(typeState);

            typeOverviews.Add(new TypeOverview(
                typeState.TypeName,
                populatedOrdinals.Cardinality(),
                populatedOrdinals.Length - populatedOrdinals.Cardinality(),
                stats.ApproxHoleFootprint,
                stats.ApproxHeapFootprint,
                SearchUtils.GetPrimaryKey(typeState.Schema),
                typeState.Schema,
                typeState.NumShards));
        }

        return View(new ShowAllTypesModel
        {
            TypeOverviews = Sort(typeOverviews, sort),
            TotalHeapFootprint = ByteSize.Format(typeOverviews.Sum(type => type.ApproxHeapFootprint)),
            TotalHoleFootprint = ByteSize.Format(typeOverviews.Sum(type => type.ApproxHoleFootprint)),
            TopLevelTypes = [.. HollowSchemaUtil.GetTopLevelTypes(stateEngine.Schemas).Order(StringComparer.Ordinal)],
            BasePath = options.BasePath,
            StateVersion = explorer.CurrentStateVersion,
            HeaderDisplayString = explorer.HeaderDisplayString,
            HeaderStringUrl = explorer.HeaderStringUrl,
            CommonHeaderEntries = [.. explorer.CommonHeaderEntries],
        });
    }

    /// <summary>
    /// A page of one type's records, and whichever of them the reader picked, written out in full.
    /// </summary>
    /// <param name="type">The type to browse.</param>
    /// <param name="page">Which page of records, counting from zero.</param>
    /// <param name="pageSize">How many records a page holds.</param>
    /// <param name="display">Whether to write the record as <c>text</c> or <c>json</c>.</param>
    /// <param name="ordinal">The record to show, by ordinal.</param>
    /// <param name="key">The record to show, by key, which wins over the ordinal when they disagree.</param>
    /// <param name="clearQuery">Whether to drop the search narrowing this type first.</param>
    [HttpGet]
    public IActionResult Type(
        string? type,
        int page = 0,
        int pageSize = 20,
        string? display = null,
        int ordinal = HollowConstants.OrdinalNone,
        string? key = null,
        bool clearQuery = false)
    {
        HollowReadStateEngine stateEngine = explorer.StateEngine;

        if (type is null || stateEngine.GetTypeState(type) is not { } typeState)
        {
            return NotFound($"this dataset has no type called {type}");
        }

        ExplorerSession session = sessions.Get(HttpContext);

        if (clearQuery)
        {
            session.ClearQueryResult();
        }

        BitSet selectedOrdinals = typeState.PopulatedOrdinals;
        string? filteredByQuery = null;

        if (session.QueryResult is { } queryResult)
        {
            queryResult.RecalculateIfNotCurrent(stateEngine);

            selectedOrdinals = queryResult.QueryMatches.GetValueOrDefault(type) ?? new BitSet();
            filteredByQuery = queryResult.QueryDisplayString;
        }

        string displayFormat = display == "json" ? "json" : "text";
        PrimaryKey? primaryKey = SearchUtils.GetPrimaryKey(typeState.Schema);
        int[][]? fieldPathIndexes = SearchUtils.GetFieldPathIndexes(stateEngine, primaryKey);

        List<TypeKey> keys = ReadPage(typeState, selectedOrdinals, page, pageSize, fieldPathIndexes);

        key ??= "";
        object?[]? parsedKey = null;

        try
        {
            parsedKey = primaryKey is null ? null : SearchUtils.ParseKey(stateEngine, primaryKey, key);
        }
        catch (ArgumentException)
        {
            // Whatever was typed is not a key of this type, so there is nothing to match it against.
            key = "";
        }

        ordinal = SearchUtils.GetOrdinalToDisplay(
            stateEngine, key, parsedKey, ordinal, selectedOrdinals, fieldPathIndexes, typeState);

        if (ordinal != HollowConstants.OrdinalNone && key.Length == 0 && fieldPathIndexes is not null)
        {
            // The record was picked by ordinal, so fill the search box in with the key it turned out to
            // have — which is what the reader would have had to type to get here.
            key = SearchUtils.BuildKey(typeState, ordinal, fieldPathIndexes, escapeDelimiters: true);
        }

        int numRecords = selectedOrdinals.Cardinality();

        return View(new BrowseSelectedTypeModel
        {
            Type = typeState.TypeName,
            Keys = keys,
            Page = page,
            PageSize = pageSize,
            NumPages = ((numRecords - 1) / pageSize) + 1,
            NumRecords = numRecords,
            Key = key,
            Ordinal = ordinal,
            Display = displayFormat,
            FilteredByQuery = filteredByQuery,
            StateEngine = stateEngine,
            TypeName = typeState.TypeName,
            BasePath = options.BasePath,
            StateVersion = explorer.CurrentStateVersion,
            HeaderDisplayString = explorer.HeaderDisplayString,
            HeaderStringUrl = explorer.HeaderStringUrl,
            CommonHeaderEntries = [.. explorer.CommonHeaderEntries],
        });
    }

    /// <summary>
    /// One or more schemas, as trees whose branches the reader opens a step at a time.
    /// </summary>
    /// <param name="type">The types to show. More than one may be given.</param>
    /// <param name="expand">A field path to open.</param>
    /// <param name="collapse">A field path to close.</param>
    [HttpGet]
    public IActionResult Schema(
        [FromQuery] string[]? type, string? expand = null, string? collapse = null)
    {
        HollowReadStateEngine stateEngine = explorer.StateEngine;

        // A schema page with nothing named would have nothing to show; the types nothing references are
        // where a reader would have started anyway.
        string[] types = type is { Length: > 0 }
            ? type
            : [.. HollowSchemaUtil.GetTopLevelTypes(stateEngine.Schemas).Order(StringComparer.Ordinal)];

        ExplorerSession session = sessions.Get(HttpContext);
        List<SchemaDisplay> schemaDisplays = [];

        foreach (string typeName in types)
        {
            if (stateEngine.GetSchema(typeName) is not { } schema)
            {
                return NotFound($"this dataset has no type called {typeName}");
            }

            SchemaDisplay? schemaDisplay = session.GetSchemaDisplay(typeName);

            if (schemaDisplay is null)
            {
                schemaDisplay = new SchemaDisplay(schema) { IsExpanded = true };
            }

            if (expand is not null)
            {
                schemaDisplay.ExpandOrCollapse(expand, isExpand: true);
            }

            if (collapse is not null)
            {
                schemaDisplay.ExpandOrCollapse(collapse, isExpand: false);
            }

            session.SetSchemaDisplay(typeName, schemaDisplay);
            schemaDisplays.Add(schemaDisplay);
        }

        return View(new BrowseSchemaModel
        {
            SchemaDisplays = schemaDisplays,
            Types = types,
            BasePath = options.BasePath,
            StateVersion = explorer.CurrentStateVersion,
            HeaderDisplayString = explorer.HeaderDisplayString,
            HeaderStringUrl = explorer.HeaderStringUrl,
            CommonHeaderEntries = [.. explorer.CommonHeaderEntries],
        });
    }

    /// <summary>
    /// Searching for a value, and narrowing the search a clause at a time.
    /// </summary>
    /// <param name="type">The type to look in, or <c>ANY TYPE</c>.</param>
    /// <param name="field">The field to look at.</param>
    /// <param name="queryValue">The value to look for, as text.</param>
    /// <param name="clear">Whether to start over.</param>
    [HttpGet]
    public IActionResult Query(
        string? type = null, string? field = null, string? queryValue = null, bool clear = false)
    {
        HollowReadStateEngine stateEngine = explorer.StateEngine;
        ExplorerSession session = sessions.Get(HttpContext);

        if (clear)
        {
            session.ClearQueryResult();
        }

        if (type == AnyType)
        {
            type = null;
        }

        session.QueryResult?.RecalculateIfNotCurrent(stateEngine);

        // An empty field is the form being submitted with nothing in it, which is not a clause — Java
        // adds it anyway and leaves the reader with a search matching nothing and no way back but
        // clearing.
        if (!string.IsNullOrEmpty(field) && queryValue is not null)
        {
            QueryResult result = session.QueryResult ??= new QueryResult(stateEngine.RandomizedTag);

            result.AugmentQuery(new QueryClause(type, field, queryValue), stateEngine);

            // The form starts empty again, ready for the next clause.
            type = null;
            field = null;
        }

        return View(new QueryPageModel
        {
            AllTypes = [.. stateEngine.Schemas.Select(schema => schema.Name).Order(StringComparer.Ordinal)],
            SelectedType = type,
            SelectedField = field,
            QueryResult = session.QueryResult,
            BasePath = options.BasePath,
            StateVersion = explorer.CurrentStateVersion,
            HeaderDisplayString = explorer.HeaderDisplayString,
            HeaderStringUrl = explorer.HeaderStringUrl,
            CommonHeaderEntries = [.. explorer.CommonHeaderEntries],
        });
    }

    /// <summary>What the type dropdown offers in place of naming a type.</summary>
    internal const string AnyType = "ANY TYPE";

    /// <summary>
    /// The records of one page, skipping over the ordinals of the pages before it.
    /// </summary>
    /// <remarks>
    /// The ordinals in scope are a bit set rather than a list, so reaching the start of a page means
    /// stepping through the ones before it. That is what makes a page deep into a large type slower to
    /// reach than the first.
    /// </remarks>
    private static List<TypeKey> ReadPage(
        HollowTypeReadState typeState,
        BitSet selectedOrdinals,
        int page,
        int pageSize,
        int[][]? fieldPathIndexes)
    {
        int startRecord = page * pageSize;
        int currentOrdinal = selectedOrdinals.NextSetBit(0);

        for (int i = 0; i < startRecord && currentOrdinal != HollowConstants.OrdinalNone; i++)
        {
            currentOrdinal = selectedOrdinals.NextSetBit(currentOrdinal + 1);
        }

        List<TypeKey> keys = new(pageSize);

        for (int i = 0; i < pageSize && currentOrdinal != HollowConstants.OrdinalNone; i++)
        {
            keys.Add(ReadKey(typeState, startRecord + i, currentOrdinal, fieldPathIndexes));
            currentOrdinal = selectedOrdinals.NextSetBit(currentOrdinal + 1);
        }

        return keys;
    }

    /// <summary>
    /// The key of one record, both as a link should carry it and as it should be read.
    /// </summary>
    /// <remarks>
    /// A type declaring no key has nothing to call its records but their ordinals, which is not a key
    /// and cannot be searched for — hence the empty link key beside a readable <c>ORDINAL:n</c>.
    /// </remarks>
    private static TypeKey ReadKey(
        HollowTypeReadState typeState, int index, int ordinal, int[][]? fieldPathIndexes) =>
        fieldPathIndexes is null
            ? new TypeKey(index, ordinal, "", $"ORDINAL:{ordinal}")
            : new TypeKey(
                index,
                ordinal,
                SearchUtils.BuildKey(typeState, ordinal, fieldPathIndexes, escapeDelimiters: true),
                SearchUtils.BuildKey(typeState, ordinal, fieldPathIndexes, escapeDelimiters: false));

    /// <summary>
    /// <paramref name="typeOverviews"/> in the order <paramref name="sort"/> asks for.
    /// </summary>
    /// <remarks>
    /// Every column but the name sorts largest first, since what a reader wants from those is which
    /// types are worth looking at. The default puts the types declaring a key first, because those are
    /// the ones whose records can be found by anything but an ordinal.
    /// </remarks>
    private static List<TypeOverview> Sort(List<TypeOverview> typeOverviews, string? sort) =>
        sort switch
        {
            "typeName" => [.. typeOverviews.OrderBy(type => type.TypeName, StringComparer.Ordinal)],
            "numRecords" => [.. typeOverviews.OrderByDescending(type => type.NumRecords)],
            "numHoles" => [.. typeOverviews.OrderByDescending(type => type.NumHoles)],
            "holeSize" => [.. typeOverviews.OrderByDescending(type => type.ApproxHoleFootprint)],
            "heapSize" => [.. typeOverviews.OrderByDescending(type => type.ApproxHeapFootprint)],
            "numShards" => [.. typeOverviews.OrderByDescending(type => type.NumShards)],
            _ =>
            [
                .. typeOverviews
                    .OrderByDescending(type => type.PrimaryKey is not null)
                    .ThenBy(type => type.TypeName, StringComparer.Ordinal),
            ],
        };
}
