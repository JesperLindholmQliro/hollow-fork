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
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hollow.Tests.Explorer;

/// <summary>
/// The explorer's four pages, read the way a person would read them.
/// </summary>
public class ExplorerPageTests(ExplorerFixture fixture) : IClassFixture<ExplorerFixture>
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
    /// The home page is the index of the dataset: every type it holds, including the ones the object
    /// mapper made up to hold the fields of the ones that were declared.
    /// </summary>
    [Fact]
    public async Task TheHomePageListsEveryTypeInTheDataset()
    {
        string html = await GetAsync("/home");

        foreach (string typeName in fixture.ReadEngine.TypeStates.Keys)
        {
            Assert.Contains($">{typeName}</a>", html, StringComparison.Ordinal);
        }

        Assert.Contains("Approx. Total Heap Footprint:", html, StringComparison.Ordinal);

        // The key a type declares is shown, since it is what its records can be looked up by.
        Assert.Contains("PrimaryKey", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The default order puts the types declaring a key first, because those are the ones whose records
    /// can be found by anything but an ordinal.
    /// </summary>
    [Fact]
    public async Task TheHomePageLeadsWithTheTypesThatDeclareAKey()
    {
        string html = await GetAsync("/home");

        Assert.Equal("Film", TypeRowOrder(html)[0]);
    }

    /// <summary>
    /// Every column heading is a link that reorders the table by that column, largest first.
    /// </summary>
    [Fact]
    public async Task TheHomePageCanBeOrderedByAnyColumn()
    {
        Assert.Equal(
            TypeRowOrder(await GetAsync("/home?sort=typeName")).Order(StringComparer.Ordinal),
            TypeRowOrder(await GetAsync("/home?sort=typeName")));

        // String is the type with most records in this dataset, being every title, name and studio.
        Assert.Equal("String", TypeRowOrder(await GetAsync("/home?sort=numRecords"))[0]);
    }

    /// <summary>
    /// The browse page lists a type's records by key, and writes out whichever one was asked for.
    /// </summary>
    [Fact]
    public async Task TheBrowsePageShowsARecordPickedByOrdinal()
    {
        string html = await GetAsync("/type?type=Film&ordinal=0");

        Assert.Contains("Title: The Matrix", html, StringComparison.Ordinal);
        Assert.Contains("Studio: Warner Bros.", html, StringComparison.Ordinal);

        // Picking by ordinal fills the search box in with the key that record turned out to have.
        Assert.Contains(@"name=""key"" value=""1""", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A key is what a person has and an ordinal is what the data has, so the page takes either.
    /// </summary>
    [Fact]
    public async Task TheBrowsePageShowsARecordPickedByKey()
    {
        string html = await GetAsync("/type?type=Film&key=2");

        Assert.Contains("Title: Everything Everywhere All at Once", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A key nothing matches is said so, rather than quietly showing whatever was on the page before.
    /// </summary>
    [Fact]
    public async Task AKeyMatchingNothingIsReported()
    {
        string html = await GetAsync("/type?type=Film&key=99");

        Assert.Contains("ERROR: Key 99 was not found!", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The JSON form is for reading a record into something else, so it has to parse.
    /// </summary>
    [Fact]
    public async Task TheBrowsePageCanWriteARecordAsJson()
    {
        string html = await GetAsync("/type?type=Film&ordinal=0&display=json");

        string json = WebUtility.HtmlDecode(Between(html, "<pre>", "</pre>"));

        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal("The Matrix", document.RootElement.GetProperty("Title").GetString());
        Assert.Equal(
            ["Keanu Reeves", "Carrie-Anne Moss"],
            document.RootElement.GetProperty("Cast").EnumerateArray()
                .Select(actor => actor.GetProperty("Name").GetString()));
    }

    /// <summary>
    /// A type the dataset does not have is a mistyped URL rather than an error the page can render.
    /// </summary>
    [Fact]
    public async Task BrowsingATypeThatDoesNotExistIsNotFound()
    {
        using HttpClient client = fixture.NewClient();

        HttpResponseMessage response =
            await client.GetAsync("/type?type=Nope", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The schema page starts with the type's own fields, and a reference is a link rather than its
    /// contents — because following every reference at once would print the whole model.
    /// </summary>
    [Fact]
    public async Task TheSchemaPageShowsOneLevelAtATime()
    {
        string html = await GetAsync("/schema?type=Film");

        Assert.Contains("Studio", html, StringComparison.Ordinal);
        Assert.Contains("expand=", html, StringComparison.Ordinal);

        // The studio's own field is behind the link, not on the page yet.
        Assert.DoesNotContain("collapse=", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Which branches are open is the reader's place in the model, so it survives the next request
    /// rather than having to be rebuilt from the URL each time.
    /// </summary>
    [Fact]
    public async Task AnOpenedBranchStaysOpenOnTheNextRequest()
    {
        using HttpClient client = fixture.NewClient();

        string expanded = await GetAsync("/schema?type=Film&expand=.Studio", client);

        Assert.Contains("collapse=", expanded, StringComparison.Ordinal);

        string revisited = await GetAsync("/schema?type=Film", client);

        Assert.Contains("collapse=", revisited, StringComparison.Ordinal);

        string collapsed = await GetAsync("/schema?type=Film&collapse=.Studio", client);

        Assert.DoesNotContain("collapse=", collapsed, StringComparison.Ordinal);
    }

    /// <summary>
    /// Asking for the schema page with no type named shows the types nothing else references, which is
    /// where a reader would have started anyway.
    /// </summary>
    [Fact]
    public async Task TheSchemaPageWithNoTypeShowsTheOnesNothingReferences()
    {
        string html = await GetAsync("/schema");

        Assert.Contains("<b>TYPE:</b>", html, StringComparison.Ordinal);
        Assert.Contains("Film", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A search matches more than the records literally holding the value: everything referencing a
    /// match comes too, which is how searching for an actor finds the films they are in.
    /// </summary>
    [Fact]
    public async Task ASearchFindsTheRecordsReferencingAMatchAsWellAsTheMatch()
    {
        using HttpClient client = fixture.NewClient();

        string html = await GetAsync("/query?type=ANY+TYPE&field=Name&queryValue=Keanu+Reeves", client);

        // The clause is shown back with its quotes encoded, since the page writes it as text.
        Assert.Contains("Name=&quot;Keanu Reeves&quot;", html, StringComparison.Ordinal);

        Dictionary<string, int> matches = QueryMatches(html);

        // The two films he is in, found through the actor record rather than by holding his name.
        Assert.Equal(2, matches["Film"]);
        Assert.Equal(1, matches["Actor"]);
    }

    /// <summary>
    /// Clauses narrow, so adding one cannot widen what matched — which is what makes a search
    /// something a person builds up rather than gets right first time.
    /// </summary>
    [Fact]
    public async Task EachClauseNarrowsWhatTheLastOneMatched()
    {
        using HttpClient client = fixture.NewClient();

        await GetAsync("/query?type=ANY+TYPE&field=Name&queryValue=Keanu+Reeves", client);

        string html = await GetAsync("/query?type=Film&field=Year&queryValue=2014", client);

        Assert.Contains(" AND ", html, StringComparison.Ordinal);
        Assert.Equal(1, QueryMatches(html)["Film"]);

        string cleared = await GetAsync("/query?clear=true", client);

        Assert.DoesNotContain("Current Query:", cleared, StringComparison.Ordinal);
    }

    /// <summary>
    /// A search is the reader's place in the data, so the browse page shows what it narrowed to rather
    /// than every record of the type — and says that it is doing so.
    /// </summary>
    [Fact]
    public async Task ASearchNarrowsWhatTheBrowsePageShows()
    {
        using HttpClient client = fixture.NewClient();

        await GetAsync("/query?type=Film&field=Year&queryValue=2014", client);

        string narrowed = await GetAsync("/type?type=Film", client);

        Assert.Contains("Results are filtered by query", narrowed, StringComparison.Ordinal);
        Assert.Contains("SIZE: 1", narrowed, StringComparison.Ordinal);

        string cleared = await GetAsync("/type?type=Film&clearQuery=true", client);

        Assert.Contains("SIZE: 3", cleared, StringComparison.Ordinal);
    }

    /// <summary>
    /// One reader's search is theirs, not the next reader's — which for a tool several people point at
    /// the same dataset is the difference between usable and baffling.
    /// </summary>
    [Fact]
    public async Task OneReadersSearchDoesNotNarrowAnothersPage()
    {
        using HttpClient searching = fixture.NewClient();

        await GetAsync("/query?type=Film&field=Year&queryValue=2014", searching);

        Assert.Contains("SIZE: 3", await GetAsync("/type?type=Film"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A record holding markup is text, not markup, wherever the page puts it.
    /// </summary>
    [Fact]
    public async Task TextFromTheDataIsEscapedRatherThanRendered()
    {
        string html = await GetAsync("/query?type=ANY+TYPE&field=Name&queryValue=%3Cscript%3E");

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The navigation bar is what carries the reader between the pages, so what it offers depends on
    /// where they are.
    /// </summary>
    [Fact]
    public async Task TheNavigationBarOffersWhatThePageHasToGoTo()
    {
        string home = await GetAsync("/home");

        Assert.Contains("Browse Top Level Schemas", home, StringComparison.Ordinal);
        Assert.DoesNotContain("Browse Data", home, StringComparison.Ordinal);

        string type = await GetAsync("/type?type=Film");

        Assert.Contains("Browse Data", type, StringComparison.Ordinal);
        Assert.DoesNotContain("Browse Top Level Schemas", type, StringComparison.Ordinal);
    }

    /// <summary>The type names of the home page's table, in the order it put them.</summary>
    private static List<string> TypeRowOrder(string html) =>
        [.. Regex.Matches(html, @"/type\?type=[^""]*"">([^<]+)</a>").Select(match => match.Groups[1].Value)];

    /// <summary>How many records of each type the search page says matched.</summary>
    private static Dictionary<string, int> QueryMatches(string html) =>
        Regex.Matches(html, @">([A-Za-z]+)</a>: (\d+) records match")
            .ToDictionary(
                match => match.Groups[1].Value,
                match => int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                StringComparer.Ordinal);

    private static string Between(string text, string start, string end)
    {
        int from = text.IndexOf(start, StringComparison.Ordinal) + start.Length;
        int to = text.IndexOf(end, from, StringComparison.Ordinal);

        return text[from..to];
    }
}
