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
using Hollow.Api.Producer;
using Hollow.Api.Producer.Enforcer;
using Hollow.Api.Producer.Fs;
using Hollow.Api.Producer.Listener;
using Hollow.Api.Producer.Validation;
using Hollow.Core;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Write.ObjectMapper;
using ConsumerBlob = Hollow.Api.Consumer.Blob;
using ConsumerHeaderBlob = Hollow.Api.Consumer.HeaderBlob;
using ProducerBlob = Hollow.Api.Producer.Blob;
using ProducerHeaderBlob = Hollow.Api.Producer.HeaderBlob;
using BlobWriter = Hollow.Core.Write.HollowBlobWriter;
using ReadStateEngine = Hollow.Core.Read.Engine.HollowReadStateEngine;

namespace Hollow.Tests.Api;

/// <summary>
/// A producer publishes a dataset one version at a time, as a delta chain consumers can follow.
/// </summary>
/// <remarks>
/// What distinguishes the producer from writing blobs by hand is that it refuses to announce a version
/// it cannot vouch for: it reads its own blobs back, checks that the snapshot and the two deltas agree,
/// and runs whatever validators are registered. These tests are mostly about what happens when one of
/// those refusals fires.
/// </remarks>
public class ProducerTests
{
    [HollowPrimaryKey("Id")]
    public sealed record Movie(int Id, string Title);

    /// <summary>
    /// A blob store that is both an <see cref="IPublisher"/> and an <see cref="IBlobRetriever"/>, so a
    /// producer and a consumer can be pointed at the same thing.
    /// </summary>
    private sealed class InMemoryPublisher : IPublisher, IBlobRetriever, IAnnouncer
    {
        private readonly Dictionary<long, PublishedBlob> _snapshots = [];
        private readonly Dictionary<long, PublishedBlob> _deltas = [];
        private readonly Dictionary<long, PublishedBlob> _reverseDeltas = [];
        private readonly Dictionary<long, PublishedHeaderBlob> _headers = [];

        internal long AnnouncedVersion { get; private set; } = HollowConstants.VersionNone;

        internal IReadOnlyDictionary<string, string> AnnouncedMetadata { get; private set; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        internal int PublishedSnapshotCount => _snapshots.Count;

        internal int PublishedDeltaCount => _deltas.Count;

        internal int PublishedHeaderCount => _headers.Count;

        public void Publish(IPublishArtifact publishArtifact)
        {
            switch (publishArtifact)
            {
                case ProducerHeaderBlob header:
                    _headers[header.Version] = new PublishedHeaderBlob(ReadAll(header), header.Version);
                    break;

                case ProducerBlob { BlobType: BlobType.Snapshot } snapshot:
                    _snapshots[snapshot.ToVersion] = new PublishedBlob(ReadAll(snapshot), snapshot.ToVersion);
                    break;

                case ProducerBlob { BlobType: BlobType.Delta } delta:
                    _deltas[delta.FromVersion] =
                        new PublishedBlob(ReadAll(delta), delta.FromVersion, delta.ToVersion);
                    break;

                case ProducerBlob reverseDelta:
                    _reverseDeltas[reverseDelta.FromVersion] =
                        new PublishedBlob(ReadAll(reverseDelta), reverseDelta.FromVersion, reverseDelta.ToVersion);
                    break;
            }
        }

        public void Announce(long stateVersion, IReadOnlyDictionary<string, string> metadata)
        {
            AnnouncedVersion = stateVersion;
            AnnouncedMetadata = metadata;
        }

        public ConsumerBlob? RetrieveSnapshotBlob(long desiredVersion)
        {
            if (_snapshots.TryGetValue(desiredVersion, out PublishedBlob? exact))
            {
                return exact;
            }

            long nearest = _snapshots.Keys
                .Where(version => version < desiredVersion)
                .DefaultIfEmpty(HollowConstants.VersionNone)
                .Max();

            return nearest == HollowConstants.VersionNone ? null : _snapshots[nearest];
        }

        public ConsumerBlob? RetrieveDeltaBlob(long currentVersion) => _deltas.GetValueOrDefault(currentVersion);

        public ConsumerBlob? RetrieveReverseDeltaBlob(long currentVersion) =>
            _reverseDeltas.GetValueOrDefault(currentVersion);

        public ConsumerHeaderBlob? RetrieveHeaderBlob(long currentVersion) =>
            _headers.GetValueOrDefault(currentVersion);

        private static byte[] ReadAll(IPublishArtifact artifact)
        {
            using Stream stream = artifact.OpenStream();
            using MemoryStream buffer = new();
            stream.CopyTo(buffer);

            return buffer.ToArray();
        }

        private sealed class PublishedBlob : ConsumerBlob
        {
            private readonly byte[] _bytes;

            internal PublishedBlob(byte[] bytes, long toVersion)
                : base(toVersion) => _bytes = bytes;

            internal PublishedBlob(byte[] bytes, long fromVersion, long toVersion)
                : base(fromVersion, toVersion) => _bytes = bytes;

            public override Stream OpenStream() => new MemoryStream(_bytes, writable: false);
        }

        private sealed class PublishedHeaderBlob(byte[] bytes, long version) : ConsumerHeaderBlob(version)
        {
            public override Stream OpenStream() => new MemoryStream(bytes, writable: false);
        }
    }

