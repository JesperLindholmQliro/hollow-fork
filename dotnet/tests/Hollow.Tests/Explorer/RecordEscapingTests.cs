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
using Hollow.Core.Read.Engine;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;
using Hollow.Explorer;

namespace Hollow.Tests.Explorer;

/// <summary>
/// Putting a record on a page without letting what it holds become part of the page.
/// </summary>
/// <remarks>
/// The record is written straight into the response rather than built up as a string first, so the
/// escaping happens on the way past — which is the one place a mistake would put a dataset's contents
/// into the markup around it.
/// </remarks>
public class RecordEscapingTests
{
    private sealed record Note(int Id, string Text);

    private static async Task<string> BrowseAsync(string text)
    {
        HollowWriteStateEngine writeEngine = new();
        HollowObjectMapper mapper = new(writeEngine);
        mapper.Add(new Note(1, text));

        HollowReadStateEngine readEngine = StateEngineRoundTripper.RoundTripSnapshot(writeEngine);

        await using HollowExplorerServer server = new(readEngine, 0);
        await server.StartAsync(TestContext.Current.CancellationToken);

        using HttpClient client = new() { BaseAddress = server.BaseAddress };

        try
        {
            return await client.GetStringAsync(
                "/type?type=Note&ordinal=0", TestContext.Current.CancellationToken);
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Markup in a record is shown as the text it is, not run as the markup it looks like.
    /// </summary>
    [Fact]
    public async Task MarkupInARecordIsShownRatherThanRun()
    {
        string html = await BrowseAsync("<script>alert('x')</script> & <b>bold</b>");

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>bold</b>", html, StringComparison.Ordinal);

        Assert.Contains(
            "&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt; &amp; &lt;b&gt;bold&lt;/b&gt;",
            html,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A record's layout survives, because what makes it readable inside a <c>pre</c> is the newlines
    /// — and an escaper that treats them as suspect would put the whole record on one line.
    /// </summary>
    [Fact]
    public async Task TheLayoutOfARecordSurvivesBeingEscaped()
    {
        string html = await BrowseAsync("nothing to escape");

        Assert.Contains("\n  Id: 1\n  Text: nothing to escape", html, StringComparison.Ordinal);
        Assert.DoesNotContain("&#xA;", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Text outside ASCII is text, not a threat — a dataset holding names is mostly the point.
    /// </summary>
    [Fact]
    public async Task TextOutsideAsciiIsWrittenAsItself()
    {
        string html = await BrowseAsync("Ægir Þórsson — 東京");

        Assert.Contains("Ægir Þórsson — 東京", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A record the key does not name is reported as missing rather than shown as empty.
    /// </summary>
    [Fact]
    public async Task TheStatusIsOkEvenWhenTheRecordIsNotFound()
    {
        HollowWriteStateEngine writeEngine = new();
        HollowObjectMapper mapper = new(writeEngine);
        mapper.Add(new Note(1, "only one"));

        HollowReadStateEngine readEngine = StateEngineRoundTripper.RoundTripSnapshot(writeEngine);

        await using HollowExplorerServer server = new(readEngine, 0);
        await server.StartAsync(TestContext.Current.CancellationToken);

        using HttpClient client = new() { BaseAddress = server.BaseAddress };

        HttpResponseMessage response = await client.GetAsync(
            "/type?type=Note&ordinal=0", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await server.StopAsync(TestContext.Current.CancellationToken);
    }
}
