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

using Hollow.Api.Producer;
using Hollow.Core.Write.ObjectMapper;
using BlobType = Hollow.Api.Consumer.BlobType;

namespace Hollow.Tests.Api;

/// <summary>
/// Publishing the snapshot off the cycle thread, and pruning the store afterwards.
/// </summary>
/// <remarks>
/// The snapshot is the largest artifact a cycle produces and the least urgent — consumers on the chain
/// move by delta. What matters is that moving its upload off the cycle does not let the cycle delete
/// the staged file out from under it, which is what the waiting test is for.
/// </remarks>
public class ProducerPublishOptionsTests
{
    [Fact]
    public void ASnapshotIsPublishedOnTheGivenScheduler()
    {
        RecordingScheduler scheduler = new();
        InMemoryPublisher blobStore = new();

        HollowProducer producer = Producer(blobStore, builder => builder.WithSnapshotPublishScheduler(scheduler));

        producer.RunCycle(state => state.Add(new Movie(1, "Heat")));

        Assert.Equal(1, scheduler.Count);
        Assert.Equal(1, blobStore.PublishedSnapshotCount);
    }

    [Fact]
    public void TheCycleWaitsForTheUploadBeforeItCleansUp()
    {
        // A scheduler that runs the work late is the shape of a slow upload; the cycle has to still be
        // holding its staged files when it happens.
        DeferredScheduler scheduler = new();
        InMemoryPublisher blobStore = new();

        HollowProducer producer = Producer(blobStore, builder => builder.WithSnapshotPublishScheduler(scheduler));

        producer.RunCycle(state => state.Add(new Movie(1, "Heat")));

        Assert.True(scheduler.Ran);
        Assert.Equal(1, blobStore.PublishedSnapshotCount);
    }

    [Fact]
    public void WithoutASchedulerTheSnapshotIsPublishedInline()
    {
        InMemoryPublisher blobStore = new();

        HollowProducer producer = Producer(blobStore);

        producer.RunCycle(state => state.Add(new Movie(1, "Heat")));

        Assert.Equal(1, blobStore.PublishedSnapshotCount);
    }

    [Fact]
    public void TheCleanerIsOfferedEveryBlobThatWasPublished()
    {
        RecordingCleaner cleaner = new();
        InMemoryPublisher blobStore = new();

        HollowProducer producer = Producer(blobStore, builder => builder.WithBlobStorageCleaner(cleaner));

        producer.RunCycle(state => state.Add(new Movie(1, "Heat")));

        Assert.Equal([BlobType.Snapshot], cleaner.Cleaned);

        producer.RunCycle(state =>
        {
            state.Add(new Movie(1, "Heat"));
            state.Add(new Movie(2, "Ronin"));
        });

        // A second cycle has a state to move from, so it publishes both transitions as well.
        Assert.Contains(BlobType.Delta, cleaner.Cleaned);
        Assert.Contains(BlobType.ReverseDelta, cleaner.Cleaned);
    }

    [Fact]
    public void ACleanerThatOverridesNothingRemovesNothing()
    {
        BlobStorageCleaner cleaner = BlobStorageCleaner.None;

        cleaner.Clean(BlobType.Snapshot);
        cleaner.Clean(BlobType.Delta);
        cleaner.Clean(BlobType.ReverseDelta);
    }

    private static HollowProducer Producer(
        InMemoryPublisher blobStore, Action<HollowProducerBuilder>? configure = null)
    {
        HollowProducerBuilder builder = new HollowProducerBuilder()
            .WithPublisher(blobStore)
            .WithAnnouncer(blobStore);

        configure?.Invoke(builder);

        HollowProducer producer = builder.Build();

        producer.InitializeDataModel(typeof(Movie));

        return producer;
    }

    /// <summary>A scheduler that runs the work immediately, and counts it.</summary>
    private sealed class RecordingScheduler : TaskScheduler
    {
        internal int Count { get; private set; }

        protected override IEnumerable<Task> GetScheduledTasks() => [];

        protected override void QueueTask(Task task)
        {
            Count++;

            TryExecuteTask(task);
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
    }

    /// <summary>A scheduler that holds the work until someone waits on it.</summary>
    private sealed class DeferredScheduler : TaskScheduler
    {
        private Task? _queued;

        internal bool Ran { get; private set; }

        protected override IEnumerable<Task> GetScheduledTasks() => _queued is null ? [] : [_queued];

        protected override void QueueTask(Task task) => _queued = task;

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        {
            // Which is what the cycle's wait ends up doing: it cannot finish until this runs.
            Ran = TryExecuteTask(task);

            return Ran;
        }
    }

    private sealed class RecordingCleaner : BlobStorageCleaner
    {
        internal List<BlobType> Cleaned { get; } = [];

        protected override void CleanSnapshots() => Cleaned.Add(BlobType.Snapshot);

        protected override void CleanDeltas() => Cleaned.Add(BlobType.Delta);

        protected override void CleanReverseDeltas() => Cleaned.Add(BlobType.ReverseDelta);
    }

    [HollowPrimaryKey("Id")]
    private sealed record Movie(int Id, string Title);
}
