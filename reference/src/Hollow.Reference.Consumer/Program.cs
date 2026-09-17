/*
 *  Copyright 2016 Netflix, Inc.
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
using Hollow.Api.Consumer;
using Hollow.Core.Index.Key;
using Hollow.Core.Tools.History;
using Hollow.Explorer;
using Hollow.Explorer.History;
using Hollow.Reference.Consumer;
using Hollow.Reference.Infrastructure;
using Hollow.Reference.Model.Generated;

// The consumer half of the reference implementation. It follows whatever the producer announces,
// keeps a typed client over it, and puts the explorer and the history UI on a web server so the data
// can be looked at while it moves.
//
// Ported from how.hollow.consumer.Consumer. Java starts two Jetty servers on two ports; this is one
// ASP.NET Core application with both UIs mounted in it, which is what a service that already has
// authentication in front of it wants — the UIs show the whole dataset, so they belong behind whatever
// guards the rest of the application rather than on a port of their own.

WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,

    // The settings file is linked into the build output rather than sitting in the project directory,
    // so the content root has to be where the binary is.
    ContentRootPath = AppContext.BaseDirectory,
});

// Logging for the parts that are built before the application is: the announcement watcher, and the
// wait for the producer's first version. Disposed when the process ends, after app.RunAsync returns.
using ILoggerFactory startupLogging = LoggerFactory.Create(logging => logging
    .AddConfiguration(builder.Configuration.GetSection("Logging"))
    .AddSimpleConsole(console => console.SingleLine = true));

ILogger logger = startupLogging.CreateLogger("Hollow.Reference.Consumer");

// ── The infrastructure ────────────────────────────────────────────────────────────────────────────

// Built rather than resolved, because the explorer has to be registered with a consumer that already
// exists and the consumer needs a blob retriever to be built from.
using HollowReferenceInfrastructure infrastructure =
    HollowReferenceInfrastructure.Create(builder.Configuration, startupLogging);

logger.LogInformation(
    "Consuming from the {Mode} infrastructure, under the namespace {Namespace}.",
    infrastructure.Options.Mode,
    infrastructure.Options.Namespace);

// ── The consumer ──────────────────────────────────────────────────────────────────────────────────

using HollowConsumer consumer = new HollowConsumerBuilder()
    .WithBlobRetriever(infrastructure.BlobRetriever)

    // With a watcher, the consumer goes where the producer says rather than where it is told: it loads
    // the announced version at start-up and follows every announcement after it.
    .WithAnnouncementWatcher(infrastructure.AnnouncementWatcher)

    // Without this the consumer still holds the data; it just has no typed view of it. With it,
    // consumer.Api is a MovieApi built afresh whenever the data underneath is replaced.
    .WithApiFactory(new MovieApiFactory())
    .Build();

await WaitForFirstVersionAsync(consumer, logger).ConfigureAwait(false);

logger.LogInformation(
    "Loaded version {Version}: {Records}.",
    consumer.CurrentVersionId,
    CatalogueQueries.Describe(consumer));

CatalogueQueries.Run(consumer, logger);

// ── The history ───────────────────────────────────────────────────────────────────────────────────

// A history is built on a read state as that state moves, not from a pile of blobs, so it starts at
// whatever the consumer has just loaded and is told about every transition after it.
HollowHistory history = new(
    consumer.StateEngine!,
    consumer.CurrentVersionId,
    infrastructure.Options.Consumer.MaxHistoricalStates);

consumer.AddRefreshListener(new CatalogueHistory(history, logger));

HollowHistoryUI historyUI = new(history);

// Which element of one film's cast is which of the other's. Without a hint the elements are paired by
// minimum difference, which is quadratic and sometimes surprising — a replaced actor can read as two
// changed names rather than one removal and one addition.
historyUI.AddMatchHint(new PrimaryKey("Actor", "ActorId"));

// ── The application ───────────────────────────────────────────────────────────────────────────────

builder.Services.AddHollowReferenceInfrastructure(infrastructure);
builder.Services.AddSingleton(consumer);

builder.Services.AddHollowExplorer(
    new HollowExplorer(consumer), infrastructure.Options.Consumer.ExplorerBasePath);

builder.Services.AddHollowHistoryUI(historyUI, infrastructure.Options.Consumer.HistoryBasePath);

WebApplication app = builder.Build();

app.MapGet("/", () => Results.Content(HomePage(consumer, history, infrastructure), "text/html; charset=utf-8"));

app.MapHollowExplorer();
app.MapHollowHistoryUI();

await app.RunAsync().ConfigureAwait(false);

/// <summary>
/// Waits until the producer has announced something and the consumer has loaded it.
/// </summary>
/// <remarks>
/// A consumer started before its producer has no data, and the history and the UIs all need some. The
/// Java reference implementation assumes the producer went first and throws if it did not; waiting is
/// both friendlier and what a service restarted before its producer would have to do anyway.
/// </remarks>
static async Task WaitForFirstVersionAsync(HollowConsumer consumer, ILogger logger)
{
    bool announced = false;

    while (consumer.CurrentVersionId == IAnnouncementWatcher.NoAnnouncementAvailable)
    {
        try
        {
            consumer.TriggerRefresh();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogDebug(e, "No version to load yet.");
        }

        if (consumer.CurrentVersionId != IAnnouncementWatcher.NoAnnouncementAvailable)
        {
            break;
        }

        if (!announced)
        {
            logger.LogInformation("Waiting for the producer to announce its first version.");
            announced = true;
        }

        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
    }
}

/// <summary>The application's own page, which is somewhere to come back to from the two UIs.</summary>
static string HomePage(
    HollowConsumer consumer, HollowHistory history, HollowReferenceInfrastructure infrastructure)
{
    string explorer = infrastructure.Options.Consumer.ExplorerBasePath;
    string historyPath = infrastructure.Options.Consumer.HistoryBasePath;

    // The states are newest first, so the last one is the furthest back this history reaches.
    // HollowHistory.OldestVersion is not that: it only means something once states have started being
    // dropped, and reads as "none" until then.
    string held = history.HistoricalStates is not [.., { Version: var oldest }]
        ? "no past versions yet; it grows one each time the producer cycles"
        : $"<b>{history.NumberOfHistoricalStates.ToString(CultureInfo.InvariantCulture)}</b> past "
            + $"versions, back to <b>{oldest.ToString(CultureInfo.InvariantCulture)}</b>";

    return $"""
        <!DOCTYPE html>
        <html lang="en">
        <head><title>Hollow reference implementation</title></head>
        <body>
            <h1>Hollow reference implementation</h1>
            <p>
                Reading from the <b>{infrastructure.Options.Mode}</b> infrastructure, namespace
                <b>{infrastructure.Options.Namespace}</b>.
            </p>
            <p>
                Currently on version <b>{consumer.CurrentVersionId.ToString(CultureInfo.InvariantCulture)}</b>,
                holding {CatalogueQueries.Describe(consumer)}.
            </p>
            <p>The history holds {held}.</p>
            <ul>
                <li><a href="{explorer}">Explorer</a> — every type and every record in the version being held.</li>
                <li><a href="{historyPath}">History</a> — what each cycle changed, and everything that ever happened to one film.</li>
            </ul>
            <p>
                Refresh this page as the producer cycles: the version moves, and the history grows a
                state each time.
            </p>
        </body>
        </html>
        """;
}