    /// <summary>Mints 1, 2, 3… so a test can name the versions it expects.</summary>
    private sealed class CountingVersionMinter : IVersionMinter
    {
        private long _version;

        public long Mint() => ++_version;
    }

    private static HollowProducer Producer(
        InMemoryPublisher blobStore, Action<HollowProducerBuilder>? configure = null)
    {
        HollowProducerBuilder builder = new HollowProducerBuilder()
            .WithPublisher(blobStore)
            .WithAnnouncer(blobStore)
            .WithVersionMinter(new CountingVersionMinter());

        configure?.Invoke(builder);

        HollowProducer producer = builder.Build();
        producer.InitializeDataModel(typeof(Movie));

        return producer;
    }

    private static Populator Movies(params Movie[] movies) =>
        state =>
        {
            foreach (Movie movie in movies)
            {
                state.Add(movie);
            }
        };

    /// <summary>The titles a consumer of <paramref name="blobStore"/> sees at the announced version.</summary>
    private static IReadOnlyList<string> ConsumedTitles(InMemoryPublisher blobStore)
    {
        using HollowConsumer consumer = new HollowConsumerBuilder().WithBlobRetriever(blobStore).Build();
        consumer.TriggerRefreshTo(blobStore.AnnouncedVersion);

        HollowObjectTypeReadState state =
            Assert.IsType<HollowObjectTypeReadState>(consumer.StateEngine!.GetTypeState("Movie"));

        // The object mapper maps a string as a reference to a shared String type rather than inlining
        // it, so reading a title means following the reference.
        HollowObjectTypeReadState strings =
            Assert.IsType<HollowObjectTypeReadState>(consumer.StateEngine.GetTypeState("String"));

        int titlePosition = state.Schema.GetPosition("Title");
        int valuePosition = strings.Schema.GetPosition("value");

        return
        [
            .. Enumerable.Range(0, state.MaxOrdinal + 1)
                .Where(ordinal => state.PopulatedOrdinals.Get(ordinal))
                .Select(ordinal => strings.ReadString(state.ReadOrdinal(ordinal, titlePosition), valuePosition)!)
        ];
    }

    [Fact]
    public void ACycleWritesASnapshotAndAnnouncesIt()
    {
        InMemoryPublisher blobStore = new();
        HollowProducer producer = Producer(blobStore);

        long version = producer.RunCycle(Movies(new Movie(1, "one")));

        Assert.Equal(1, version);
        Assert.Equal(1, blobStore.AnnouncedVersion);
        Assert.Equal(1, blobStore.PublishedSnapshotCount);
        Assert.Equal(1, blobStore.PublishedHeaderCount);
        Assert.Equal(0, blobStore.PublishedDeltaCount);
        Assert.Equal(["one"], ConsumedTitles(blobStore));
    }

