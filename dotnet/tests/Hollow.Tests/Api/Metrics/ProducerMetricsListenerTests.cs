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
using Hollow.Api.Producer.Metrics;
using Hollow.Core;
using Hollow.Core.Read.Engine;
using Hollow.Core.Schema;
using Hollow.Core.Write;

namespace Hollow.Tests.Api.Metrics;

/// <summary>
/// Turns a producer's lifecycle events into cycle and announcement metrics.
/// </summary>
/// <remarks>
/// The listener's whole contract is "given this sequence of events, report these numbers", so the
/// tests drive the events directly rather than through a producer. What matters is the state carried
/// between events — the failure count, the last success — which a single cycle could not exercise.
/// </remarks>
public class ProducerMetricsListenerTests
{
    [Fact]
    public void ASkippedCycleReportsNeitherDurationNorOutcome()
    {
        Recorder recorder = new();

        recorder.OnCycleSkip(CycleSkipReason.NotPrimaryProducer);

        CycleMetrics metrics = Assert.Single(recorder.Cycles);

        // A producer that is not primary skips cycle after cycle, and that is healthy: a skip is
        // neither a success nor a failure, and it never ran so it took no time.
        Assert.Null(metrics.IsCycleSuccess);
        Assert.Null(metrics.CycleDuration);
        Assert.Equal(0, metrics.ConsecutiveFailures);
        Assert.Null(metrics.LastCycleSuccessTimestamp);
    }

    [Fact]
    public void AFailedCycleCountsUpAndASuccessfulOneResets()
    {
        Recorder recorder = new();

        recorder.OnCycleComplete(Failed(), null, 1, TimeSpan.FromMilliseconds(5));
        recorder.OnCycleComplete(Failed(), null, 2, TimeSpan.FromMilliseconds(5));

        Assert.Equal(2, recorder.LastCycle.ConsecutiveFailures);
        Assert.False(recorder.LastCycle.IsCycleSuccess);
        Assert.Null(recorder.LastCycle.LastCycleSuccessTimestamp);

        recorder.OnCycleComplete(Status.Success, null, 3, TimeSpan.FromMilliseconds(7));

        Assert.Equal(0, recorder.LastCycle.ConsecutiveFailures);
        Assert.True(recorder.LastCycle.IsCycleSuccess);
        Assert.Equal(TimeSpan.FromMilliseconds(7), recorder.LastCycle.CycleDuration);
        Assert.NotNull(recorder.LastCycle.LastCycleSuccessTimestamp);
    }

    [Fact]
    public void ASkippedCycleKeepsTheFailureCountAndTheLastSuccess()
    {
        Recorder recorder = new();

        recorder.OnCycleComplete(Status.Success, null, 1, TimeSpan.FromMilliseconds(5));
        recorder.OnCycleComplete(Failed(), null, 2, TimeSpan.FromMilliseconds(5));

        long? lastSuccess = recorder.LastCycle.LastCycleSuccessTimestamp;

        recorder.OnCycleSkip(CycleSkipReason.NotPrimaryProducer);

        // Leadership moving elsewhere is not the producer recovering, so the failure it had stands.
        Assert.Equal(1, recorder.LastCycle.ConsecutiveFailures);
        Assert.Equal(lastSuccess, recorder.LastCycle.LastCycleSuccessTimestamp);
        Assert.Null(recorder.LastCycle.IsCycleSuccess);
    }

    [Fact]
    public void AnAnnouncementReportsTheDataSizeAndShardLayout()
    {
        Recorder recorder = new();
        HollowReadStateEngine dataset = Dataset();

        recorder.OnAnnouncementComplete(
            Status.Success, new State(1, dataset), 1, TimeSpan.FromMilliseconds(3));

        AnnouncementMetrics metrics = Assert.Single(recorder.Announcements);

        Assert.True(metrics.IsAnnouncementSuccess);
        Assert.Equal(TimeSpan.FromMilliseconds(3), metrics.AnnouncementDuration);
        Assert.Equal(dataset.ApproxDataSize, metrics.DataSizeBytes);
        Assert.NotNull(metrics.LastAnnouncementSuccessTimestamp);

        // The shard count only means something next to how big a shard is, so both are reported.
        Assert.Equal(2, metrics.NumShardsPerType["Movie"]);
        Assert.Equal(
            dataset.GetTypeState("Movie")!.ApproxHeapFootprintInBytes / 2,
            metrics.ShardSizePerType["Movie"]);
    }

