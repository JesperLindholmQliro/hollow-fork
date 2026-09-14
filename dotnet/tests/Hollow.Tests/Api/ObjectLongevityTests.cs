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

using Hollow.Api.Client;
using Hollow.Api.Consumer;
using Hollow.Api.Objects.Generic;
using Hollow.Core.Read.DataAccess.Disabled;
using Hollow.Core.Read.DataAccess.Proxy;
using Hollow.Core.Schema;
using Hollow.Core.Util;
using Hollow.Core.Write;

namespace Hollow.Tests.Api;

/// <summary>
/// Keeping a record readable after the state it came from has moved on.
/// </summary>
/// <remarks>
/// What is being tested is a guarantee about <em>stale</em> references, so every test here holds a
/// record across enough refreshes for its ordinal to be reused. Two are needed, not one: a removed
/// record stays readable for exactly one transition — Hollow's ghost-record guarantee — so its
/// ordinal is not free to be taken until the next.
/// </remarks>
public class ObjectLongevityTests
{
    [Fact]
    public void WithoutLongevityAHeldRecordStartsReadingWhateverTookItsOrdinal()
    {
        using HollowConsumer consumer = Consumer(Published(), longevity: null);
        consumer.TriggerRefreshTo(1);

        GenericHollowObject held = Movie(consumer, "two");

        consumer.TriggerRefreshTo(2);
        consumer.TriggerRefreshTo(3);

        // The reference is a type and an ordinal, not a copy, so it now reads the record that took
        // that ordinal rather than the one it was taken for.
        Assert.Equal("four", held.GetString("title"));
    }

    [Fact]
    public void WithLongevityAHeldRecordKeepsReadingItsOwnValues()
    {
        using HollowConsumer consumer = Consumer(Published(), ObjectLongevityConfig.Enabled);
        consumer.TriggerRefreshTo(1);

        GenericHollowObject held = Movie(consumer, "two");

        consumer.TriggerRefreshTo(2);
        consumer.TriggerRefreshTo(3);

        // The API this record came from was pointed at a historical state holding exactly the records
        // that transition removed — of which this is one.
        Assert.Equal("two", held.GetString("title"));
    }

    [Fact]
    public void ARecordObtainedAfterTheRefreshReadsTheNewData()
    {
        using HollowConsumer consumer = Consumer(Published(), ObjectLongevityConfig.Enabled);
        consumer.TriggerRefreshTo(1);

        GenericHollowObject held = Movie(consumer, "two");

        consumer.TriggerRefreshTo(2);
        consumer.TriggerRefreshTo(3);

        // Both are true at once, which is the point: the old reference is frozen, the new one is live.
        Assert.Equal("two", held.GetString("title"));
        Assert.Equal("four", Movie(consumer, "four").GetString("title"));
    }

    [Fact]
    public void ARecordSurvivesSeveralRefreshesByWalkingTheHistoricalChain()
    {
        using HollowConsumer consumer = Consumer(Published(), ObjectLongevityConfig.Enabled);
        consumer.TriggerRefreshTo(1);

        GenericHollowObject held = Movie(consumer, "one");

        consumer.TriggerRefreshTo(2);
        consumer.TriggerRefreshTo(3);

        // "one" was never removed, so no historical state holds it. Reading it walks forward along the
        // chain until it reaches the live state, which does.
        Assert.Equal("one", held.GetString("title"));
    }

    [Fact]
    public void AHeldRecordIsStillReadableInsideTheGracePeriod()
    {
        ManualTimeProvider time = new();
        StaleReferenceDetector detector = DetectorFor(out HollowConsumer consumer, time);

        using (consumer)
        {
            consumer.TriggerRefreshTo(1);

            GenericHollowObject held = Movie(consumer, "two");

            consumer.TriggerRefreshTo(2);
            consumer.TriggerRefreshTo(3);

            // Most of the way through the grace period. Nothing has been flagged or dropped.
            time.Advance(TimeSpan.FromMinutes(59));
            detector.Housekeeping();

            Assert.Equal("two", held.GetString("title"));
        }
    }