    [Fact]
    public void LaterCyclesPublishDeltas()
    {
        InMemoryPublisher blobStore = new();
        HollowProducer producer = Producer(blobStore);

        producer.RunCycle(Movies(new Movie(1, "one")));
        long second = producer.RunCycle(Movies(new Movie(1, "one"), new Movie(2, "two")));

        Assert.Equal(2, second);
        Assert.Equal(1, blobStore.PublishedDeltaCount);
        Assert.Equal(["one", "two"], ConsumedTitles(blobStore));

        // The consumer followed the delta rather than reloading, which is the whole point.
        using HollowConsumer consumer = new HollowConsumerBuilder().WithBlobRetriever(blobStore).Build();
        consumer.TriggerRefreshTo(1);
        ReadStateEngine afterFirst = consumer.StateEngine!;
        consumer.TriggerRefreshTo(2);

        Assert.Same(afterFirst, consumer.StateEngine);
    }

    /// <summary>
    /// A cycle whose data is unchanged publishes nothing and reports the version consumers are already
    /// on, so a producer polling a slow-moving source does not push a version per poll.
    /// </summary>
    [Fact]
    public void ACycleWithNothingNewPublishesNothing()
    {
        InMemoryPublisher blobStore = new();
        HollowProducer producer = Producer(blobStore);

        Populator sameEveryTime = Movies(new Movie(1, "one"));

        long first = producer.RunCycle(sameEveryTime);
        long second = producer.RunCycle(sameEveryTime);

        Assert.Equal(first, second);
        Assert.Equal(1, blobStore.PublishedSnapshotCount);
        Assert.Equal(0, blobStore.PublishedDeltaCount);
        Assert.Equal(first, blobStore.AnnouncedVersion);
    }

    /// <summary>
    /// The cycle that follows an empty one still produces a delta from the last published version, not
    /// from the version the empty cycle would have had.
    /// </summary>
    [Fact]
    public void ACycleAfterAnEmptyOneStillProducesADelta()
    {
        InMemoryPublisher blobStore = new();
        HollowProducer producer = Producer(blobStore);

        producer.RunCycle(Movies(new Movie(1, "one")));
        producer.RunCycle(Movies(new Movie(1, "one")));

        long third = producer.RunCycle(Movies(new Movie(1, "one"), new Movie(2, "two")));

        Assert.Equal(3, third);
        Assert.Equal(1, blobStore.PublishedDeltaCount);
        Assert.Equal(["one", "two"], ConsumedTitles(blobStore));
    }

    [Fact]
    public void AProducerCanRestoreAndContinueTheChain()
    {
        InMemoryPublisher blobStore = new();
        HollowProducer first = Producer(blobStore);

        first.RunCycle(Movies(new Movie(1, "one"), new Movie(2, "two")));

        // The producer restarts: a new one, pointed at the same blob store, picks up where the old one
        // left off.
        HollowProducer restarted = Producer(
            blobStore, builder => builder.WithVersionMinter(new FixedVersionMinter(7)));

        IReadState? restored = restarted.Restore(blobStore.AnnouncedVersion, blobStore);

        Assert.NotNull(restored);
        Assert.Equal(1, restored.Version);

        restarted.RunCycle(Movies(new Movie(1, "one"), new Movie(2, "two"), new Movie(3, "three")));

        // A delta from the published version, not a new chain — so a consumer on version 1 can follow.
        Assert.NotNull(blobStore.RetrieveDeltaBlob(1));
        Assert.Equal(7, blobStore.AnnouncedVersion);

        using HollowConsumer consumer = new HollowConsumerBuilder().WithBlobRetriever(blobStore).Build();
        consumer.TriggerRefreshTo(1);
        ReadStateEngine afterFirst = consumer.StateEngine!;
        consumer.TriggerRefreshTo(7);

        Assert.Same(afterFirst, consumer.StateEngine);
        Assert.Equal(["one", "two", "three"], ConsumedTitles(blobStore));
    }

    private sealed class FixedVersionMinter(long version) : IVersionMinter
    {
        public long Mint() => version;
    }

    [Fact]
    public void RestoringBeforeTheDataModelIsRegisteredIsRejected()
    {
        InMemoryPublisher blobStore = new();
        Producer(blobStore).RunCycle(Movies(new Movie(1, "one")));

        HollowProducer bare = new HollowProducerBuilder().WithPublisher(blobStore).Build();

        Assert.Throws<InvalidOperationException>(() => bare.Restore(1, blobStore));
    }

