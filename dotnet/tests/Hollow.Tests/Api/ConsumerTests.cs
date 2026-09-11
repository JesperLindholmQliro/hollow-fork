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
using Hollow.Core;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Read.Filter;
using Hollow.Core.Schema;
using Hollow.Core.Util;
using Hollow.Core.Write;

namespace Hollow.Tests.Api;

/// <summary>
/// A consumer keeps a local copy of a dataset in step with what a producer publishes.
/// </summary>
/// <remarks>
/// The distinction these tests keep returning to is between following deltas and loading a snapshot.
/// Following deltas keeps the consumer's ordinals — and therefore any index built over them — valid;
/// loading a snapshot over existing data throws all of that away and briefly doubles the memory. A
/// consumer should do the former whenever it can and the latter only when it must.
/// </remarks>
public class ConsumerTests
{
    private static HollowObjectSchema MovieSchema()
    {
        HollowObjectSchema schema = new("Movie", 2);
        schema.AddField("id", FieldType.Int);
        schema.AddField("title", FieldType.String);
        return schema;
    }

    private static void AddMovie(HollowWriteStateEngine engine, HollowObjectSchema schema, int id, string title)
    {
        HollowObjectWriteRecord record = new(schema);
        record.SetInt("id", id);
        record.SetString("title", title);
        engine.Add("Movie", record);
    }

    /// <summary>The titles a consumer currently holds, in ordinal order.</summary>
    private static IReadOnlyList<string> Titles(HollowConsumer consumer)
    {
        HollowObjectTypeReadState state =
            Assert.IsType<HollowObjectTypeReadState>(consumer.StateEngine!.GetTypeState("Movie"));

        int titlePosition = state.Schema.GetPosition("title");

        return
        [
            .. Enumerable.Range(0, state.MaxOrdinal + 1)
                .Where(ordinal => state.PopulatedOrdinals.Get(ordinal))
                .Select(ordinal => state.ReadString(ordinal, titlePosition)!)
        ];
    }

    /// <summary>
    /// Publishes three cycles — one, two, three movies — and returns the store.
    /// </summary>
    private static InMemoryBlobStore PublishThreeCycles(HollowObjectSchema schema)
    {
        InMemoryBlobStore blobStore = new();
        HollowWriteStateEngine producer = HollowWriteStateCreator.CreateWithSchemas([schema]);

        AddMovie(producer, schema, 1, "one");
        blobStore.Publish(producer, 1);

        AddMovie(producer, schema, 1, "one");
        AddMovie(producer, schema, 2, "two");
        blobStore.Publish(producer, 2);

        AddMovie(producer, schema, 1, "one");
        AddMovie(producer, schema, 2, "two");
        AddMovie(producer, schema, 3, "three");
        blobStore.Publish(producer, 3);

        return blobStore;
    }

    private static HollowConsumer Consumer(InMemoryBlobStore blobStore, IDoubleSnapshotConfig? config = null)
    {
        HollowConsumerBuilder builder = new HollowConsumerBuilder().WithBlobRetriever(blobStore);

        if (config is not null)
        {
            builder.WithDoubleSnapshotConfig(config);
        }

        return builder.Build();
    }

    [Fact]
    public void AConsumerHoldsNothingUntilItIsRefreshed()
    {
        HollowObjectSchema schema = MovieSchema();
        using HollowConsumer consumer = Consumer(PublishThreeCycles(schema));

        Assert.Null(consumer.StateEngine);
        Assert.Equal(HollowConstants.VersionNone, consumer.CurrentVersionId);
        Assert.False(consumer.InitialLoad.IsCompleted);
    }

    [Fact]
    public async Task RefreshingLoadsTheRequestedVersion()
    {
        HollowObjectSchema schema = MovieSchema();
        using HollowConsumer consumer = Consumer(PublishThreeCycles(schema));

        consumer.TriggerRefreshTo(2);

        Assert.Equal(2, consumer.CurrentVersionId);
        Assert.Equal(["one", "two"], Titles(consumer));
        Assert.Equal(2, await consumer.InitialLoad);
    }

