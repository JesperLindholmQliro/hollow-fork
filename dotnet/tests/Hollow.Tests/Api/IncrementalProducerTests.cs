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
using Hollow.Api.Producer.Listener;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Write.ObjectMapper;
using Producer = Hollow.Api.Producer.HollowProducer;

namespace Hollow.Tests.Api;

/// <summary>
/// A cycle that describes only what changed since the last version, rather than the whole dataset.
/// </summary>
/// <remarks>
/// <para>
/// The published result has to be indistinguishable from what a full cycle over the same data would
/// have produced — same records, same blobs, same integrity check — because that is the only reason to
/// prefer it. So most of these tests state an equivalence: run the change incrementally, and check the
/// consumer ends up where describing the whole dataset would have put it.
/// </para>
/// <para>
/// What makes that non-trivial is deletion. Removing a record has to remove what it referenced —
/// otherwise every string a deleted record held would stay in the state forever — but only where
/// nothing else still references it.
/// </para>
/// </remarks>
public class IncrementalProducerTests
{
    [HollowPrimaryKey("Id")]
    public sealed record Movie(int Id, string Title);

    /// <summary>A movie type whose title is stored inline, so it has no shared sub-record.</summary>
    [HollowPrimaryKey("Id")]
    public sealed record InlineMovie(int Id, [property: HollowInline] string Title);

    private sealed class RecordingListener : HollowProducerListener
    {
        internal List<(long Removed, long AddedOrModified)> Changes { get; } = [];

        public override void OnIncrementalPopulateComplete(
            Status status, long removedCount, long addedOrModifiedCount, long version, TimeSpan elapsed) =>
            Changes.Add((removedCount, addedOrModifiedCount));
    }

    private static IReadOnlyList<Movie> Consumed(InMemoryPublisher blobStore)
    {
        using Hollow.Api.Consumer.HollowConsumer consumer =
            new Hollow.Api.Consumer.HollowConsumerBuilder().WithBlobRetriever(blobStore).Build();
        consumer.TriggerRefreshTo(blobStore.AnnouncedVersion);

        HollowObjectTypeReadState movies =
            Assert.IsType<HollowObjectTypeReadState>(consumer.StateEngine!.GetTypeState("Movie"));
        HollowObjectTypeReadState strings =
            Assert.IsType<HollowObjectTypeReadState>(consumer.StateEngine.GetTypeState("String"));

        int idPosition = movies.Schema.GetPosition("Id");
        int titlePosition = movies.Schema.GetPosition("Title");
        int valuePosition = strings.Schema.GetPosition("value");

        return
        [
            .. movies.PopulatedOrdinals
                .EnumerateSetBits()
                .Select(ordinal => new Movie(
                    movies.ReadInt(ordinal, idPosition),
                    strings.ReadString(movies.ReadOrdinal(ordinal, titlePosition), valuePosition)!))
                .OrderBy(movie => movie.Id),
        ];
    }

    /// <summary>The number of records the announced version holds of a type.</summary>
    private static int ConsumedCount(InMemoryPublisher blobStore, string typeName)
    {
        using Hollow.Api.Consumer.HollowConsumer consumer =
            new Hollow.Api.Consumer.HollowConsumerBuilder().WithBlobRetriever(blobStore).Build();
        consumer.TriggerRefreshTo(blobStore.AnnouncedVersion);

        return consumer.StateEngine!.GetTypeState(typeName)?.PopulatedOrdinals.Cardinality() ?? 0;
    }

    private static Producer Producer(InMemoryPublisher blobStore, params IHollowProducerEventListener[] listeners)
    {
        Producer producer = new HollowProducerBuilder()
            .WithPublisher(blobStore)
            .WithAnnouncer(blobStore)
            .WithListeners(listeners)
            .Build();

        producer.InitializeDataModel(typeof(Movie));

        return producer;
    }

