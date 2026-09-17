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

using Hollow.Api.Consumer;
using Hollow.Api.Producer;
using Hollow.Reference.Infrastructure.Adapters;
using Hollow.Reference.Infrastructure.Storage.Local;
using Hollow.Reference.Model;
using Hollow.Reference.Model.Generated;
using ConsumerBlob = Hollow.Api.Consumer.Blob;
using GeneratedMovie = Hollow.Reference.Model.Generated.Movie;
using HollowProducer = Hollow.Api.Producer.HollowProducer;
using ModelActor = Hollow.Reference.Model.Actor;
using ModelMovie = Hollow.Reference.Model.Movie;

namespace Hollow.Reference.Tests;

/// <summary>
/// The producer and the consumer against each other, over the adapters, in the mode that ships
/// switched on.
/// </summary>
/// <remarks>
/// The Java reference implementation has no tests; these are the ones its quick-start guide asks a
/// reader to perform by hand — run the producer, run the consumer, see the films — written down so they
/// run in a second.
/// </remarks>
public sealed class LocalRoundTripTests
{
    [Fact]
    public void ACatalogueWrittenByTheProducerIsReadBackByTheConsumer()
    {
        using TemporaryDirectory root = new();
        using Harness harness = new(root.Path);

        long version = harness.Publish(Catalogue(("Rashomon", "Toshiro Mifune"), ("Ran", "Tatsuya Nakadai")));

        using HollowConsumer consumer = harness.CreateConsumer();
        consumer.TriggerRefreshTo(version);

        MovieApi api = (MovieApi)consumer.Api!;

        Assert.Equal(version, consumer.CurrentVersionId);
        Assert.Equal(
            ["Ran", "Rashomon"],
            api.AllMovie.Select(movie => movie.Title).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ASecondCycleIsReachedByADeltaRatherThanAReload()
    {
        // The whole reason for a delta chain: the consumer's state engine is updated in place, so the
        // second version costs what changed rather than what there is.
        using TemporaryDirectory root = new();
        using Harness harness = new(root.Path);

        long first = harness.Publish(Catalogue(("Rashomon", "Toshiro Mifune")));
        long second = harness.Publish(Catalogue(("Rashomon", "Toshiro Mifune"), ("Ran", "Tatsuya Nakadai")));

        using HollowConsumer consumer = harness.CreateConsumer();
        consumer.TriggerRefreshTo(first);

        object before = consumer.StateEngine!;

        consumer.TriggerRefreshTo(second);

        Assert.Same(before, consumer.StateEngine);
        Assert.Equal(2, ((MovieApi)consumer.Api!).AllMovie.Count());
    }

    [Fact]
    public void AVersionWithNoSnapshotOfItsOwnIsReachedThroughTheSnapshotIndex()
    {
        // A consumer starting cold asks for a snapshot of the announced version, and a producer that
        // keeps only every tenth snapshot has not written one. The index is what lets the consumer
        // start from the nearest snapshot below it and walk deltas forwards — the case it exists for.
        using TemporaryDirectory root = new();
        using Harness harness = new(root.Path, snapshotEveryNthCycle: 10);

        long first = harness.Publish(Catalogue(("Rashomon", "Toshiro Mifune")));
        long second = harness.Publish(Catalogue(("Rashomon", "Toshiro Mifune"), ("Ran", "Tatsuya Nakadai")));

        Assert.True(SnapshotExists(root.Path, first));
        Assert.False(SnapshotExists(root.Path, second));

        // What the index buys, stated directly: asked for a snapshot of a version there is none of,
        // the retriever answers with the newest one below it, and the delta out of that one exists.
        ConsumerBlob? snapshot = harness.BlobRetriever.RetrieveSnapshotBlob(second);

        Assert.NotNull(snapshot);
        Assert.Equal(first, snapshot.ToVersion);
        Assert.NotNull(harness.BlobRetriever.RetrieveDeltaBlob(first));

        using HollowConsumer consumer = harness.CreateConsumer();
        consumer.TriggerRefreshTo(second);

        Assert.Equal(second, consumer.CurrentVersionId);
        Assert.Equal(2, ((MovieApi)consumer.Api!).AllMovie.Count());

        // And the index is what made that possible, so it had better be there.
        Assert.True(File.Exists(Path.Combine(root.Path, "blobs", Namespace, "snapshot.index")));
    }

    private static bool SnapshotExists(string rootPath, long version) =>
        File.Exists(Path.Combine(
            rootPath,
            "blobs",
            BlobKeys.For(Namespace, BlobType.Snapshot, version).Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public async Task AConsumerWatchingTheFolderFollowsTheProducerWithoutBeingTold()
    {
        // The local mode's announcement store pushes rather than being polled, so this asserts on the
        // FileSystemWatcher path end to end: a file appears, and the consumer is on the new version.
        using TemporaryDirectory root = new();
        using Harness harness = new(root.Path);

        long first = harness.Publish(Catalogue(("Rashomon", "Toshiro Mifune")));

        using HollowAnnouncementStoreWatcher watcher = new(harness.AnnouncementStore);
        using HollowConsumer consumer = harness.CreateConsumer(watcher);

        Assert.True(watcher.IsPushBased);

        consumer.TriggerRefresh();
        Assert.Equal(first, consumer.CurrentVersionId);

        long second = harness.Publish(Catalogue(("Rashomon", "Toshiro Mifune"), ("Ran", "Tatsuya Nakadai")));

        await WaitFor(
            () => consumer.CurrentVersionId == second,
            $"the consumer stayed on {consumer.CurrentVersionId} instead of following to {second}");

        Assert.Equal(2, ((MovieApi)consumer.Api!).AllMovie.Count());
    }

    [Fact]
    public async Task APinnedConsumerStaysWhereItIsPutHoweverMuchIsAnnounced()
    {
        using TemporaryDirectory root = new();
        using Harness harness = new(root.Path);

        long first = harness.Publish(Catalogue(("Rashomon", "Toshiro Mifune")));

        harness.AnnouncementStore.Pin(first);

        _ = harness.Publish(Catalogue(("Rashomon", "Toshiro Mifune"), ("Ran", "Tatsuya Nakadai")));

        using HollowAnnouncementStoreWatcher watcher = new(harness.AnnouncementStore);

        await watcher.PollAsync(Token);

        Assert.Equal(first, watcher.GetLatestVersion());
        Assert.True(watcher.GetLatestVersionInfo().IsPinned);

        using HollowConsumer consumer = harness.CreateConsumer(watcher);
        consumer.TriggerRefresh();

        Assert.Equal(first, consumer.CurrentVersionId);
        Assert.Single(((MovieApi)consumer.Api!).AllMovie);
    }

    [Fact]
    public void TheCastSetIsReadBackThroughItsHashKey()
    {
        // [HollowHashKey("ActorName")] on Movie.Actors is what lets the set be asked about by name
        // rather than by building an Actor to hash — the one piece of the Java model that is not just
        // a field.
        using TemporaryDirectory root = new();
        using Harness harness = new(root.Path);

        long version = harness.Publish(Catalogue(("Ran", "Tatsuya Nakadai")));

        using HollowConsumer consumer = harness.CreateConsumer();
        consumer.TriggerRefreshTo(version);

        GeneratedMovie movie = ((MovieApi)consumer.Api!).AllMovie.Single();

        Assert.NotNull(movie.Actors);
        Assert.Equal("Tatsuya Nakadai", movie.Actors.Single().ActorName);
    }

    private const string Namespace = "catalogue";

    private static List<ModelMovie> Catalogue(params (string Title, string Actor)[] films)
    {
        List<ModelMovie> movies = [];
        int id = 1;

        foreach ((string title, string actor) in films)
        {
            movies.Add(new ModelMovie(id, title, [new ModelActor(1000 + id, actor)]));
            id++;
        }

        return movies;
    }

    private static async Task WaitFor(Func<bool> condition, string failure)
    {
        for (int attempt = 0; attempt < 150; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100, Token);
        }

        Assert.Fail(failure);
    }

    /// <summary>A producer and a consumer over one directory, wired the way the applications wire them.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly HollowBlobStorePublisher _publisher;
        private readonly HollowBlobStoreBlobRetriever _blobRetriever;
        private readonly HollowProducer _producer;
        private readonly string _stagingDirectory;

        internal Harness(string rootPath, int snapshotEveryNthCycle = 1)
        {
            // No pretend latency: the point of it is to make a demonstration realistic, and the point
            // of a test is to run in a second.
            LocalBlobStore blobStore = new(rootPath, SimulatedLatency.None);

            AnnouncementStore = new LocalAnnouncementStore(rootPath, SimulatedLatency.None);

            _publisher = new HollowBlobStorePublisher(blobStore, Namespace);
            _blobRetriever = new HollowBlobStoreBlobRetriever(blobStore, Namespace);
            _stagingDirectory = Path.Combine(rootPath, "staging");

            HollowProducerBuilder builder = new HollowProducerBuilder()
                .WithPublisher(_publisher)
                .WithAnnouncer(new HollowAnnouncementStoreAnnouncer(AnnouncementStore))
                .WithBlobStagingDirectory(_stagingDirectory);

            if (snapshotEveryNthCycle > 1)
            {
                // Skipping snapshots only takes effect with the integrity check off: the check reads
                // back the snapshot a cycle wrote, so a cycle that wrote none cannot be checked.
                builder = builder
                    .WithNumStatesBetweenSnapshots(snapshotEveryNthCycle)
                    .WithoutIntegrityCheck();
            }

            _producer = builder.Build();

            _producer.InitializeDataModel(typeof(ModelMovie));
        }

        internal LocalAnnouncementStore AnnouncementStore { get; }

        internal IBlobRetriever BlobRetriever => _blobRetriever;

        internal long Publish(IReadOnlyList<ModelMovie> movies) =>
            _producer.RunCycle(writeState =>
            {
                foreach (ModelMovie movie in movies)
                {
                    writeState.Add(movie);
                }
            });

        internal HollowConsumer CreateConsumer(IAnnouncementWatcher? announcementWatcher = null)
        {
            HollowConsumerBuilder builder = new HollowConsumerBuilder()
                .WithBlobRetriever(_blobRetriever)
                .WithApiFactory(new MovieApiFactory());

            return announcementWatcher is null
                ? builder.Build()
                : builder.WithAnnouncementWatcher(announcementWatcher).Build();
        }

        public void Dispose() => _publisher.Dispose();
    }
}