    [Fact]
    public void RefreshingWithoutAVersionGoesToTheLatest()
    {
        HollowObjectSchema schema = MovieSchema();
        using HollowConsumer consumer = Consumer(PublishThreeCycles(schema));

        consumer.TriggerRefresh();

        Assert.Equal(3, consumer.CurrentVersionId);
        Assert.Equal(["one", "two", "three"], Titles(consumer));
    }

    /// <summary>
    /// The property that makes deltas worth having: the consumer's state engine survives, so anything
    /// holding a reference to it — an index, say — is still looking at live data.
    /// </summary>
    [Fact]
    public void FollowingDeltasKeepsTheSameStateEngine()
    {
        HollowObjectSchema schema = MovieSchema();
        using HollowConsumer consumer = Consumer(PublishThreeCycles(schema));

        consumer.TriggerRefreshTo(1);
        HollowReadStateEngine afterFirst = consumer.StateEngine!;

        consumer.TriggerRefreshTo(3);

        Assert.Same(afterFirst, consumer.StateEngine);
        Assert.Equal(3, consumer.CurrentVersionId);
        Assert.Equal(["one", "two", "three"], Titles(consumer));
    }

    [Fact]
    public void AConsumerCanGoBackwardsThroughReverseDeltas()
    {
        HollowObjectSchema schema = MovieSchema();
        using HollowConsumer consumer = Consumer(PublishThreeCycles(schema));

        consumer.TriggerRefreshTo(3);
        HollowReadStateEngine stateEngine = consumer.StateEngine!;

        consumer.TriggerRefreshTo(1);

        Assert.Equal(1, consumer.CurrentVersionId);
        Assert.Equal(["one"], Titles(consumer));

        // Backwards is the same mechanism as forwards, so the state engine survives that too.
        Assert.Same(stateEngine, consumer.StateEngine);
    }

    /// <summary>
    /// With no delta path to the destination the consumer has to start again from a snapshot, and
    /// everything built over the old state becomes stale.
    /// </summary>
    [Fact]
    public void ABrokenDeltaChainForcesASnapshot()
    {
        HollowObjectSchema schema = MovieSchema();
        InMemoryBlobStore blobStore = new();
        HollowWriteStateEngine producer = HollowWriteStateCreator.CreateWithSchemas([schema]);

        AddMovie(producer, schema, 1, "one");
        blobStore.Publish(producer, 1);

        // A second chain, unconnected to the first: no delta joins version 1 to version 2.
        HollowWriteStateEngine forkedProducer = HollowWriteStateCreator.CreateWithSchemas([MovieSchema()]);
        AddMovie(forkedProducer, schema, 9, "nine");
        blobStore.Publish(forkedProducer, 2, withDeltas: false);

        using HollowConsumer consumer = Consumer(blobStore);

        consumer.TriggerRefreshTo(1);
        HollowReadStateEngine afterFirst = consumer.StateEngine!;

        consumer.TriggerRefreshTo(2);

        Assert.Equal(2, consumer.CurrentVersionId);
        Assert.Equal(["nine"], Titles(consumer));
        Assert.NotSame(afterFirst, consumer.StateEngine);
    }

    [Fact]
    public void ADeltaOnlyConsumerStaysPutRatherThanDoubleSnapshotting()
    {
        HollowObjectSchema schema = MovieSchema();
        InMemoryBlobStore blobStore = new();
        HollowWriteStateEngine producer = HollowWriteStateCreator.CreateWithSchemas([schema]);

        AddMovie(producer, schema, 1, "one");
        blobStore.Publish(producer, 1);

        HollowWriteStateEngine forkedProducer = HollowWriteStateCreator.CreateWithSchemas([MovieSchema()]);
        AddMovie(forkedProducer, schema, 9, "nine");
        blobStore.Publish(forkedProducer, 2, withDeltas: false);

        using HollowConsumer consumer = Consumer(blobStore, DoubleSnapshotConfig.DeltasOnly);

        consumer.TriggerRefreshTo(1);

        // Reaching version 2 would mean a snapshot, which this consumer forbids. It says so rather
        // than silently doing nothing — but it would rather serve slightly stale data than throw away
        // its indexes, so it stays where it is.
        Assert.Throws<InvalidOperationException>(() => consumer.TriggerRefreshTo(2));

        Assert.Equal(1, consumer.CurrentVersionId);
        Assert.Equal(["one"], Titles(consumer));
    }

