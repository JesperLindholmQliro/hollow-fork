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
using Hollow.Core.Read.Engine;
using Hollow.Core.Tools.Stringifier;
using Hollow.Explorer.Models;

namespace Hollow.Explorer.ViewModels;

/// <summary>What the home page shows: every type in the dataset and what it costs.</summary>
public sealed class ShowAllTypesModel : ExplorerPageModel
{
    /// <summary>The types, in the order the reader asked for.</summary>
    public required IReadOnlyList<TypeOverview> TypeOverviews { get; init; }

    /// <summary>What the whole dataset costs.</summary>
    public required string TotalHeapFootprint { get; init; }

    /// <summary>How much of that is ordinals holding nothing.</summary>
    public required string TotalHoleFootprint { get; init; }
}

/// <summary>What the browse page shows: a page of one type's records, and one of them in full.</summary>
public sealed class BrowseSelectedTypeModel : ExplorerPageModel
{
    /// <summary>The type being browsed.</summary>
    public required string Type { get; init; }

    /// <summary>The records of this page, by key.</summary>
    public required IReadOnlyList<TypeKey> Keys { get; init; }

    /// <summary>Which page of records is being shown, counting from zero.</summary>
    public required int Page { get; init; }

    /// <summary>How many records a page holds.</summary>
    public required int PageSize { get; init; }

    /// <summary>How many pages there are.</summary>
    public required int NumPages { get; init; }

    /// <summary>How many records are in scope, which a search may have narrowed.</summary>
    public required int NumRecords { get; init; }

    /// <summary>The key of the record being shown, as a link should carry it.</summary>
    public required string Key { get; init; }

    /// <summary>
    /// The ordinal being shown, or <see cref="HollowConstants.OrdinalNone"/> when none is.
    /// </summary>
    public required int Ordinal { get; init; }

    /// <summary>Which form the record is written in: <c>text</c> or <c>json</c>.</summary>
    public required string Display { get; init; }

    /// <summary>The search narrowing this page, written out, or nothing when there is none.</summary>
    public string? FilteredByQuery { get; init; }

    /// <summary>Whether a key was typed that nothing matched.</summary>
    public bool KeyNotFound => Key.Length > 0 && Ordinal == HollowConstants.OrdinalNone;

    /// <summary>Whether there is a record to show.</summary>
    public bool HasRecord => Ordinal != HollowConstants.OrdinalNone;

    /// <summary>The state the record is read from.</summary>
    public required HollowReadStateEngine StateEngine { get; init; }

    /// <summary>
    /// Writes the record being shown to <paramref name="writer"/>, in whichever form was asked for.
    /// </summary>
    /// <remarks>
    /// The page writes the record straight out rather than building it up as a string first, because a
    /// record holding a large collection would otherwise be held twice — once by the stringifier and
    /// once by the page.
    /// </remarks>
    public void WriteRecord(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (!HasRecord)
        {
            return;
        }

        HollowStringifier stringifier = Display == "json"
            ? new HollowRecordJsonStringifier(
                prettyPrint: true, collapseAllSingleFieldObjects: true, sortSingleFieldSetElements: true)
            : new HollowRecordStringifier(
                showOrdinals: false,
                showTypes: false,
                collapseSingleFieldObjects: true,
                sortSingleFieldSetElements: true);

        stringifier.Stringify(writer, StateEngine, Type, Ordinal);
    }
}

/// <summary>What the schema page shows: one or more schemas, as trees the reader opens.</summary>
public sealed class BrowseSchemaModel : ExplorerPageModel
{
    /// <summary>The schemas being shown.</summary>
    public required IReadOnlyList<SchemaDisplay> SchemaDisplays { get; init; }

    /// <summary>
    /// The types asked for, which every expand and collapse link has to carry so that opening one
    /// branch does not close the other schemas on the page.
    /// </summary>
    public required IReadOnlyList<string> Types { get; init; }
}

/// <summary>What the search page shows: the form, and what the search so far matched.</summary>
public sealed class QueryPageModel : ExplorerPageModel
{
    /// <summary>Every type in the dataset, to pick one to search in.</summary>
    public required IReadOnlyList<string> AllTypes { get; init; }

    /// <summary>The type the form should start on, where a link named one.</summary>
    public string? SelectedType { get; init; }

    /// <summary>The field the form should start on, where a link named one.</summary>
    public string? SelectedField { get; init; }

    /// <summary>The search so far, or <see langword="null"/> when none has been started.</summary>
    public QueryResult? QueryResult { get; init; }
}
