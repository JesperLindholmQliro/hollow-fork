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
using Hollow.Api.Consumer;
using Hollow.Api.Consumer.Fs;
using Hollow.Api.Producer;
using Hollow.Api.Producer.Fs;
using Hollow.Core.Read.Engine;
using Hollow.Core.Tools.History;
using Hollow.Explorer;
using Hollow.Explorer.History;
using Hollow.FakeData;

// Produces a fake book catalogue with some entropy in the delta chain, and serves the explorer and
// the history over it while it does.
//
// This is not a sample of how to use Hollow — the other four are. It is a generator: something to
// point at the explorer and the history UI when what those need is a dataset big enough and churny
// enough to be worth looking at. The whole run stays on this machine, in a directory under the
// temporary path, and nothing announces itself to anyone.

Options options = Options.Parse(args);

Console.WriteLine($"Writing fake data blobs to local path: {options.BlobPath}");
Directory.CreateDirectory(options.BlobPath);

// ── The catalogue ─────────────────────────────────────────────────────────────────────────────────

FakeText text = new(options.Seed);
Catalogue catalogue = new(text);

catalogue.PopulateArtists(options.Artists);
catalogue.PopulateBooks(options.Books);

Console.WriteLine(string.Create(
    CultureInfo.InvariantCulture,
    $"Generated {catalogue.Books.Count} book records from {options.Books} books, seed {options.Seed}."));

// ── The producer ──────────────────────────────────────────────────────────────────────────────────

HollowProducer producer = new HollowProducerBuilder()
    .WithPublisher(new HollowFilesystemPublisher(options.BlobPath))
    .WithAnnouncer(new HollowFilesystemAnnouncer(options.BlobPath))
    .WithBlobStagingDirectory(Path.Combine(options.BlobPath, "staging"))

    // Three snapshots across the run, so that a consumer joining late has somewhere near to start
    // from without the run being mostly snapshot writing.
    .WithNumStatesBetweenSnapshots(Math.Max(1, options.Cycles / 3))
    .Build();

// Declared rather than inferred, so that a type stays in the schema through a cycle that happens to
// publish no record of it.
producer.InitializeDataModel(typeof(Book));

producer.RunCycle(Publish(catalogue.Books));

// ── The consumer, the explorer and the history ────────────────────────────────────────────────────

HollowConsumer consumer = new HollowConsumerBuilder()
    .WithBlobRetriever(new HollowFilesystemBlobRetriever(options.BlobPath))
    .Build();

consumer.TriggerRefresh();

// Handed the consumer rather than a read state, so the explorer follows whatever the cycle loop
// publishes rather than being pinned to the first version.
HollowExplorer explorer = new(consumer)
{
    HeaderDisplayString = "fake book catalogue",
    HeaderStringUrl = "https://github.com/Netflix/hollow",
};

// A history is built on a read state as it moves, not from a pile of blobs, so it starts wherever the
// consumer is and is told about each transition as it happens.
HollowHistory history = new(
    consumer.StateEngine!, consumer.CurrentVersionId, maxHistoricalStatesToKeep: options.Cycles + 1);

consumer.AddRefreshListener(new UiFollower(explorer, history));

await using HollowExplorerServer explorerServer = new(explorer, options.ExplorerPort);
await using HollowHistoryUIServer historyServer = new(history, options.HistoryPort);

await explorerServer.StartAsync();
await historyServer.StartAsync();

Console.WriteLine($"Explorer started at {explorerServer.BaseAddress}");
Console.WriteLine($"History server started listening at {historyServer.BaseAddress}");

// ── The cycles ────────────────────────────────────────────────────────────────────────────────────

using CancellationTokenSource stopping = new();

Console.CancelKeyPress += (_, eventArgs) =>
{
    // Handled here rather than left to terminate the process, so that a run interrupted halfway
    // through still shuts the two servers down in an orderly way.
    eventArgs.Cancel = true;
    stopping.Cancel();
};

for (int cycle = 1; cycle < options.Cycles && !stopping.IsCancellationRequested; cycle++)
{
    // Worked out before the cycle rather than inside it: the populator may run more than once if the
    // producer has to retry, and the churn is meant to be decided once.
    HashSet<int> toModify = catalogue.RandomBookIds(text.Next(options.MaxModificationsPerCycle));
    HashSet<int> toRemove = catalogue.RandomBookIds(text.Next(options.MaxRemovesPerCycle));
    int toAdd = text.Next(options.MaxAddsPerCycle);

    // A book cannot both change and go away in one cycle; the change is the more interesting of the
    // two to look at, so it wins.
    toRemove.ExceptWith(toModify);

    catalogue.PopulateBooks(toAdd);

    foreach (Book book in catalogue.Books)
    {
        if (toModify.Contains(book.Id.Value))
        {
            catalogue.ModifyBook(book);
        }
    }

    long version = producer.RunCycle(
        state =>
        {
            foreach (Book book in catalogue.Books)
            {
                // Dropped for this cycle only. The book stays in the catalogue, so a later cycle
                // may well publish it again — which is the point of the churn.
                if (!toRemove.Contains(book.Id.Value))
                {
                    state.Add(book);
                }
            }
        });

    consumer.TriggerRefreshTo(version);

    Console.WriteLine(string.Create(
        CultureInfo.InvariantCulture,
        $"Cycle {cycle + 1}/{options.Cycles}: version {version}, "
        + $"{toModify.Count} changed, {toRemove.Count} dropped, {toAdd} added, "
        + $"{history.NumberOfHistoricalStates} historical states."));
}

