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

using System.Net;
using System.Text.RegularExpressions;
using Hollow.Core.Tools.Diff;

namespace Hollow.Tests.Explorer;

/// <summary>
/// The diff's four pages, and the two requests that open and close a row, read the way a person would
/// read them.
/// </summary>
public partial class DiffUIPageTests(DiffUIFixture fixture) : IClassFixture<DiffUIFixture>
{
    private async Task<string> GetAsync(string path, HttpClient? client = null)
    {
        bool ownsClient = client is null;
        client ??= fixture.NewClient();

        try
        {
            HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            if (ownsClient)
            {
                client.Dispose();
            }
        }
    }

    /// <summary>
    /// The overview is the index of the diff: every type that was compared, and how far apart the two
    /// states are in each.
    /// </summary>
    [Fact]
    public async Task TheOverviewListsEveryTypeThatWasCompared()
    {
        string html = await GetAsync("/");

        foreach (string typeName in fixture.Diff.TypeDiffs.Select(diff => diff.TypeName))
        {
            Assert.Contains($">{typeName}</a>", html, StringComparison.Ordinal);
        }

        Assert.Contains("Heap Diff:", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The default order leads with the type that moved most, which is the one worth opening first.
    /// </summary>
    [Fact]
    public async Task TheOverviewLeadsWithTheTypeThatMovedMost()
    {
        Assert.Equal("Film", TypeRowOrder(await GetAsync("/"))[0]);
    }

    /// <summary>
    /// Every column heading reorders the table by that column, largest first.
    /// </summary>
    [Fact]
    public async Task TheOverviewCanBeOrderedByAnyColumn()
    {
        using HttpClient client = fixture.NewClient();

        string html = await GetAsync("/overview?sortBy=fromCount", client);
        IReadOnlyList<string> order = TypeRowOrder(html);

        // Film has three records in the earlier state; every other type has at most that many, so it
        // cannot be beaten and must come first.
        Assert.Equal("Film", order[0]);

        // The chosen order is remembered, so following a link that says nothing about it keeps it.
        Assert.Equal(order, TypeRowOrder(await GetAsync("/overview", client)));
    }

    /// <summary>
    /// A type's page accounts for every record: those that moved, and those only one side has.
    /// </summary>
    [Fact]
    public async Task TheTypePageAccountsForEveryRecord()
    {
        string html = await GetAsync("/typediff?type=Film");

        // Both surviving films moved: one changed its title and year, the other its cast.
        Assert.Contains("Total Diff Objects: 2", html, StringComparison.Ordinal);

        // The Lobster went and Everything Everywhere arrived, so each side has exactly one record the
        // other does not.
        Assert.Contains("Extra in from-blob : 1", html, StringComparison.Ordinal);
        Assert.Contains("Extra in to-blob : 1", html, StringComparison.Ordinal);

        Assert.Contains("objectdiff?type=Film&amp;fromOrdinal=", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The field list can be folded away, and stays folded on the pages that follow.
    /// </summary>
    [Fact]
    public async Task TheTypePageRemembersWhetherTheFieldListIsOpen()
    {
        using HttpClient client = fixture.NewClient();

        Assert.Contains("fielddiff?type=Film", await GetAsync("/typediff?type=Film", client), StringComparison.Ordinal);

        string hidden = await GetAsync("/typediff?type=Film&showFields=false", client);
        Assert.DoesNotContain("fielddiff?type=Film", hidden, StringComparison.Ordinal);

        Assert.DoesNotContain(
            "fielddiff?type=Film",
            await GetAsync("/typediff?type=Film", client),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A field's page lists the record pairs that field differs in.
    /// </summary>
    [Fact]
    public async Task TheFieldPageListsThePairsThatFieldDiffersIn()
    {
        string html = await GetAsync("/fielddiff?type=Film&fieldIdx=0");

        Assert.Contains("Object Diffs", html, StringComparison.Ordinal);
        Assert.Contains("objectdiff?type=Film&amp;fieldIdx=0", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A field index that names no field is not found, rather than a page reporting an error from
    /// somewhere inside it.
    /// </summary>
    [Fact]
    public async Task AFieldThatDoesNotExistIsNotFound()
    {
        using HttpClient client = fixture.NewClient();

        HttpResponseMessage response = await client.GetAsync(
            "/fielddiff?type=Film&fieldIdx=9999", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The record page shows the two records side by side, opened at what differs between them.
    /// </summary>
    [Fact]
    public async Task TheRecordPageOpensAtWhatDiffers()
    {
        (int fromOrdinal, int toOrdinal) = ChangedFilmOrdinals();

        string html = await GetAsync(
            $"/objectdiff?type=Film&fromOrdinal={fromOrdinal}&toOrdinal={toOrdinal}");

        // Both titles are on the page, since the changed field is what the view opens at.
        Assert.Contains("John Wick", html, StringComparison.Ordinal);
        Assert.Contains("John Wick: Chapter 2", html, StringComparison.Ordinal);

        // The changed cell is coloured as a replacement, and the year moved with the title.
        Assert.Contains("class=\"replace\"", html, StringComparison.Ordinal);
        Assert.Contains("2017", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A record only one side has is shown against nothing, which is what the -1 ordinal means.
    /// </summary>
    [Fact]
    public async Task ARecordOnlyOneSideHasIsShownAgainstNothing()
    {
        int ordinal = fixture.Diff.GetTypeDiff("Film")!.UnmatchedOrdinalsInFrom.Get(0);

        string html = await GetAsync($"/objectdiff?type=Film&fromOrdinal={ordinal}&toOrdinal=-1");

        Assert.Contains("The Lobster", html, StringComparison.Ordinal);

        // Every field of it is a removal, and the other column has nothing in it at all.
        Assert.Contains("class=\"delete\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"empty\"", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A branch the view left closed opens on request, and the rows that became visible come back —
    /// eight fields each, run together, as the page's script reads them.
    /// </summary>
    [Fact]
    public async Task OpeningARowAnswersWithTheRowsThatBecameVisible()
    {
        using HttpClient client = fixture.NewClient();
        (string page, string rowPath) = await OpenableRowAsync(client);

        string rows = await GetAsync($"/diffrowdata?{page}&row={rowPath}", client);

        Assert.NotEmpty(rows);

        string[] fields = rows.Split('|');
        Assert.Equal(0, fields.Length % 8);

        // Every row that came back is under the row that was opened.
        foreach (string path in fields.Where((_, index) => index % 8 == 0))
        {
            Assert.StartsWith(rowPath + ".", path, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Closing a branch hides it, so opening it again is what brings its rows back.
    /// </summary>
    [Fact]
    public async Task ClosingARowHidesWhatIsUnderIt()
    {
        using HttpClient client = fixture.NewClient();
        (string page, string rowPath) = await OpenableRowAsync(client);

        _ = await GetAsync($"/diffrowdata?{page}&row={rowPath}", client);

        Assert.Equal("ok", await GetAsync($"/collapsediffrow?{page}&row={rowPath}", client));

        // Drawing the page again shows the branch closed, since the tree is the one that was changed.
        string html = await GetAsync($"/objectdiff?{page}", client);

        Assert.DoesNotContain($"id=\"r{rowPath}.", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A row path is checked step by step, because it arrives in a URL. A stale page must not be able
    /// to walk off the end of a branch.
    /// </summary>
    [Theory]
    [InlineData("9999")]
    [InlineData("0.9999")]
    [InlineData("nonsense")]
    [InlineData("-1")]
    public async Task ARowPathThatLeadsNowhereIsNotFound(string rowPath)
    {
        using HttpClient client = fixture.NewClient();
        (string page, _) = await OpenableRowAsync(client);

        HttpResponseMessage response = await client.GetAsync(
            $"/diffrowdata?{page}&row={Uri.EscapeDataString(rowPath)}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The stylesheet and the three margin images ride along in the assembly, so an embedder gets a
    /// working page without serving static files of its own.
    /// </summary>
    [Theory]
    [InlineData("diffview.css", "text/css")]
    [InlineData("expand.png", "image/png")]
    [InlineData("collapse.png", "image/png")]
    [InlineData("partial_expand.png", "image/png")]
    public async Task TheStylesheetAndMarginImagesAreServed(string name, string contentType)
    {
        using HttpClient client = fixture.NewClient();

        HttpResponseMessage response = await client.GetAsync(
            "/resource/" + name, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
        Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Only the resources the pages actually ask for are served — the name indexes a fixed set, so
    /// nothing a caller writes reaches the assembly's manifest as text.
    /// </summary>
    [Theory]
    [InlineData("Hollow.dll")]
    [InlineData("../Views/Diff/Overview.cshtml")]
    public async Task NothingElseIsServedAsAResource(string name)
    {
        using HttpClient client = fixture.NewClient();

        HttpResponseMessage response = await client.GetAsync(
            "/resource/" + Uri.EscapeDataString(name), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>The two ordinals of the film whose title and year changed.</summary>
    private (int FromOrdinal, int ToOrdinal) ChangedFilmOrdinals()
    {
        HollowFieldDiff fieldDiff = fixture.Diff.GetTypeDiff("Film")!.FieldDiffs[0];

        return (fieldDiff.GetFromOrdinal(0), fieldDiff.GetToOrdinal(0));
    }

    /// <summary>
    /// A drawn record page, and a row on it the reader can open.
    /// </summary>
    /// <remarks>
    /// Which rows start closed is the view's decision, so the row to open is read off the page rather
    /// than assumed — and the page has to be drawn first either way, since opening a row changes the
    /// tree the page was drawn from.
    /// </remarks>
    private async Task<(string Page, string RowPath)> OpenableRowAsync(HttpClient client)
    {
        (int fromOrdinal, int toOrdinal) = ChangedFilmOrdinals();
        string page = $"type=Film&fromOrdinal={fromOrdinal}&toOrdinal={toOrdinal}";

        Match match = UncollapsePattern().Match(await GetAsync($"/objectdiff?{page}", client));

        Assert.True(match.Success, "the record page offered no row to open");

        return (page, match.Groups[1].Value);
    }

    private static IReadOnlyList<string> TypeRowOrder(string html) =>
        [.. TypeLinkPattern().Matches(html).Select(match => match.Groups[1].Value)];

    // Razor encodes the quotes inside the attribute, which is correct HTML — the browser decodes them
    // before the script sees them.
    [GeneratedRegex("uncollapseRow\\(&#x27;([^&]+)&#x27;\\)")]
    private static partial Regex UncollapsePattern();

    [GeneratedRegex("typediff\\?type=([^\"]+)\"")]
    private static partial Regex TypeLinkPattern();
}