    private sealed class FailingValidator : IValidatorListener
    {
        public string Name => nameof(FailingValidator);

        public ValidationResult OnValidate(IReadState readState) =>
            ValidationResult.From(this).Detail("why", "because").Failed("this data is not acceptable");
    }

    /// <summary>
    /// A rejected cycle is never announced, and the producer stays able to publish the next one against
    /// the version consumers are actually on.
    /// </summary>
    [Fact]
    public void AFailedValidationStopsTheAnnouncement()
    {
        InMemoryPublisher blobStore = new();
        HollowProducer producer = Producer(blobStore);

        producer.RunCycle(Movies(new Movie(1, "one")));

        FailingValidator validator = new();
        producer.AddListener(validator);

        ValidationStatusException e = Assert.Throws<ValidationStatusException>(
            () => producer.RunCycle(Movies(new Movie(1, "one"), new Movie(2, "two"))));

        Assert.Contains(e.ValidationStatus.Results, result => !result.IsPassed);
        Assert.Equal("this data is not acceptable", e.ValidationStatus.Results[0].Message);
        Assert.Equal("because", e.ValidationStatus.Results[0].Details["why"]);

        // Consumers were never told about version 2.
        Assert.Equal(1, blobStore.AnnouncedVersion);
        Assert.Equal(["one"], ConsumedTitles(blobStore));

        // The producer picks up again from the version consumers are on, not from the rejected one, so
        // the next delta applies to what they actually hold.
        producer.RemoveListener(validator);
        producer.RunCycle(Movies(new Movie(1, "one"), new Movie(3, "three")));

        Assert.Equal(3, blobStore.AnnouncedVersion);
        Assert.NotNull(blobStore.RetrieveDeltaBlob(1));
        Assert.Equal(["one", "three"], ConsumedTitles(blobStore));
    }

    private sealed class ThrowingValidator : IValidatorListener
    {
        public string Name => nameof(ThrowingValidator);

        public ValidationResult OnValidate(IReadState readState) =>
            throw new InvalidOperationException("this validator is broken");
    }

    /// <summary>
    /// A validator that throws cannot vouch for the data, so it counts as a failure rather than being
    /// ignored — but the other validators still run, so one report says everything that is wrong.
    /// </summary>
    [Fact]
    public void AValidatorThatThrowsFailsTheCycleAndTheOthersStillRun()
    {
        InMemoryPublisher blobStore = new();
        HollowProducer producer = Producer(
            blobStore, builder => builder.WithValidators(new ThrowingValidator(), new FailingValidator()));

        ValidationStatusException e =
            Assert.Throws<ValidationStatusException>(() => producer.RunCycle(Movies(new Movie(1, "one"))));

        Assert.Equal(2, e.ValidationStatus.Results.Count);
        Assert.Equal(ValidationResultType.Error, e.ValidationStatus.Results[0].ResultType);
        Assert.IsType<InvalidOperationException>(e.ValidationStatus.Results[0].Exception);
        Assert.Equal(ValidationResultType.Failed, e.ValidationStatus.Results[1].ResultType);

        Assert.Equal(HollowConstants.VersionNone, blobStore.AnnouncedVersion);
    }

    [Fact]
    public void TheDuplicateKeyValidatorCatchesTwoRecordsSharingAKey()
    {
        InMemoryPublisher blobStore = new();
        HollowProducer producer = Producer(
            blobStore, builder => builder.WithValidators(new DuplicateDataDetectionValidator("Movie")));

        // Same id, different title, so the two records do not deduplicate into one.
        ValidationStatusException e = Assert.Throws<ValidationStatusException>(
            () => producer.RunCycle(Movies(new Movie(1, "one"), new Movie(1, "uno"))));

        Assert.Contains("Duplicate keys found for type Movie", e.ValidationStatus.Results[0].Message!, StringComparison.Ordinal);
        Assert.Equal(HollowConstants.VersionNone, blobStore.AnnouncedVersion);

        // Without the duplicate the same validator passes.
        producer.RunCycle(Movies(new Movie(1, "one"), new Movie(2, "two")));
        Assert.Equal(2, blobStore.AnnouncedVersion);
    }

