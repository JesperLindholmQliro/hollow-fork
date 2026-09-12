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

using Hollow.Api.Consumer;
using Hollow.Api.Consumer.Fs;
using Hollow.Api.Producer;
using Hollow.Api.Producer.Fs;
using Hollow.Core.Read.Engine;
using Hollow.Explorer;
using Hollow.Explorer.Sample;

// An ASP.NET Core application with the Hollow explorer inside it.
//
// The application has a dataset — a producer publishing a film catalogue, a consumer following it —
// and mounts the explorer at /hollow so that whoever runs it can look at that dataset without writing
// a page for it. This is the arrangement the explorer is for: it goes in the process that already has
// the data, and therefore already has whatever authentication guards it.

string blobDirectory = Path.Combine(Path.GetTempPath(), $"hollow-explorer-sample-{Environment.ProcessId}");
Directory.CreateDirectory(blobDirectory);

// ── The dataset ───────────────────────────────────────────────────────────────────────────────────

HollowProducer producer = new HollowProducerBuilder()
    .WithPublisher(new HollowFilesystemPublisher(blobDirectory))
    .WithAnnouncer(new HollowFilesystemAnnouncer(blobDirectory))
    .WithBlobStagingDirectory(Path.Combine(blobDirectory, "staging"))
    .Build();

producer.InitializeDataModel(typeof(Film));
producer.RunCycle(Populate(Catalogue.Version1));

HollowConsumer consumer = new HollowConsumerBuilder()
    .WithBlobRetriever(new HollowFilesystemBlobRetriever(blobDirectory))
    .Build();

consumer.TriggerRefresh();

// ── The explorer ──────────────────────────────────────────────────────────────────────────────────

// Handed the consumer rather than a read state, so it follows whatever version the consumer is on
// rather than being pinned to the one it was given.
HollowExplorer explorer = new(consumer);

// What the navigation bar says this dataset is, and where that links to.
explorer.HeaderDisplayString = "film catalogue";
explorer.HeaderStringUrl = "https://github.com/Netflix/hollow";

// A cell of the application's own in the explorer's navigation bar, so that the explorer reads as part
// of the application rather than as somewhere else the reader has ended up. The value is markup, so
// only put markup there that you wrote.
explorer.AddCommonHeaderEntry(
    "back", """<td class="nav">[ <a href="/">&#8592; back to the app</a> ]</td>""", position: 0);

// The one thing an embedder has to remember. Each type's heap and hole figures are worked out once per
// state rather than once per request, and the cache notices a new state by its randomized tag — which a
// delta does not change. So a consumer that moves by delta has to say when it has moved.
consumer.AddRefreshListener(new ExplorerCacheInvalidator(explorer));

// ── The application ───────────────────────────────────────────────────────────────────────────────

int published = 1;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

if (builder.Configuration["urls"] is null)
{
    builder.WebHost.UseUrls("http://127.0.0.1:7101");
}

builder.Services.AddHollowExplorer(explorer, basePath: "/hollow");

WebApplication app = builder.Build();

// The application's own page, which is all the rest of this sample is: somewhere to come back to, and
// a button that publishes the next cycle so the explorer can be watched following it.
app.MapGet("/", () => Results.Content(HomePage(consumer), "text/html; charset=utf-8"));

app.MapPost(
    "/publish",
    () =>
    {
        // Alternating keeps every press a real cycle. Publishing the same catalogue twice would be a
        // cycle with nothing in it, which the producer is right to make no version for.
        published = published == 1 ? 2 : 1;
        producer.RunCycle(Populate(published == 1 ? Catalogue.Version1 : Catalogue.Version2));

        // In a real application an announcement watcher would do this; here it is explicit so that the
        // page has finished refreshing by the time it redirects.
        consumer.TriggerRefresh();

        return Results.Redirect("/");
    });

app.MapHollowExplorer();

try
{
    app.Run();
}
finally
{
    // In a finally rather than on ApplicationStopping, because that never fires if the host fails to
    // start — the port already being in use is the likely way to run this twice — and the blobs would
    // be left behind.
    Directory.Delete(blobDirectory, recursive: true);
}

static Populator Populate(IReadOnlyList<Film> films) =>
    state =>
    {
        foreach (Film film in films)
        {
            state.Add(film);
        }
    };

static string HomePage(HollowConsumer consumer)
{
    HollowReadStateEngine stateEngine = consumer.StateEngine!;
    int films = stateEngine.GetTypeState("Film")!.PopulatedOrdinals.Cardinality();

    return $"""
        <!DOCTYPE html>
        <html lang="en">
        <head><title>Film catalogue</title></head>
        <body>
            <h1>Film catalogue</h1>
            <p>An application that happens to hold a Hollow dataset: version
               <b>{consumer.CurrentVersionId}</b>, <b>{films}</b> films.</p>
            <p><a href="/hollow">Explore the dataset &#8594;</a></p>
            <form method="post" action="/publish">
                <button type="submit">Publish the next cycle</button>
            </form>
            <p>
                Publishing swaps the catalogue over: a film is retitled, one is dropped and one is
                added. The consumer follows it by delta, so the dropped film leaves a hole behind —
                which the explorer's home page will show as a type costing more than its records do.
            </p>
        </body>
        </html>
        """;
}

/// <summary>
/// Tells the explorer to work its heap figures out again whenever the data underneath it changes.
/// </summary>
/// <remarks>
/// Walking every type's ordinals is slow enough to be worth doing once per state, and a delta leaves
/// the state's randomized tag alone — so nothing but this would tell the cache it has gone stale.
/// Prefilling here rather than leaving it to the next request keeps the cost off whoever asks first.
/// </remarks>
internal sealed class ExplorerCacheInvalidator(HollowExplorer explorer) : HollowRefreshListener
{
    public override void RefreshSuccessful(long beforeVersion, long afterVersion, long requestedVersion)
    {
        explorer.ClearCache();
        explorer.PrefillHeapStatsCache();
    }
}