    [Fact]
    public void AnIncrementalCycleCarriesAcrossEverythingItDoesNotMention()
    {
        InMemoryPublisher blobStore = new();
        Producer producer = Producer(blobStore);

        producer.RunCycle(state =>
        {
            state.Add(new Movie(1, "one"));
            state.Add(new Movie(2, "two"));
            state.Add(new Movie(3, "three"));
        });

        producer.RunIncrementalCycle(state => state.AddOrModify(new Movie(2, "TWO")));

        Assert.Equal(
            [new Movie(1, "one"), new Movie(2, "TWO"), new Movie(3, "three")], Consumed(blobStore));
    }

    [Fact]
    public void AnIncrementalCycleAddsRecordsTheLastVersionDidNotHold()
    {
        InMemoryPublisher blobStore = new();
        Producer producer = Producer(blobStore);

        producer.RunCycle(state => state.Add(new Movie(1, "one")));
        producer.RunIncrementalCycle(state => state.AddOrModify(new Movie(2, "two")));

        Assert.Equal([new Movie(1, "one"), new Movie(2, "two")], Consumed(blobStore));
    }

    [Fact]
    public void AnIncrementalCycleDeletesByPrimaryKey()
    {
        InMemoryPublisher blobStore = new();
        Producer producer = Producer(blobStore);

        producer.RunCycle(state =>
        {
            state.Add(new Movie(1, "one"));
            state.Add(new Movie(2, "two"));
        });

        producer.RunIncrementalCycle(state => state.Delete(new Movie(2, "two")));

        Assert.Equal([new Movie(1, "one")], Consumed(blobStore));
    }

    /// <summary>
    /// A deletion names the record, not its contents: the title given here is not the one stored, and
    /// the record still goes.
    /// </summary>
    [Fact]
    public void ADeletionOnlyNeedsTheKeyToMatch()
    {
        InMemoryPublisher blobStore = new();
        Producer producer = Producer(blobStore);

        producer.RunCycle(state =>
        {
            state.Add(new Movie(1, "one"));
            state.Add(new Movie(2, "two"));
        });

        producer.RunIncrementalCycle(state => state.Delete(new Movie(2, "something else entirely")));
        Assert.Equal([new Movie(1, "one")], Consumed(blobStore));

        producer.RunIncrementalCycle(state => state.Delete(new RecordPrimaryKey("Movie", 1)));
        Assert.Empty(Consumed(blobStore));
    }

    [Fact]
    public void DeletingARecordThatIsNotThereDoesNothing()
    {
        InMemoryPublisher blobStore = new();
        Producer producer = Producer(blobStore);

        long first = producer.RunCycle(state => state.Add(new Movie(1, "one")));
        long second = producer.RunIncrementalCycle(state => state.Delete(new Movie(99, "never added")));

        // Nothing changed, so nothing was published and consumers stay where they were.
        Assert.Equal(first, second);
        Assert.Equal([new Movie(1, "one")], Consumed(blobStore));
    }

    [Fact]
    public void AddIfAbsentLeavesAnExistingRecordAlone()
    {
        InMemoryPublisher blobStore = new();
        Producer producer = Producer(blobStore);

        producer.RunCycle(state => state.Add(new Movie(1, "one")));

        producer.RunIncrementalCycle(state =>
        {
            state.AddIfAbsent(new Movie(1, "ONE"));
            state.AddIfAbsent(new Movie(2, "two"));
        });

        Assert.Equal([new Movie(1, "one"), new Movie(2, "two")], Consumed(blobStore));
    }

    /// <summary>
    /// Reporting the same record twice is the last word winning, not both changes applying.
    /// </summary>
    [Fact]
    public void TheLastChangeToARecordIsTheOneThatCounts()
    {
        InMemoryPublisher blobStore = new();
        Producer producer = Producer(blobStore);

        producer.RunCycle(state => state.Add(new Movie(1, "one")));

        producer.RunIncrementalCycle(state =>
        {
            state.AddOrModify(new Movie(2, "first go"));
            state.AddOrModify(new Movie(2, "second go"));
            state.Delete(new Movie(1, "one"));
            state.AddOrModify(new Movie(1, "back again"));
        });

        Assert.Equal([new Movie(1, "back again"), new Movie(2, "second go")], Consumed(blobStore));
    }