    /// <summary>
    /// Past a certain number of deltas, replaying them all costs more than starting over.
    /// </summary>
    [Fact]
    public void TooManyDeltasInOneRefreshBecomeASnapshot()
    {
        HollowObjectSchema schema = MovieSchema();
        InMemoryBlobStore blobStore = new();
        HollowWriteStateEngine producer = HollowWriteStateCreator.CreateWithSchemas([schema]);

        for (int version = 1; version <= 6; version++)
        {
            for (int id = 1; id <= version; id++)
            {
                AddMovie(producer, schema, id, $"movie {id}");
            }

            blobStore.Publish(producer, version);
        }

        using HollowConsumer consumer = Consumer(
            blobStore, new DoubleSnapshotConfig { MaxDeltasBeforeDoubleSnapshot = 2 });

        consumer.TriggerRefreshTo(1);
        HollowReadStateEngine afterFirst = consumer.StateEngine!;

        consumer.TriggerRefreshTo(6);

        Assert.Equal(6, consumer.CurrentVersionId);
        Assert.NotSame(afterFirst, consumer.StateEngine);

        // Two deltas would have been within budget.
        using HollowConsumer patientConsumer = Consumer(
            blobStore, new DoubleSnapshotConfig { MaxDeltasBeforeDoubleSnapshot = 2 });

        patientConsumer.TriggerRefreshTo(1);
        HollowReadStateEngine patientState = patientConsumer.StateEngine!;

        patientConsumer.TriggerRefreshTo(3);

        Assert.Same(patientState, patientConsumer.StateEngine);
    }

    [Fact]
    public void AFailedRefreshLeavesTheConsumerOnItsOldData()
    {
        HollowObjectSchema schema = MovieSchema();
        InMemoryBlobStore blobStore = PublishThreeCycles(schema);

        using HollowConsumer consumer = Consumer(blobStore);
        consumer.TriggerRefreshTo(1);

        UnreadableBlobRetriever broken = new(blobStore, failFrom: 1);
        using HollowConsumer brokenConsumer = new HollowConsumerBuilder().WithBlobRetriever(broken).Build();

        brokenConsumer.TriggerRefreshTo(1);
        Assert.Equal(1, brokenConsumer.CurrentVersionId);

        Assert.ThrowsAny<Exception>(() => brokenConsumer.TriggerRefreshTo(2));

        Assert.Equal(1, brokenConsumer.CurrentVersionId);
        Assert.Equal(["one"], Titles(brokenConsumer));
    }

    /// <summary>
    /// A transition that has already failed is not attempted again, until the consumer is told to
    /// forget it.
    /// </summary>
    [Fact]
    public void AFailedTransitionIsRememberedUntilItIsCleared()
    {
        HollowObjectSchema schema = MovieSchema();
        UnreadableBlobRetriever broken = new(PublishThreeCycles(schema), failFrom: 1);

        using HollowConsumer consumer = new HollowConsumerBuilder().WithBlobRetriever(broken).Build();

        consumer.TriggerRefreshTo(1);
        Assert.Equal(0, consumer.NumFailedDeltaTransitions);

        Assert.ThrowsAny<Exception>(() => consumer.TriggerRefreshTo(2));
        Assert.Equal(1, consumer.NumFailedDeltaTransitions);

        consumer.ClearFailedTransitions();
        Assert.Equal(0, consumer.NumFailedDeltaTransitions);
    }

    [Fact]
    public void TheConsumerFallsBackToTheNearestEarlierSnapshot()
    {
        HollowObjectSchema schema = MovieSchema();
        InMemoryBlobStore blobStore = PublishThreeCycles(schema);
        blobStore.RemoveSnapshot(3);

        using HollowConsumer consumer = Consumer(blobStore);

        // Version 3 has no snapshot, so the plan starts at version 2 and catches up with a delta.
        consumer.TriggerRefreshTo(3);

        Assert.Equal(3, consumer.CurrentVersionId);
        Assert.Equal(["one", "two", "three"], Titles(consumer));
    }