    [Fact]
    public void TheRecordCountValidatorCatchesASuddenDrop()
    {
        InMemoryPublisher blobStore = new();
        HollowProducer producer = Producer(
            blobStore, builder => builder.WithValidators(new RecordCountVarianceValidator("Movie", 25f)));

        producer.RunCycle(Movies([.. Enumerable.Range(1, 100).Select(i => new Movie(i, $"movie {i}"))]));
        Assert.Equal(1, blobStore.AnnouncedVersion);

        // 10% fewer is within the threshold.
        producer.RunCycle(Movies([.. Enumerable.Range(1, 90).Select(i => new Movie(i, $"movie {i}"))]));
        Assert.Equal(2, blobStore.AnnouncedVersion);

        // Half the data disappearing is what the validator is for.
        Assert.Throws<ValidationStatusException>(
            () => producer.RunCycle(Movies([.. Enumerable.Range(1, 45).Select(i => new Movie(i, $"movie {i}"))])));

        Assert.Equal(2, blobStore.AnnouncedVersion);
    }

    private sealed class RecordingListener : HollowProducerListener
    {
        internal List<string> Events { get; } = [];

        public override void OnNewDeltaChain(long version) => Events.Add("newDeltaChain");

        public override void OnCycleStart(long version) => Events.Add("cycleStart");

        public override void OnPopulateStart(long version) => Events.Add("populateStart");

        public override void OnPopulateComplete(Status status, long version, TimeSpan elapsed) =>
            Events.Add($"populateComplete {status.Type}");

        public override void OnPublishStart(long version) => Events.Add("publishStart");

        public override void OnBlobStage(Status status, ProducerBlob blob, TimeSpan elapsed) =>
            Events.Add($"blobStage {blob.BlobType}");

        public override void OnBlobPublish(Status status, ProducerBlob blob, TimeSpan elapsed) =>
            Events.Add($"blobPublish {blob.BlobType}");

        public override void OnPublishComplete(Status status, long version, TimeSpan elapsed) =>
            Events.Add($"publishComplete {status.Type}");

        public override void OnIntegrityCheckStart(long version) => Events.Add("integrityCheckStart");

        public override void OnIntegrityCheckComplete(
            Status status, IReadState? readState, long version, TimeSpan elapsed) =>
            Events.Add($"integrityCheckComplete {status.Type}");

        public override void OnValidationStatusStart(long version) => Events.Add("validationStart");

        public override void OnValidationStatusComplete(ValidationStatus status, long version, TimeSpan elapsed) =>
            Events.Add($"validationComplete passed={status.Passed}");

        public override void OnAnnouncementStart(long version) => Events.Add("announcementStart");

        public override void OnAnnouncementComplete(
            Status status, IReadState? readState, long version, TimeSpan elapsed) =>
            Events.Add($"announcementComplete {status.Type}");

        public override void OnCycleComplete(Status status, IReadState? readState, long version, TimeSpan elapsed) =>
            Events.Add($"cycleComplete {status.Type}");

        public override void OnNoDeltaAvailable(long version) => Events.Add("noDelta");
    }

    [Fact]
    public void AListenerSeesEveryStageInOrder()
    {
        InMemoryPublisher blobStore = new();
        RecordingListener listener = new();
        HollowProducer producer = Producer(blobStore, builder => builder.WithListeners(listener));

        producer.RunCycle(Movies(new Movie(1, "one")));

        Assert.Equal(
            [
                "newDeltaChain",
                "cycleStart",
                "populateStart",
                "populateComplete Success",
                "publishStart",
                "blobStage Snapshot",
                "blobPublish Snapshot",
                "publishComplete Success",
                "integrityCheckStart",
                "integrityCheckComplete Success",
                "validationStart",
                "validationComplete passed=True",
                "announcementStart",
                "announcementComplete Success",
                "cycleComplete Success",
            ],
            listener.Events);

        listener.Events.Clear();
        producer.RunCycle(Movies(new Movie(1, "one"), new Movie(2, "two")));

        // Second cycle: a snapshot plus both deltas, and no new delta chain.
        Assert.DoesNotContain("newDeltaChain", listener.Events);
        Assert.Contains("blobStage Delta", listener.Events);
        Assert.Contains("blobStage ReverseDelta", listener.Events);
        Assert.Contains("blobPublish Delta", listener.Events);

        listener.Events.Clear();
        producer.RunCycle(Movies(new Movie(1, "one"), new Movie(2, "two")));

        Assert.Equal(
            ["cycleStart", "populateStart", "populateComplete Success", "noDelta", "cycleComplete Success"],
            listener.Events);
    }