    [Fact]
    public void AnUnusedRecordIsDroppedOnceBothPeriodsHavePassed()
    {
        ManualTimeProvider time = new();
        StaleReferenceDetector detector = DetectorFor(out HollowConsumer consumer, time);

        using (consumer)
        {
            consumer.TriggerRefreshTo(1);

            GenericHollowObject held = Movie(consumer, "two");

            consumer.TriggerRefreshTo(2);
            consumer.TriggerRefreshTo(3);

            // Past the grace period: the usage detection window opens and the read flag is cleared.
            time.Advance(TimeSpan.FromMinutes(61));
            detector.Housekeeping();

            // Past the detection window, with nothing having read the record in it.
            time.Advance(TimeSpan.FromMinutes(61));
            detector.Housekeeping();

            Assert.Throws<HollowDataAccessDisabledException>(() => held.GetString("title"));
        }
    }

    [Fact]
    public void ARecordThatIsStillBeingReadIsNotDropped()
    {
        ManualTimeProvider time = new();
        StaleReferenceDetector detector = DetectorFor(out HollowConsumer consumer, time);

        using (consumer)
        {
            consumer.TriggerRefreshTo(1);

            GenericHollowObject held = Movie(consumer, "two");

            consumer.TriggerRefreshTo(2);
            consumer.TriggerRefreshTo(3);

            time.Advance(TimeSpan.FromMinutes(61));
            detector.Housekeeping();

            // A read inside the detection window is what says this reference is genuinely in use.
            Assert.Equal("two", held.GetString("title"));

            time.Advance(TimeSpan.FromMinutes(61));
            detector.Housekeeping();

            Assert.Equal("two", held.GetString("title"));
        }
    }

    [Fact]
    public void ForceDropDataDropsARecordEvenWhileItIsBeingRead()
    {
        ManualTimeProvider time = new();
        IObjectLongevityConfig config = new ObjectLongevityConfig
        {
            EnableLongLivedObjectSupport = true,
            DropDataAutomatically = true,
            ForceDropData = true,
        };

        StaleReferenceDetector detector = DetectorFor(out HollowConsumer consumer, time, config);

        using (consumer)
        {
            consumer.TriggerRefreshTo(1);

            GenericHollowObject held = Movie(consumer, "two");

            consumer.TriggerRefreshTo(2);
            consumer.TriggerRefreshTo(3);

            time.Advance(TimeSpan.FromMinutes(61));
            detector.Housekeeping();

            Assert.Equal("two", held.GetString("title"));

            // Read or not, the data goes. This is the setting for finding the code that holds a
            // reference too long rather than tolerating it.
            time.Advance(TimeSpan.FromMinutes(61));
            detector.Housekeeping();

            Assert.Throws<HollowDataAccessDisabledException>(() => held.GetString("title"));
        }
    }

    [Fact]
    public void TheDetectorIsToldWhenAStaleReferenceIsBeingRead()
    {
        ManualTimeProvider time = new();
        CountingDetector counts = new();

        StaleReferenceDetector detector =
            DetectorFor(out HollowConsumer consumer, time, ObjectLongevityConfig.Enabled, counts);

        using (consumer)
        {
            consumer.TriggerRefreshTo(1);

            GenericHollowObject held = Movie(consumer, "two");

            consumer.TriggerRefreshTo(2);
            consumer.TriggerRefreshTo(3);

            time.Advance(TimeSpan.FromMinutes(61));
            detector.Housekeeping();

            Assert.Equal("two", held.GetString("title"));

            detector.Housekeeping();

            // The usage signal is the strong one: something is not merely holding old data, it is
            // reading it.
            Assert.Contains(counts.Usage, count => count > 0);
        }
    }

    [Fact]
    public void ALongevityConsumerBuildsItsApiOverAProxy()
    {
        using HollowConsumer plain = Consumer(Published(), longevity: null);
        plain.TriggerRefreshTo(1);

        using HollowConsumer longLived = Consumer(Published(), ObjectLongevityConfig.Enabled);
        longLived.TriggerRefreshTo(1);

        Assert.IsNotType<HollowProxyDataAccess>(plain.Api!.DataAccess);
        Assert.IsType<HollowProxyDataAccess>(longLived.Api!.DataAccess);
    }