    [Fact]
    public void AskingForAVersionThatWasNeverPublishedFails()
    {
        HollowObjectSchema schema = MovieSchema();
        using HollowConsumer consumer = Consumer(PublishThreeCycles(schema));

        // Older than anything in the store, so there is nothing to build on.
        Assert.Throws<InvalidOperationException>(() => consumer.TriggerRefreshTo(0));
        Assert.Equal(HollowConstants.VersionNone, consumer.CurrentVersionId);
    }

    [Fact]
    public void ATypeFilterIsHonoured()
    {
        HollowObjectSchema movieSchema = MovieSchema();
        HollowObjectSchema actorSchema = new("Actor", 1);
        actorSchema.AddField("name", FieldType.String);

        InMemoryBlobStore blobStore = new();
        HollowWriteStateEngine producer = HollowWriteStateCreator.CreateWithSchemas([movieSchema, actorSchema]);

        AddMovie(producer, movieSchema, 1, "one");

        HollowObjectWriteRecord actor = new(actorSchema);
        actor.SetString("name", "someone");
        producer.Add("Actor", actor);

        blobStore.Publish(producer, 1);

        using HollowConsumer consumer = new HollowConsumerBuilder()
            .WithBlobRetriever(blobStore)
            .WithTypeFilter(TypeFilter.Include(["Movie"]))
            .Build();

        consumer.TriggerRefresh();

        Assert.NotNull(consumer.StateEngine!.GetTypeState("Movie"));
        Assert.Null(consumer.StateEngine.GetTypeState("Actor"));
    }

    private sealed class RecordingListener : HollowRefreshListener
    {
        internal List<string> Events { get; } = [];

        public override void RefreshStarted(long currentVersion, long requestedVersion) =>
            Events.Add($"started {currentVersion.Invariant()}->{requestedVersion.Invariant()}");

        public override void TransitionsPlanned(
            long beforeVersion, long desiredVersion, bool isSnapshotPlan, IReadOnlyList<BlobType> transitionSequence) =>
            Events.Add($"planned {string.Join('+', transitionSequence)}");

        public override void BlobLoaded(Blob transition) => Events.Add($"loaded {transition.BlobType}");

        public override void SnapshotApplied(HollowReadStateEngine stateEngine, long version) =>
            Events.Add($"snapshotApplied {version.Invariant()}");

        public override void DeltaApplied(HollowReadStateEngine stateEngine, long version) =>
            Events.Add($"deltaApplied {version.Invariant()}");

        public override void SnapshotUpdateOccurred(HollowReadStateEngine stateEngine, long version) =>
            Events.Add($"snapshotUpdate {version.Invariant()}");

        public override void DeltaUpdateOccurred(HollowReadStateEngine stateEngine, long version) =>
            Events.Add($"deltaUpdate {version.Invariant()}");

        public override void RefreshSuccessful(long beforeVersion, long afterVersion, long requestedVersion) =>
            Events.Add($"successful {afterVersion.Invariant()}");

        public override void RefreshFailed(
            long beforeVersion, long afterVersion, long requestedVersion, Exception failureCause) =>
            Events.Add($"failed {failureCause.GetType().Name}");
    }

    /// <summary>
    /// Exactly one of <c>SnapshotUpdateOccurred</c> and <c>DeltaUpdateOccurred</c> fires per refresh,
    /// which is how a listener knows whether to rebuild its indexing or update it in place.
    /// </summary>
    [Fact]
    public void AListenerIsToldWhetherToRebuildOrUpdateInPlace()
    {
        HollowObjectSchema schema = MovieSchema();
        InMemoryBlobStore blobStore = PublishThreeCycles(schema);
        blobStore.RemoveSnapshot(2);
        blobStore.RemoveSnapshot(3);

        RecordingListener listener = new();

        using HollowConsumer consumer = new HollowConsumerBuilder()
            .WithBlobRetriever(blobStore)
            .WithRefreshListeners(listener)
            .Build();

        consumer.TriggerRefreshTo(3);

        Assert.Equal(
            [
                "started -9223372036854775808->3",
                "planned Snapshot+Delta+Delta",
                "loaded Snapshot",
                "snapshotApplied 1",
                "loaded Delta",
                "deltaApplied 2",
                "loaded Delta",
                "deltaApplied 3",

                // One snapshot update for the refresh as a whole, and no delta updates at all: the data
                // was replaced, so nothing derived from the old ordinals is salvageable.
                "snapshotUpdate 3",
                "successful 3",
            ],
            listener.Events);

        listener.Events.Clear();
        consumer.TriggerRefreshTo(1);

        Assert.Equal(
            [
                "started 3->1",
                "planned ReverseDelta+ReverseDelta",
                "loaded ReverseDelta",
                "deltaUpdate 2",
                "deltaApplied 2",
                "loaded ReverseDelta",
                "deltaUpdate 1",
                "deltaApplied 1",
                "successful 1",
            ],
            listener.Events);
    }