    private sealed class BrokenListener : HollowProducerListener
    {
        public override void OnCycleStart(long version) => throw new InvalidOperationException("broken");
    }

    /// <summary>
    /// A producer's job is to publish data, not to run other people's code, so a broken listener is
    /// reported and ignored.
    /// </summary>
    [Fact]
    public void ABrokenListenerDoesNotStopTheCycle()
    {
        InMemoryPublisher blobStore = new();
        HollowProducer producer = Producer(blobStore, builder => builder.WithListeners(new BrokenListener()));

        List<Exception> reported = [];
        producer.ListenerFailed += (_, e) => reported.Add(e);

        producer.RunCycle(Movies(new Movie(1, "one")));

        Assert.Equal(1, blobStore.AnnouncedVersion);
        Assert.Single(reported);
        Assert.IsType<InvalidOperationException>(reported[0]);
    }

    private sealed class VetoingListener : HollowProducerListener, IVetoableListener
    {
        public override void OnCycleStart(long version) =>
            throw new ListenerVetoException("this cycle must not run");
    }

    /// <summary>
    /// A listener that means it can stop the cycle, which is what the veto is for.
    /// </summary>
    [Fact]
    public void AVetoingListenerStopsTheCycle()
    {
        InMemoryPublisher blobStore = new();
        HollowProducer producer = Producer(blobStore, builder => builder.WithListeners(new VetoingListener()));

        Assert.Throws<ListenerVetoException>(() => producer.RunCycle(Movies(new Movie(1, "one"))));
        Assert.Equal(HollowConstants.VersionNone, blobStore.AnnouncedVersion);
    }

    [Fact]
    public void AProducerThatIsNotPrimaryDoesNotRunCycles()
    {
        InMemoryPublisher blobStore = new();
        BasicSingleProducerEnforcer enforcer = new();
        RecordingListener listener = new();

        HollowProducer producer = Producer(
            blobStore,
            builder => builder.WithSingleProducerEnforcer(enforcer).WithListeners(listener));

        producer.RunCycle(Movies(new Movie(1, "one")));
        Assert.Equal(1, blobStore.AnnouncedVersion);

        Assert.False(producer.EnablePrimaryProducer(false));

        listener.Events.Clear();
        long version = producer.RunCycle(Movies(new Movie(1, "one"), new Movie(2, "two")));

        Assert.Equal(1, version);
        Assert.Equal(1, blobStore.AnnouncedVersion);
        Assert.Empty(listener.Events);

        Assert.True(producer.EnablePrimaryProducer(true));
        producer.RunCycle(Movies(new Movie(1, "one"), new Movie(2, "two")));

        // A skipped cycle does not even mint a version — the check comes first — so the next one takes
        // the number the skipped cycle would have had.
        Assert.Equal(2, blobStore.AnnouncedVersion);
    }

    [Fact]
    public void TheAnnouncementCarriesTheProducerHeaderTags()
    {
        InMemoryPublisher blobStore = new();
        HollowProducer producer = Producer(blobStore);

        producer.RunCycle(Movies(new Movie(1, "one")));

        Assert.Equal("1", blobStore.AnnouncedMetadata[HollowHeaderTags.ProducerToVersion]);
        Assert.Equal("1", blobStore.AnnouncedMetadata[HollowHeaderTags.DeltaChainVersionCounter]);
        Assert.True(blobStore.AnnouncedMetadata.ContainsKey(HollowHeaderTags.SchemaHash));
        Assert.True(blobStore.AnnouncedMetadata.ContainsKey(HollowHeaderTags.MetricAnnouncement));
    }

