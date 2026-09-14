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
using System.Net;
using System.Text.RegularExpressions;
using Hollow.Core.Util;

namespace Hollow.Tests.Explorer;

/// <summary>
/// The history UI's pages, served over HTTP as a browser would fetch them.
/// </summary>
/// <remarks>
/// A history page is worth very little unless it can answer the question a reader actually has: what
/// happened to this record, and when. So most of these follow a record — by key, into a version, into
/// the record page — rather than checking each page in isolation.
/// </remarks>
public class HistoryUIPageTests(HistoryUIFixture fixture) : IClassFixture<HistoryUIFixture>
{
    private const string FilmType = "Film";

    [Fact]
    public async Task TheOverviewListsEveryVersionNewestFirst()
    {
        string html = await GetAsync("/overview");

        // The version the history was started at is not a change, so it has no line of its own.
        foreach (long version in HistoryUIFixture.Versions.Skip(1))
        {
            Assert.Contains(version.ToString(CultureInfo.InvariantCulture), html, StringComparison.Ordinal);
        }

        int newest = html.IndexOf(
            HistoryUIFixture.Versions[2].ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        int older = html.IndexOf(
            HistoryUIFixture.Versions[1].ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        Assert.True(newest < older, "the overview should list the newest version first");
    }

    [Fact]
    public async Task TheOverviewReadsAClockStampedVersionAsAMoment()
    {
        string html = await GetAsync("/overview");

        // 20240117093000123 is 2024-01-17 09:30 UTC.
        Assert.Contains("[01/17 09:30 UTC]", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOverviewCountsWhatEachVersionChanged()
    {
        string html = await GetAsync("/overview");

        // The second version drops one film and adds another; the third changes one.
        Assert.Contains("+: 1", html, StringComparison.Ordinal);
        Assert.Contains("-: 1", html, StringComparison.Ordinal);
        Assert.Contains("&#916;: 1", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVersionListsTheTypesThatChangedInIt()
    {
        string html = await GetAsync(StatePath(HistoryUIFixture.Versions[1]));

        Assert.Contains(FilmType, html, StringComparison.Ordinal);
        Assert.Contains("statetype?version=", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVersionLinksToTheOnesEitherSideOfIt()
    {
        // The history was started at the first version, so the oldest state it holds is the second —
        // there is nothing before it to link back to.
        string oldest = await GetAsync(StatePath(HistoryUIFixture.Versions[1]));

        Assert.DoesNotContain("prev version", oldest, StringComparison.Ordinal);
        Assert.Contains("next version", oldest, StringComparison.Ordinal);

        // And nothing after the newest.
        string newest = await GetAsync(StatePath(HistoryUIFixture.Versions[2]));

        Assert.Contains("prev version", newest, StringComparison.Ordinal);
        Assert.DoesNotContain("next version", newest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVersionTheHistoryDoesNotHoldIsNotFound()
    {
        using HttpClient client = fixture.NewClient();
        using HttpResponseMessage response =
            await client.GetAsync(StatePath(1L), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ATypeInAVersionListsTheRecordsThatChanged()
    {
        string html = await GetAsync(StateTypePath(HistoryUIFixture.Versions[1]));

        Assert.Contains("Modified:", html, StringComparison.Ordinal);
        Assert.Contains("Added:", html, StringComparison.Ordinal);
        Assert.Contains("Removed:", html, StringComparison.Ordinal);

        // Every listed record links to its own page.
        Assert.Contains("historicalObject?version=", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATypeCanBeGroupedByAKeyField()
    {
        string ungrouped = await GetAsync(StateTypePath(HistoryUIFixture.Versions[1]));

        // Both key fields are on offer while nothing is grouped by.
        Assert.Contains("groupBy=Id", ungrouped, StringComparison.Ordinal);
        Assert.Contains("groupBy=Studio.Country", ungrouped, StringComparison.Ordinal);

        string grouped = await GetAsync(
            StateTypePath(HistoryUIFixture.Versions[1]) + "&groupBy=Studio.Country");

        // Grouped by country, the records sit under the country they came from.
        Assert.Contains("data-expand-group=", grouped, StringComparison.Ordinal);
        Assert.Contains("US", grouped, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpeningAGroupFetchesItsRecords()
    {
        using HttpClient client = fixture.NewClient();

        string grouped = await GetAsync(
            client, StateTypePath(HistoryUIFixture.Versions[1]) + "&groupBy=Studio.Country");

        string groupId = Regex.Matches(grouped, "data-expand-group=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .First();

        string expanded = await GetAsync(
            client,
            "/statetypeexpand?version="
            + HistoryUIFixture.Versions[1].ToString(CultureInfo.InvariantCulture)
            + "&type=" + FilmType
            + "&groupBy=Studio.Country"
            + "&expandGroupId=" + Uri.EscapeDataString(groupId));

        // The fragment is the records themselves, with no page around them.
        Assert.Contains("historicalObject?version=", expanded, StringComparison.Ordinal);
        Assert.DoesNotContain("<html", expanded, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchingForAKeyFindsEveryVersionThatChangedIt()
    {
        // Film 2's title changed at the third version and nothing else touched it.
        string html = await GetAsync("/query?query=2");

        Assert.Contains(
            HistoryUIFixture.Versions[2].ToString(CultureInfo.InvariantCulture),
            html,
            StringComparison.Ordinal);

        Assert.Contains("Modified:", html, StringComparison.Ordinal);
        Assert.Contains(FilmType, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchingForAKeyThatNeverChangedSaysSo()
    {
        // Film 5 is in every version, unchanged throughout.
        string html = await GetAsync("/query?query=5");

        Assert.Contains("No version in the history changed", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARecordPageShowsBothSidesOfTheChange()
    {
        using HttpClient client = fixture.NewClient();

        // Film 1's cast changed at the second version.
        string html = await GetAsync(client, RecordPath(HistoryUIFixture.Versions[1], FilmOneKeyOrdinal()));

        Assert.Contains("difftable", html, StringComparison.Ordinal);

        // Something under the record differs, and the rows leading to it are marked as such.
        Assert.Contains("class=\"delete\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"insert\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARecordPageListsTheVersionsThatChangedThatRecord()
    {
        using HttpClient client = fixture.NewClient();

        string html = await GetAsync(client, RecordPath(HistoryUIFixture.Versions[1], FilmOneKeyOrdinal()));

        Assert.Contains("Changed in:", html, StringComparison.Ordinal);
        Assert.Contains(
            HistoryUIFixture.Versions[1].ToString(CultureInfo.InvariantCulture),
            html,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARecordKeyThatTheVersionDidNotTouchIsNotFound()
    {
        using HttpClient client = fixture.NewClient();

        // Film 5 never changed, so no version has a record page for it.
        using HttpResponseMessage response = await client.GetAsync(
            RecordPath(HistoryUIFixture.Versions[1], KeyOrdinalOf("5")),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ARowOpensAndClosesAgain()
    {
        using HttpClient client = fixture.NewClient();

        string html = await GetAsync(client, RecordPath(HistoryUIFixture.Versions[1], FilmOneKeyOrdinal()));

        string rowPath = Regex.Matches(html, "uncollapseRow\\(&#x27;([^&]+)&#x27;\\)")
            .Select(match => match.Groups[1].Value)
            .First();

        string rowQuery =
            "version=" + HistoryUIFixture.Versions[1].ToString(CultureInfo.InvariantCulture)
            + "&type=" + FilmType
            + "&keyOrdinal=" + FilmOneKeyOrdinal().ToString(CultureInfo.InvariantCulture)
            + "&row=" + Uri.EscapeDataString(rowPath);

        string opened = await GetAsync(client, "/diffrowdata?" + rowQuery);

        // The rows that became visible come back pipe-delimited, eight fields to a row.
        Assert.NotEmpty(opened);
        Assert.Equal(0, opened.Split('|').Length % 8);

        Assert.Equal("ok", await GetAsync(client, "/collapsediffrow?" + rowQuery));
    }

    [Fact]
    public async Task TheRecordViewsResourcesAreServed()
    {
        using HttpClient client = fixture.NewClient();

        foreach (string name in new[] { "diffview.css", "expand.png", "collapse.png", "partial_expand.png" })
        {
            using HttpResponseMessage response =
                await client.GetAsync("/resource/" + name, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        }

        using HttpResponseMessage missing =
            await client.GetAsync("/resource/anything-else", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task EveryPageButTheOverviewOffersAWayBackToIt()
    {
        Assert.DoesNotContain(">Home<", await GetAsync("/overview"), StringComparison.Ordinal);
        Assert.Contains(">Home<", await GetAsync(StatePath(HistoryUIFixture.Versions[1])), StringComparison.Ordinal);
    }

    private static string StatePath(long version) =>
        "/state?version=" + version.ToString(CultureInfo.InvariantCulture);

    private static string StateTypePath(long version) =>
        "/statetype?version=" + version.ToString(CultureInfo.InvariantCulture) + "&type=" + FilmType;

    private static string RecordPath(long version, int keyOrdinal) =>
        "/historicalObject?version=" + version.ToString(CultureInfo.InvariantCulture)
        + "&type=" + FilmType
        + "&keyOrdinal=" + keyOrdinal.ToString(CultureInfo.InvariantCulture);

    /// <summary>The key ordinal of film 1, whose cast changed at the second version.</summary>
    private int FilmOneKeyOrdinal() => KeyOrdinalOf("1");

    private int KeyOrdinalOf(string id)
    {
        // The key is (Id, country), so the search is by id alone and the one match is the film.
        IntList matches = fixture.History.KeyIndex.TypeKeyIndexes[FilmType].QueryIndexedFields(id);

        Assert.Equal(1, matches.Count);

        return matches.Get(0);
    }

    private async Task<string> GetAsync(string path)
    {
        using HttpClient client = fixture.NewClient();

        return await GetAsync(client, path);
    }

    private static async Task<string> GetAsync(HttpClient client, string path)
    {
        using HttpResponseMessage response =
            await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }
}