    private sealed class ThrowingListener : HollowRefreshListener
    {
        public override void SnapshotUpdateOccurred(HollowReadStateEngine stateEngine, long version) =>
            throw new InvalidOperationException("this listener cannot cope with this data");
    }

    /// <summary>
    /// A listener that cannot cope with the new data fails the refresh, because the alternative is a
    /// consumer whose data and whose indexes disagree.
    /// </summary>
    [Fact]
    public void AListenerThatThrowsFailsTheRefresh()
    {
        HollowObjectSchema schema = MovieSchema();
        RecordingListener recorder = new();

        using HollowConsumer consumer = new HollowConsumerBuilder()
            .WithBlobRetriever(PublishThreeCycles(schema))
            .WithRefreshListeners(new ThrowingListener(), recorder)
            .Build();

        Assert.Throws<InvalidOperationException>(() => consumer.TriggerRefreshTo(1));

        Assert.Contains("failed InvalidOperationException", recorder.Events);
        Assert.Equal(1, consumer.NumFailedSnapshotTransitions);
    }

    [Fact]
    public void AddedAndRemovedListenersTakeEffectOnTheNextRefresh()
    {
        HollowObjectSchema schema = MovieSchema();
        RecordingListener listener = new();

        using HollowConsumer consumer = Consumer(PublishThreeCycles(schema));

        consumer.TriggerRefreshTo(1);
        Assert.Empty(listener.Events);

        consumer.AddRefreshListener(listener);
        consumer.TriggerRefreshTo(2);
        Assert.Contains("successful 2", listener.Events);

        listener.Events.Clear();
        consumer.RemoveRefreshListener(listener);
        consumer.TriggerRefreshTo(3);
        Assert.Empty(listener.Events);
    }

    private sealed class FixedAnnouncementWatcher : IAnnouncementWatcher
    {
        private readonly List<HollowConsumer> _consumers = [];

        internal long Version { get; set; } = IAnnouncementWatcher.NoAnnouncementAvailable;

        public long GetLatestVersion() => Version;

        public void SubscribeToUpdates(HollowConsumer consumer) => _consumers.Add(consumer);

        internal void Announce(long version)
        {
            Version = version;

            foreach (HollowConsumer consumer in _consumers)
            {
                consumer.TriggerRefresh();
            }
        }
    }

    [Fact]
    public void AnAnnouncementWatcherDrivesTheConsumer()
    {
        HollowObjectSchema schema = MovieSchema();
        FixedAnnouncementWatcher watcher = new();

        using HollowConsumer consumer = new HollowConsumerBuilder()
            .WithBlobRetriever(PublishThreeCycles(schema))
            .WithAnnouncementWatcher(watcher)
            .Build();

        watcher.Announce(2);
        Assert.Equal(2, consumer.CurrentVersionId);

        watcher.Announce(3);
        Assert.Equal(3, consumer.CurrentVersionId);
        Assert.Equal(["one", "two", "three"], Titles(consumer));

        // The watcher owns the version, so the caller cannot also pick one.
        Assert.Throws<NotSupportedException>(() => consumer.TriggerRefreshTo(1));
    }