    /// <summary>
    /// The write state a populator is given is valid only while the populate stage is running: keeping
    /// one and using it later would add records to whatever cycle happened to be running then.
    /// </summary>
    [Fact]
    public void TheWriteStateIsUnusableAfterThePopulateStage()
    {
        InMemoryPublisher blobStore = new();
        HollowProducer producer = Producer(blobStore);

        IWriteState? escaped = null;

        producer.RunCycle(state =>
        {
            escaped = state;
            state.Add(new Movie(1, "one"));
        });

        Assert.NotNull(escaped);
        Assert.Throws<InvalidOperationException>(() => escaped.Add(new Movie(2, "two")));
        Assert.Throws<InvalidOperationException>(() => escaped.StateEngine);
        Assert.Throws<InvalidOperationException>(() => escaped.Version);
    }

    [Fact]
    public void APopulatorSeesThePreviousCyclesState()
    {
        InMemoryPublisher blobStore = new();
        HollowProducer producer = Producer(blobStore);

        long? priorVersionDuringFirstCycle = null;
        long? priorVersionDuringSecondCycle = null;

        producer.RunCycle(state =>
        {
            priorVersionDuringFirstCycle = state.PriorState?.Version;
            state.Add(new Movie(1, "one"));
        });

        producer.RunCycle(state =>
        {
            priorVersionDuringSecondCycle = state.PriorState?.Version;
            state.Add(new Movie(1, "one"));
            state.Add(new Movie(2, "two"));
        });

        Assert.Null(priorVersionDuringFirstCycle);
        Assert.Equal(1, priorVersionDuringSecondCycle);
    }

    [Fact]
    public void APopulatorThatThrowsLeavesTheProducerUsable()
    {
        InMemoryPublisher blobStore = new();
        HollowProducer producer = Producer(blobStore);

        producer.RunCycle(Movies(new Movie(1, "one")));

        Assert.Throws<InvalidOperationException>(() => producer.RunCycle(state =>
        {
            state.Add(new Movie(2, "two"));
            throw new InvalidOperationException("the data source failed half way through");
        }));

        Assert.Equal(1, blobStore.AnnouncedVersion);

        // The half-populated cycle left nothing behind: the next delta is from version 1 and holds only
        // the record that really is new.
        producer.RunCycle(Movies(new Movie(1, "one"), new Movie(3, "three")));

        Assert.Equal(3, blobStore.AnnouncedVersion);
        Assert.Equal(["one", "three"], ConsumedTitles(blobStore));
    }

    /// <summary>
    /// Without the integrity check the producer publishes the same blobs, so the check is a check and
    /// not part of producing the data.
    /// </summary>
    [Fact]
    public void TurningOffTheIntegrityCheckProducesTheSameData()
    {
        InMemoryPublisher checkedStore = new();
        HollowProducer checkedProducer = Producer(checkedStore);

        InMemoryPublisher uncheckedStore = new();
        HollowProducer uncheckedProducer = Producer(
            uncheckedStore, builder => builder.WithoutIntegrityCheck());

        foreach (HollowProducer producer in (HollowProducer[])[checkedProducer, uncheckedProducer])
        {
            producer.RunCycle(Movies(new Movie(1, "one"), new Movie(2, "two")));
            producer.RunCycle(Movies(new Movie(1, "one"), new Movie(3, "three")));
        }

        Assert.Equal(ConsumedTitles(checkedStore), ConsumedTitles(uncheckedStore));
        Assert.Equal(checkedStore.AnnouncedVersion, uncheckedStore.AnnouncedVersion);
    }

    /// <summary>
    /// With the integrity check off, a producer can skip writing most snapshots — a consumer starting
    /// fresh then replays a few deltas instead.
    /// </summary>
    [Fact]
    public void SnapshotsCanBePublishedLessOften()
    {
        InMemoryPublisher blobStore = new();
        HollowProducer producer = Producer(
            blobStore, builder => builder.WithoutIntegrityCheck().WithNumStatesBetweenSnapshots(2));

        for (int i = 1; i <= 5; i++)
        {
            producer.RunCycle(Movies([.. Enumerable.Range(1, i).Select(id => new Movie(id, $"movie {id}"))]));
        }

        Assert.Equal(5, blobStore.AnnouncedVersion);
        Assert.Equal(4, blobStore.PublishedDeltaCount);

        // Fewer snapshots than cycles, and a consumer still reaches the latest version.
        Assert.True(
            blobStore.PublishedSnapshotCount < 5,
            $"expected fewer than 5 snapshots, got {blobStore.PublishedSnapshotCount}");

        Assert.Equal(["movie 1", "movie 2", "movie 3", "movie 4", "movie 5"], ConsumedTitles(blobStore));
    }

