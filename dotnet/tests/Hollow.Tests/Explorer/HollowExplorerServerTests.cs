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
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hollow.Tests.Explorer;

/// <summary>
/// Standing the explorer up, both on a port of its own and inside an application that already exists.
/// </summary>
public class HollowExplorerServerTests
{
    private sealed record Film(int Id, string Title);

    /// <summary>
    /// Ported from Java's <c>HollowExplorerUIServerTest</c>: a server over an empty state starts and
    /// stops, which is what most of the ways of getting this wrong show up as.
    /// </summary>
    [Fact]
    public async Task AServerOverAnEmptyStateStartsAndStops()
    {
        await using HollowExplorerServer server = new(new HollowReadStateEngine(), 0);

        await server.StartAsync(TestContext.Current.CancellationToken);

        using HttpClient client = new() { BaseAddress = server.BaseAddress };

        // Nothing to list, but the page that would list it still renders.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/home", TestContext.Current.CancellationToken)).StatusCode);

        await server.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The explorer is usually wanted inside the application already holding the dataset, mounted out
    /// of the way of whatever that application serves at the root.
    /// </summary>
    [Fact]
    public async Task TheExplorerCanBeMountedInsideAnotherApplication()
    {
        HollowWriteStateEngine writeEngine = new();
        HollowObjectMapper mapper = new(writeEngine);
        mapper.Add(new Film(1, "The Matrix"));

        HollowReadStateEngine readEngine = StateEngineRoundTripper.RoundTripSnapshot(writeEngine);

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddHollowExplorer(new HollowExplorer(readEngine), basePath: "/hollow");

        await using WebApplication app = builder.Build();

        app.MapGet("/", () => "the application itself");
        app.MapHollowExplorer();

        await app.StartAsync(TestContext.Current.CancellationToken);

        using HttpClient client = new() { BaseAddress = new Uri(app.Urls.First()) };

        Assert.Equal("the application itself", await client.GetStringAsync("/", TestContext.Current.CancellationToken));

        // The explorer's own pages, and the links it writes, all sit under where it was mounted.
        string html = await client.GetStringAsync("/hollow", TestContext.Current.CancellationToken);

        Assert.Contains(@"href=""/hollow/query""", html, StringComparison.Ordinal);
        Assert.Contains(
            "The Matrix",
            await client.GetStringAsync("/hollow/type?type=Film&ordinal=0", TestContext.Current.CancellationToken),
            StringComparison.Ordinal);

        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// An embedder can put its own cells in the navigation bar, which is how the explorer ends up
    /// looking like part of the application it was mounted in.
    /// </summary>
    [Fact]
    public async Task AnEmbedderCanAddToTheNavigationBar()
    {
        await using HollowExplorerServer server = new(new HollowReadStateEngine(), 0);

        server.Explorer.HeaderDisplayString = "the catalogue";
        server.Explorer.HeaderStringUrl = "https://example.invalid/catalogue";
        server.Explorer.AddCommonHeaderEntry("back", "<td class=\"nav\">[ <a href=\"/\">BACK</a> ]</td>", 0);

        await server.StartAsync(TestContext.Current.CancellationToken);

        using HttpClient client = new() { BaseAddress = server.BaseAddress };

        string html = await client.GetStringAsync("/home", TestContext.Current.CancellationToken);

        Assert.Contains("<title>the catalogue</title>", html, StringComparison.Ordinal);
        Assert.Contains(@"href=""https://example.invalid/catalogue""", html, StringComparison.Ordinal);
        Assert.Contains(@"[ <a href=""/"">BACK</a> ]", html, StringComparison.Ordinal);

        server.Explorer.RemoveCommonHeaderEntry("back");

        Assert.DoesNotContain("BACK", await client.GetStringAsync("/home", TestContext.Current.CancellationToken), StringComparison.Ordinal);

        await server.StopAsync(TestContext.Current.CancellationToken);
    }
}