    /// <summary>
    /// With nothing announced, a consumer sits on an empty state rather than failing — and the next
    /// refresh starts from a snapshot however the double-snapshot config is set.
    /// </summary>
    [Fact]
    public void AConsumerWithNothingAnnouncedInitialisesToAnEmptyState()
    {
        HollowObjectSchema schema = MovieSchema();
        FixedAnnouncementWatcher watcher = new();

        using HollowConsumer consumer = new HollowConsumerBuilder()
            .WithBlobRetriever(PublishThreeCycles(schema))
            .WithAnnouncementWatcher(watcher)
            .WithDoubleSnapshotConfig(DoubleSnapshotConfig.DeltasOnly)
            .Build();

        consumer.TriggerRefresh();

        Assert.NotNull(consumer.StateEngine);
        Assert.Empty(consumer.StateEngine.TypeStates);
        Assert.Equal(HollowConstants.VersionNone, consumer.CurrentVersionId);

        watcher.Announce(2);

        Assert.Equal(2, consumer.CurrentVersionId);
        Assert.Equal(["one", "two"], Titles(consumer));
    }

    [Fact]
    public async Task AsyncRefreshReachesTheSameState()
    {
        HollowObjectSchema schema = MovieSchema();
        using HollowConsumer consumer = Consumer(PublishThreeCycles(schema));

        await consumer.TriggerRefreshAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, consumer.CurrentVersionId);
    }

    /// <summary>
    /// The whole point of the restore work, seen from the consumer's side: a producer restart does not
    /// cost the consumer its state engine.
    /// </summary>
    [Fact]
    public void AProducerRestartDoesNotDisturbTheConsumer()
    {
        HollowObjectSchema schema = MovieSchema();
        InMemoryBlobStore blobStore = new();
        HollowWriteStateEngine producer = HollowWriteStateCreator.CreateWithSchemas([schema]);

        AddMovie(producer, schema, 1, "one");
        AddMovie(producer, schema, 2, "two");
        blobStore.Publish(producer, 1);

        using HollowConsumer consumer = Consumer(blobStore);
        consumer.TriggerRefreshTo(1);

        HollowReadStateEngine stateEngine = consumer.StateEngine!;

        // The producer restarts, rebuilds its write state from what the consumer already has, and
        // carries on. Everything from here is a delta.
        HollowReadStateEngine published = new();
        using (Stream snapshot = blobStore.RetrieveSnapshotBlob(1)!.OpenStream())
        {
            new HollowBlobReader(published).ReadSnapshot(snapshot);
        }

        HollowWriteStateEngine restarted = HollowWriteStateCreator.CreateWithSchemas([schema]);
        restarted.RestoreFrom(published);

        AddMovie(restarted, schema, 1, "one");
        AddMovie(restarted, schema, 2, "two");
        AddMovie(restarted, schema, 3, "three");
        blobStore.Publish(restarted, 2);

        consumer.TriggerRefreshTo(2);

        Assert.Same(stateEngine, consumer.StateEngine);
        Assert.Equal(2, consumer.CurrentVersionId);
        Assert.Equal(["one", "two", "three"], Titles(consumer));
    }

    /// <summary>
    /// A watcher that hands over the producer's announcement metadata along with the version.
    /// </summary>
    private sealed class MetadataAnnouncementWatcher(InMemoryBlobStore blobStore) : IAnnouncementWatcher
    {
        public long GetLatestVersion() => blobStore.LatestVersion;

        public void SubscribeToUpdates(HollowConsumer consumer)
        {
        }

        public VersionInfo GetLatestVersionInfo() =>
            new(
                blobStore.LatestVersion,
                new Dictionary<string, string>(blobStore.LatestAnnouncementMetadata, StringComparer.Ordinal),
                isPinned: null,
                wasAnnounced: true);
    }

    /// <summary>
    /// A delta carries records but not schemas, so a consumer following deltas keeps the data model it
    /// first loaded. Comparing the producer's published schema hash against its own is how it notices
    /// and takes the snapshot it needs.
    /// </summary>
    [Fact]
    public void AChangedDataModelForcesASnapshotWhenConfiguredTo()
    {
        HollowObjectSchema narrowSchema = MovieSchema();

        HollowObjectSchema widerSchema = new("Movie", 3);
        widerSchema.AddField("id", FieldType.Int);
        widerSchema.AddField("title", FieldType.String);
        widerSchema.AddField("year", FieldType.Int);

        InMemoryBlobStore blobStore = new();
        HollowWriteStateEngine producer = HollowWriteStateCreator.CreateWithSchemas([narrowSchema]);
        producer.AddHeaderTag(HollowHeaderTags.SchemaHash, new HollowSchemaHash(producer).Hash);

        AddMovie(producer, narrowSchema, 1, "one");
        blobStore.Publish(producer, 1);

        // The producer's data model gains a field, so it starts a new chain with the wider schema.
        HollowWriteStateEngine widerProducer = HollowWriteStateCreator.CreateWithSchemas([widerSchema]);
        widerProducer.AddHeaderTag(HollowHeaderTags.SchemaHash, new HollowSchemaHash(widerProducer).Hash);

        HollowObjectWriteRecord record = new(widerSchema);
        record.SetInt("id", 1);
        record.SetString("title", "one");
        record.SetInt("year", 1999);
        widerProducer.Add("Movie", record);
        blobStore.Publish(widerProducer, 2, withDeltas: false);

        MetadataAnnouncementWatcher watcher = new(blobStore);

        using HollowConsumer consumer = new HollowConsumerBuilder()
            .WithBlobRetriever(blobStore)
            .WithAnnouncementWatcher(watcher)
            .WithDoubleSnapshotConfig(new DoubleSnapshotConfig { DoubleSnapshotOnSchemaChange = true })
            .Build();

        consumer.TriggerRefresh();

        Assert.Equal(2, consumer.CurrentVersionId);
        Assert.NotEqual(-1, consumer.StateEngine!.GetTypeState("Movie")!.Schema is HollowObjectSchema movieSchema
            ? movieSchema.GetPosition("year")
            : -1);
    }

    [Fact]
    public void TheSchemaHashDoesNotDependOnDeclarationOrder()
    {
        HollowObjectSchema movie = MovieSchema();

        HollowObjectSchema actor = new("Actor", 1);
        actor.AddField("name", FieldType.String);

        Assert.Equal(
            new HollowSchemaHash([movie, actor]).Hash,
            new HollowSchemaHash([actor, movie]).Hash);

        HollowObjectSchema differentMovie = new("Movie", 1);
        differentMovie.AddField("id", FieldType.Int);

        Assert.NotEqual(
            new HollowSchemaHash([movie]).Hash,
            new HollowSchemaHash([differentMovie]).Hash);
    }

    /// <summary>
    /// A read taken under the refresh lock sees one version throughout, even while something else is
    /// asking for a refresh.
    /// </summary>
    [Fact]
    public void TheRefreshLockHoldsOffARefresh()
    {
        HollowObjectSchema schema = MovieSchema();
        using HollowConsumer consumer = Consumer(PublishThreeCycles(schema));

        consumer.TriggerRefreshTo(1);

        using ManualResetEventSlim refreshFinished = new();

        // A plain thread rather than a task: the refresh lock belongs to the thread that took it, so
        // the test cannot let an await move it somewhere else halfway through.
        Thread refresher = new(() =>
        {
            consumer.TriggerRefreshTo(3);
            refreshFinished.Set();
        })
        {
            IsBackground = true,
        };

        using (consumer.AcquireRefreshLock())
        {
            refresher.Start();

            Assert.False(refreshFinished.Wait(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken));
            Assert.Equal(1, consumer.CurrentVersionId);
        }

        Assert.True(refreshFinished.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        refresher.Join();

        Assert.Equal(3, consumer.CurrentVersionId);
    }

    /// <summary>
    /// A retriever that hands back a blob whose stream cannot be read, from a chosen version onwards.
    /// </summary>
    private sealed class UnreadableBlobRetriever(InMemoryBlobStore inner, long failFrom) : IBlobRetriever
    {
        public Blob? RetrieveSnapshotBlob(long desiredVersion) => inner.RetrieveSnapshotBlob(desiredVersion);

        public Blob? RetrieveDeltaBlob(long currentVersion) =>
            inner.RetrieveDeltaBlob(currentVersion) is { } blob
                ? currentVersion >= failFrom ? new UnreadableBlob(blob.FromVersion, blob.ToVersion) : blob
                : null;

        public Blob? RetrieveReverseDeltaBlob(long currentVersion) => inner.RetrieveReverseDeltaBlob(currentVersion);
    }
}