    /// <summary>
    /// A stager that re-emits an earlier cycle's snapshot, standing in for any bug that would make a
    /// cycle's blobs disagree with each other.
    /// </summary>
    private sealed class StaleSnapshotStager : IBlobStager
    {
        private readonly HollowInMemoryBlobStager _inner = new();

        private byte[]? _firstSnapshot;

        public ProducerBlob OpenSnapshot(long version) => new StaleSnapshotBlob(this, _inner.OpenSnapshot(version));

        public ProducerHeaderBlob OpenHeader(long version) => _inner.OpenHeader(version);

        public ProducerBlob OpenDelta(long fromVersion, long toVersion) => _inner.OpenDelta(fromVersion, toVersion);

        public ProducerBlob OpenReverseDelta(long fromVersion, long toVersion) =>
            _inner.OpenReverseDelta(fromVersion, toVersion);

        private sealed class StaleSnapshotBlob(StaleSnapshotStager stager, ProducerBlob inner)
            : ProducerBlob(inner.FromVersion, inner.ToVersion, inner.BlobType)
        {
            public override void Write(BlobWriter blobWriter)
            {
                inner.Write(blobWriter);

                if (stager._firstSnapshot is null)
                {
                    using Stream written = inner.OpenStream();
                    using MemoryStream buffer = new();
                    written.CopyTo(buffer);

                    stager._firstSnapshot = buffer.ToArray();
                }
            }

            public override Stream OpenStream() =>
                stager._firstSnapshot is { } stale ? new MemoryStream(stale, writable: false) : inner.OpenStream();

            public override void Cleanup() => inner.Cleanup();
        }
    }

    /// <summary>
    /// The check that earns the producer its keep: a delta is derived from the write state's ordinal
    /// bookkeeping rather than from the data, so a mistake there produces a delta that applies cleanly
    /// and leaves a consumer holding data that never existed. Comparing checksums catches it before
    /// anything is announced.
    /// </summary>
    [Fact]
    public void BlobsThatDisagreeWithEachOtherAreNeverAnnounced()
    {
        InMemoryPublisher blobStore = new();
        HollowProducer producer = Producer(
            blobStore, builder => builder.WithBlobStager(new StaleSnapshotStager()));

        producer.RunCycle(Movies(new Movie(1, "one")));
        Assert.Equal(1, blobStore.AnnouncedVersion);

        ChecksumValidationException e = Assert.Throws<ChecksumValidationException>(
            () => producer.RunCycle(Movies(new Movie(1, "one"), new Movie(2, "two"))));

        Assert.Equal(BlobType.Delta, e.BlobType);
        Assert.NotEqual(e.Expected, e.Actual);

        // Consumers stay on the version that was checked.
        Assert.Equal(1, blobStore.AnnouncedVersion);
        Assert.Equal(["one"], ConsumedTitles(blobStore));
    }

    /// <summary>
    /// A producer with no announcer publishes but tells nobody, for a deployment where something else
    /// decides when consumers move.
    /// </summary>
    [Fact]
    public void AProducerWithoutAnAnnouncerStillPublishes()
    {
        InMemoryPublisher blobStore = new();

        HollowProducer producer = new HollowProducerBuilder()
            .WithPublisher(blobStore)
            .WithVersionMinter(new CountingVersionMinter())
            .Build();

        producer.InitializeDataModel(typeof(Movie));

        long version = producer.RunCycle(Movies(new Movie(1, "one")));

        Assert.Equal(1, version);
        Assert.Equal(HollowConstants.VersionNone, blobStore.AnnouncedVersion);
        Assert.Equal(1, blobStore.PublishedSnapshotCount);
    }
}