    [Fact]
    public void AFailedAnnouncementLeavesTheLastSuccessWhereItWas()
    {
        Recorder recorder = new();

        recorder.OnAnnouncementComplete(
            Status.Success, new State(1, Dataset()), 1, TimeSpan.FromMilliseconds(3));

        long? lastSuccess = recorder.LastAnnouncement.LastAnnouncementSuccessTimestamp;

        recorder.OnAnnouncementComplete(
            Failed(), new State(2, Dataset()), 2, TimeSpan.FromMilliseconds(3));

        Assert.False(recorder.LastAnnouncement.IsAnnouncementSuccess);
        Assert.Equal(lastSuccess, recorder.LastAnnouncement.LastAnnouncementSuccessTimestamp);
    }

    [Fact]
    public void AnAnnouncementWithoutAStateReportsNoSizes()
    {
        Recorder recorder = new();

        // A failure can arrive before there is a state to measure, and a metrics listener that threw
        // here would fail the producer's cycle over a metric.
        recorder.OnAnnouncementComplete(Failed(), null, 1, TimeSpan.FromMilliseconds(3));

        Assert.Equal(0, recorder.LastAnnouncement.DataSizeBytes);
        Assert.Empty(recorder.LastAnnouncement.NumShardsPerType);
        Assert.Empty(recorder.LastAnnouncement.ShardSizePerType);
        Assert.Null(recorder.LastAnnouncement.DeltaChainVersionCounter);
    }

    [Theory]
    [InlineData("42", 42L)]
    [InlineData("not a number", null)]
    public void TheDeltaChainCounterComesFromTheBlobHeader(string tagValue, long? expected)
    {
        Recorder recorder = new();

        recorder.OnAnnouncementComplete(
            Status.Success,
            new State(1, Dataset((HollowHeaderTags.DeltaChainVersionCounter, tagValue))),
            1,
            TimeSpan.FromMilliseconds(3));

        // An unreadable tag leaves the metric absent rather than wrong.
        Assert.Equal(expected, recorder.LastAnnouncement.DeltaChainVersionCounter);
    }

    private static Status Failed() => Status.Fail(new InvalidOperationException("the publish failed"));

    /// <summary>A two-shard dataset, optionally carrying the given blob header tags.</summary>
    private static HollowReadStateEngine Dataset(params (string Tag, string Value)[] headerTags)
    {
        HollowWriteStateEngine engine = new();

        HollowObjectSchema movie = new("Movie", 1, "Id");
        movie.AddField("Id", FieldType.Int);

        engine.AddTypeState(new HollowObjectTypeWriteState(movie, numShards: 2));

        HollowObjectWriteRecord record = new(movie);

        for (int id = 1; id <= 8; id++)
        {
            record.Reset();
            record.SetInt("Id", id);

            engine.Add("Movie", record);
        }

        foreach ((string tag, string value) in headerTags)
        {
            engine.AddHeaderTag(tag, value);
        }

        engine.PrepareForWrite();

        return StateEngineRoundTripper.RoundTripSnapshot(engine);
    }

    private sealed record State(long Version, HollowReadStateEngine StateEngine) : IReadState;

    private sealed class Recorder : ProducerMetricsListener
    {
        internal List<CycleMetrics> Cycles { get; } = [];

        internal List<AnnouncementMetrics> Announcements { get; } = [];

        internal CycleMetrics LastCycle => Cycles[^1];

        internal AnnouncementMetrics LastAnnouncement => Announcements[^1];

        protected override void ReportCycleMetrics(CycleMetrics metrics) => Cycles.Add(metrics);

        protected override void ReportAnnouncementMetrics(AnnouncementMetrics metrics) =>
            Announcements.Add(metrics);
    }
}