    [Fact]
    public void NoDetectorRunsWhenLongevityIsOff()
    {
        InMemoryBlobStore blobStore = Published();

        using HollowConsumer plain = Consumer(blobStore, longevity: null);
        using HollowConsumer longLived = Consumer(blobStore, ObjectLongevityConfig.Enabled);

        Assert.Null(plain.StaleReferenceDetector);
        Assert.NotNull(longLived.StaleReferenceDetector);
    }

    /// <summary>The movie record with <paramref name="title"/>, read through the consumer's API.</summary>
    /// <remarks>
    /// Through the API rather than the state engine, because the API is what holds the data access
    /// longevity swaps out. A record read straight off the state engine would not be covered by it.
    /// </remarks>
    private static GenericHollowObject Movie(HollowConsumer consumer, string title)
    {
        foreach (int ordinal in consumer.StateEngine!.GetTypeState("Movie")!.PopulatedOrdinals.EnumerateSetBits())
        {
            GenericHollowObject movie = new(consumer.Api!.DataAccess, "Movie", ordinal);

            if (movie.GetString("title") == title)
            {
                return movie;
            }
        }

        throw new InvalidOperationException($"no movie titled {title}");
    }

    /// <summary>
    /// Three cycles in which an ordinal is reused, which is what makes a stale reference visible.
    /// </summary>
    /// <remarks>
    /// "two" goes in cycle 2 and "four" arrives in cycle 3, by which point the ordinal "two" occupied
    /// has stopped being a ghost and is free to be taken. A reference to "two" held across both
    /// transitions is therefore pointing at "four" unless something stops it.
    /// </remarks>
    private static InMemoryBlobStore Published()
    {
        HollowObjectSchema schema = new("Movie", 2);
        schema.AddField("id", FieldType.Int);
        schema.AddField("title", FieldType.String);

        InMemoryBlobStore blobStore = new();
        HollowWriteStateEngine producer = HollowWriteStateCreator.CreateWithSchemas([schema]);

        Add(producer, schema, 1, "one");
        Add(producer, schema, 2, "two");
        blobStore.Publish(producer, 1);

        Add(producer, schema, 1, "one");
        Add(producer, schema, 3, "three");
        blobStore.Publish(producer, 2);

        Add(producer, schema, 1, "one");
        Add(producer, schema, 4, "four");
        blobStore.Publish(producer, 3);

        return blobStore;
    }

    private static void Add(HollowWriteStateEngine engine, HollowObjectSchema schema, int id, string title)
    {
        HollowObjectWriteRecord record = new(schema);
        record.SetInt("id", id);
        record.SetString("title", title);

        engine.Add("Movie", record);
    }

    private static HollowConsumer Consumer(InMemoryBlobStore blobStore, IObjectLongevityConfig? longevity)
    {
        HollowConsumerBuilder builder = new HollowConsumerBuilder().WithBlobRetriever(blobStore);

        if (longevity is not null)
        {
            builder.WithObjectLongevityConfig(longevity);
        }

        return builder.Build();
    }

    /// <summary>A longevity consumer whose clock this test drives, and its detector.</summary>
    private static StaleReferenceDetector DetectorFor(
        out HollowConsumer consumer,
        ManualTimeProvider time,
        IObjectLongevityConfig? config = null,
        IObjectLongevityDetector? detector = null)
    {
        consumer = new HollowConsumerBuilder()
            .WithBlobRetriever(Published())
            .WithObjectLongevityConfig(config ?? ObjectLongevityConfig.Enabled, detector, time)
            .Build();

        return consumer.StaleReferenceDetector!;
    }

    /// <summary>Records what the detector was told.</summary>
    private sealed class CountingDetector : IObjectLongevityDetector
    {
        internal List<int> Existence { get; } = [];

        internal List<int> Usage { get; } = [];

        public void StaleReferenceExistenceDetected(int count) => Existence.Add(count);

        public void StaleReferenceUsageDetected(int count) => Usage.Add(count);
    }

    /// <summary>
    /// A clock a test moves by hand, with a timer that never fires.
    /// </summary>
    /// <remarks>
    /// The timer is inert because these tests call <see cref="StaleReferenceDetector.Housekeeping"/>
    /// directly: what is being tested is what housekeeping decides, not that a timer runs.
    /// </remarks>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new InertTimer();

        internal void Advance(TimeSpan by) => _now += by;

        private sealed class InertTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