    /// <summary>
    /// Changing a record has to take its old title with it, or the strings of every version ever
    /// published would accumulate in the state.
    /// </summary>
    [Fact]
    public void ModifyingARecordDropsTheSubRecordsNothingElseNeeds()
    {
        InMemoryPublisher blobStore = new();
        Producer producer = Producer(blobStore);

        producer.RunCycle(state =>
        {
            state.Add(new Movie(1, "one"));
            state.Add(new Movie(2, "two"));
        });

        Assert.Equal(2, ConsumedCount(blobStore, "String"));

        producer.RunIncrementalCycle(state => state.AddOrModify(new Movie(1, "ONE")));

        Assert.Equal([new Movie(1, "ONE"), new Movie(2, "two")], Consumed(blobStore));
        Assert.Equal(2, ConsumedCount(blobStore, "String"));
    }

    /// <summary>
    /// But a sub-record another record still shares has to stay, which is what distinguishes this from
    /// simply deleting everything the record pointed at.
    /// </summary>
    [Fact]
    public void ASharedSubRecordSurvivesTheDeletionOfOneRecordThatUsesIt()
    {
        InMemoryPublisher blobStore = new();
        Producer producer = Producer(blobStore);

        producer.RunCycle(state =>
        {
            state.Add(new Movie(1, "shared"));
            state.Add(new Movie(2, "shared"));
            state.Add(new Movie(3, "its own"));
        });

        // The two movies sharing a title share the String record behind it.
        Assert.Equal(2, ConsumedCount(blobStore, "String"));

        producer.RunIncrementalCycle(state => state.Delete(new Movie(1, "shared")));

        Assert.Equal([new Movie(2, "shared"), new Movie(3, "its own")], Consumed(blobStore));
        Assert.Equal(2, ConsumedCount(blobStore, "String"));

        producer.RunIncrementalCycle(state => state.Delete(new Movie(3, "its own")));

        Assert.Equal([new Movie(2, "shared")], Consumed(blobStore));
        Assert.Equal(1, ConsumedCount(blobStore, "String"));
    }

    /// <summary>
    /// The whole point: an incremental cycle and a full one describing the same data publish the same
    /// thing, record for record.
    /// </summary>
    [Fact]
    public void AnIncrementalCycleReachesTheSameStateAsAFullOne()
    {
        InMemoryPublisher incrementalStore = new();
        Producer incremental = Producer(incrementalStore);

        InMemoryPublisher fullStore = new();
        Producer full = Producer(fullStore);

        Movie[] firstCycle =
            [new Movie(1, "one"), new Movie(2, "two"), new Movie(3, "three"), new Movie(4, "four")];

        foreach (Producer producer in new[] { incremental, full })
        {
            producer.RunCycle(state =>
            {
                foreach (Movie movie in firstCycle)
                {
                    state.Add(movie);
                }
            });
        }

        incremental.RunIncrementalCycle(state =>
        {
            state.Delete(new Movie(2, "two"));
            state.AddOrModify(new Movie(3, "THREE"));
            state.AddOrModify(new Movie(5, "five"));
        });

        full.RunCycle(state =>
        {
            state.Add(new Movie(1, "one"));
            state.Add(new Movie(3, "THREE"));
            state.Add(new Movie(4, "four"));
            state.Add(new Movie(5, "five"));
        });

        Assert.Equal(Consumed(fullStore), Consumed(incrementalStore));
        Assert.Equal(
            ConsumedCount(fullStore, "String"), ConsumedCount(incrementalStore, "String"));
    }

    [Fact]
    public void AnIncrementalCycleCanStartADeltaChain()
    {
        InMemoryPublisher blobStore = new();
        Producer producer = Producer(blobStore);

        producer.RunIncrementalCycle(state =>
        {
            state.AddOrModify(new Movie(1, "one"));

            // There is no previous version, so these have nothing to act on.
            state.AddIfAbsent(new Movie(2, "two"));
            state.Delete(new Movie(3, "three"));
        });

        Assert.Equal([new Movie(1, "one"), new Movie(2, "two")], Consumed(blobStore));
    }