Console.WriteLine("Done publishing. Press Ctrl+C to stop the servers.");

try
{
    await Task.Delay(Timeout.InfiniteTimeSpan, stopping.Token);
}
catch (OperationCanceledException)
{
    // Ctrl+C, which is the only way out of here.
}

await explorerServer.StopAsync();
await historyServer.StopAsync();

static Populator Publish(IReadOnlyList<Book> books) =>
    state =>
    {
        foreach (Book book in books)
        {
            state.Add(book);
        }
    };

/// <summary>
/// Keeps the explorer and the history in step with the consumer they are reading.
/// </summary>
/// <remarks>
/// Java's <c>HollowHistoryUIServer(consumer, port)</c> builds a history and attaches a listener like
/// this one itself. This port keeps the two apart — a history is useful without a UI over it — so the
/// wiring is here, where it can be read.
/// </remarks>
internal sealed class UiFollower(HollowExplorer explorer, HollowHistory history) : HollowRefreshListener
{
    public override void DeltaUpdateOccurred(HollowReadStateEngine stateEngine, long version) =>
        // Called once the delta has been applied, with the version just moved to. This is where the
        // history takes its copy of the records that transition dropped: nothing else will have them,
        // and a moment later the space they occupied is the read state's to reuse.
        history.DeltaOccurred(version);

    public override void SnapshotUpdateOccurred(HollowReadStateEngine stateEngine, long version) =>
        // A consumer that fell far enough behind reloads rather than catching up by delta. A history
        // can still say what changed across that gap, but it has to compare two whole states to do it.
        history.DoubleSnapshotOccurred(stateEngine, version);

    public override void RefreshSuccessful(long beforeVersion, long afterVersion, long requestedVersion) =>
        // Each type's heap and hole figures are worked out once per state, and the cache notices a new
        // state by its randomized tag — which a delta does not change. So a consumer moving by delta
        // has to say when it has moved. Not prefilled: at this size, working them out a hundred times
        // over would cost more than the reader who asks for them once.
        explorer.ClearCache();
}

/// <summary>
/// What to generate, and where to put it.
/// </summary>
/// <remarks>
/// Java hard-codes these as constants and asks you to recompile. The defaults here are Java's, and
/// every one of them can be said on the command line instead.
/// </remarks>
internal sealed record Options
{
    /// <remarks>Java's <c>/tmp/fakehollowdata</c>, spelled in a way that works off Linux too.</remarks>
    internal string BlobPath { get; init; } = Path.Combine(Path.GetTempPath(), "fakehollowdata");

    /// <summary>How many producer cycles to run — the number of states in the delta chain.</summary>
    internal int Cycles { get; init; } = 100;

    /// <summary>How many books to start with, before the per-country fan-out.</summary>
    internal int Books { get; init; } = 10_000;

    /// <summary>How many artists the cover art draws from.</summary>
    internal int Artists { get; init; } = 1_000;

    internal int MaxAddsPerCycle { get; init; } = 200;

    internal int MaxRemovesPerCycle { get; init; } = 100;

    internal int MaxModificationsPerCycle { get; init; } = 1_000;

    /// <summary>What makes the run reproducible.</summary>
    internal int Seed { get; init; } = 20160101;

    internal int ExplorerPort { get; init; } = 7001;

    internal int HistoryPort { get; init; } = 7002;

    internal static Options Parse(string[] args)
    {
        Options options = new();

        for (int i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"{args[i]} was given without a value", nameof(args));
            }

            string value = args[i + 1];

            options = args[i] switch
            {
                "--blob-path" => options with { BlobPath = value },
                "--cycles" => options with { Cycles = Number(value) },
                "--books" => options with { Books = Number(value) },
                "--artists" => options with { Artists = Number(value) },
                "--max-adds" => options with { MaxAddsPerCycle = Number(value) },
                "--max-removes" => options with { MaxRemovesPerCycle = Number(value) },
                "--max-modifications" => options with { MaxModificationsPerCycle = Number(value) },
                "--seed" => options with { Seed = Number(value) },
                "--explorer-port" => options with { ExplorerPort = Number(value) },
                "--history-port" => options with { HistoryPort = Number(value) },
                _ => throw new ArgumentException($"unknown option {args[i]}", nameof(args)),
            };
        }

        return options;

        static int Number(string value) => int.Parse(value, CultureInfo.InvariantCulture);
    }
}