    [Fact]
    public void AnIncrementalCycleThatChangesNothingPublishesNothing()
    {
        InMemoryPublisher blobStore = new();
        Producer producer = Producer(blobStore);

        long first = producer.RunCycle(state => state.Add(new Movie(1, "one")));
        int publishedAfterFirst = blobStore.PublishedSnapshotCount;

        long second = producer.RunIncrementalCycle(state => state.AddOrModify(new Movie(1, "one")));

        Assert.Equal(first, second);
        Assert.Equal(publishedAfterFirst, blobStore.PublishedSnapshotCount);
        Assert.Equal(0, blobStore.PublishedDeltaCount);
    }

    [Fact]
    public void TheListenerIsToldHowMuchChanged()
    {
        InMemoryPublisher blobStore = new();
        RecordingListener listener = new();
        Producer producer = Producer(blobStore, listener);

        producer.RunCycle(state =>
        {
            state.Add(new Movie(1, "one"));
            state.Add(new Movie(2, "two"));
        });

        // A full cycle has no incremental sub-stage at all.
        Assert.Empty(listener.Changes);

        producer.RunIncrementalCycle(state =>
        {
            state.Delete(new Movie(1, "one"));
            state.AddOrModify(new Movie(2, "TWO"));
            state.AddOrModify(new Movie(3, "three"));
        });

        Assert.Equal([(1L, 2L)], listener.Changes);
    }

    [Fact]
    public void AnIncrementalWriteStateIsUnusableOnceTheStageHasFinished()
    {
        InMemoryPublisher blobStore = new();
        Producer producer = Producer(blobStore);

        IIncrementalWriteState? escaped = null;
        producer.RunIncrementalCycle(state =>
        {
            escaped = state;
            state.AddOrModify(new Movie(1, "one"));
        });

        Assert.Throws<InvalidOperationException>(() => escaped!.AddOrModify(new Movie(2, "two")));
    }

    [Fact]
    public void AFailingIncrementalPopulatorLeavesThePreviousVersionAnnounced()
    {
        InMemoryPublisher blobStore = new();
        Producer producer = Producer(blobStore);

        long first = producer.RunCycle(state => state.Add(new Movie(1, "one")));

        Assert.Throws<InvalidOperationException>(() => producer.RunIncrementalCycle(state =>
        {
            state.AddOrModify(new Movie(2, "two"));
            throw new InvalidOperationException("the change feed fell over");
        }));

        Assert.Equal(first, blobStore.AnnouncedVersion);
        Assert.Equal([new Movie(1, "one")], Consumed(blobStore));

        // And the producer carries on from the version that was announced, not from the failed attempt.
        producer.RunIncrementalCycle(state => state.AddOrModify(new Movie(2, "two")));

        Assert.Equal([new Movie(1, "one"), new Movie(2, "two")], Consumed(blobStore));
    }

    [Fact]
    public void ARecordWithoutAPrimaryKeyCannotTakePartInAnIncrementalCycle()
    {
        InMemoryPublisher blobStore = new();
        Producer producer = Producer(blobStore);

        Assert.Throws<ArgumentException>(
            () => producer.RunIncrementalCycle(state => state.AddOrModify(new KeylessMovie(1, "one"))));
    }

    /// <summary>
    /// An inline title is part of the movie record rather than a record of its own, so deleting a movie
    /// takes it with it and there is nothing to share.
    /// </summary>
    [Fact]
    public void ATypeWithNoSubRecordsNeedsNoTraversalToDeleteCleanly()
    {
        InMemoryPublisher blobStore = new();
        Producer producer = new HollowProducerBuilder()
            .WithPublisher(blobStore)
            .WithAnnouncer(blobStore)
            .Build();

        producer.InitializeDataModel(typeof(InlineMovie));

        producer.RunCycle(state =>
        {
            state.Add(new InlineMovie(1, "one"));
            state.Add(new InlineMovie(2, "two"));
        });

        producer.RunIncrementalCycle(state => state.Delete(new InlineMovie(1, "one")));

        Assert.Equal(1, ConsumedCount(blobStore, "InlineMovie"));
        Assert.Equal(0, ConsumedCount(blobStore, "String"));
    }

    public sealed record KeylessMovie(int Id, string Title);
}
