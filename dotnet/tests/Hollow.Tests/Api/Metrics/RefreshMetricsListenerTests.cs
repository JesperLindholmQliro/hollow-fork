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
using Hollow.Api.Consumer.Metrics;
using Hollow.Core;
using Hollow.Core.Read.Engine;
using Hollow.Core.Schema;
using Hollow.Core.Write;

namespace Hollow.Tests.Api.Metrics;

/// <summary>
/// Turns a consumer's refresh events into refresh metrics.
/// </summary>
/// <remarks>
/// As with the producer's listener, the tests drive the events directly: what is worth asserting is
/// the state the listener carries between refreshes, and the classification decisions it makes from
/// events a single happy-path refresh would never produce.
/// </remarks>
public class RefreshMetricsListenerTests
{
    private const long Announced = 1_700_000_000_000;

    [Fact]
    public void AnInitialLoadIsMarkedAsOneAndTheNextRefreshIsNot()
    {
        Recorder recorder = new();

        recorder.RefreshStarted(HollowConstants.VersionNone, 100);
        recorder.RefreshSuccessful(HollowConstants.VersionNone, 100, 100);

        Assert.True(recorder.Last.IsInitialLoad);

        recorder.RefreshStarted(100, 200);
        recorder.RefreshSuccessful(100, 200, 200);

        Assert.False(recorder.Last.IsInitialLoad);
        Assert.Equal(TimeSpan.Zero, recorder.Last.SinceLastRefreshSuccess);
    }

    [Theory]
    [InlineData(true, 100L, 200L, BlobType.Snapshot)]
    [InlineData(false, 100L, 200L, BlobType.Delta)]
    [InlineData(false, 200L, 100L, BlobType.ReverseDelta)]
    public void ThePlanDecidesWhatTheRefreshCost(
        bool isSnapshotPlan, long beforeVersion, long desiredVersion, BlobType expected)
    {
        Recorder recorder = new();

        recorder.RefreshStarted(beforeVersion, desiredVersion);
        recorder.TransitionsPlanned(beforeVersion, desiredVersion, isSnapshotPlan, [expected]);
        recorder.RefreshSuccessful(beforeVersion, desiredVersion, desiredVersion);

        // A plan containing a snapshot is a snapshot refresh however many deltas follow it.
        Assert.Equal(expected, recorder.Last.OverallRefreshType);
    }

    [Fact]
    public void ARefreshThatFailedPartWayReportsHowFarItGot()
    {
        Recorder recorder = new();

        recorder.RefreshStarted(100, 400);
        recorder.TransitionsPlanned(100, 400, false, [BlobType.Delta, BlobType.Delta, BlobType.Delta]);

        // The listener only counts the blobs, so what they are is beside the point here.
        recorder.BlobLoaded(null!);
        recorder.BlobLoaded(null!);

        recorder.RefreshFailed(100, 300, 400, new IOException("the third delta would not load"));

        UpdatePlanDetails plan = recorder.Last.UpdatePlan;

        Assert.Equal(100, plan.BeforeVersion);
        Assert.Equal(400, plan.DesiredVersion);
        Assert.Equal(3, plan.TransitionSequence.Count);
        Assert.Equal(2, plan.SuccessfulTransitionCount);
        Assert.False(recorder.Last.IsRefreshSuccess);
    }

    [Fact]
    public void AFailedRefreshCountsUpAndReportsTheAgeOfTheLastSuccess()
    {
        Recorder recorder = new();

        recorder.RefreshStarted(HollowConstants.VersionNone, 100);
        recorder.RefreshFailed(
            HollowConstants.VersionNone, HollowConstants.VersionNone, 100, new IOException("no blobs"));

        Assert.Equal(1, recorder.Last.ConsecutiveFailures);

        // Nothing has ever succeeded, so there is no age to report rather than an age of zero.
        Assert.Null(recorder.Last.SinceLastRefreshSuccess);

        recorder.RefreshStarted(HollowConstants.VersionNone, 100);
        recorder.RefreshSuccessful(HollowConstants.VersionNone, 100, 100);

        Assert.Equal(0, recorder.Last.ConsecutiveFailures);

        recorder.RefreshStarted(100, 200);
        recorder.RefreshFailed(100, 100, 200, new IOException("the delta would not load"));

        Assert.Equal(1, recorder.Last.ConsecutiveFailures);
        Assert.NotNull(recorder.Last.SinceLastRefreshSuccess);
    }

    [Fact]
    public void TheProducerCycleStartAndChainCounterComeFromTheBlobHeader()
    {
        Recorder recorder = new();

        DateTimeOffset cycleStart = DateTimeOffset.FromUnixTimeMilliseconds(Announced);

        HollowReadStateEngine dataset = Dataset(
            (HollowHeaderTags.MetricCycleStart, Announced.ToString(CultureInfo.InvariantCulture)),
            (HollowHeaderTags.DeltaChainVersionCounter, "7"));

        recorder.RefreshStarted(HollowConstants.VersionNone, 100);
        recorder.SnapshotUpdateOccurred(dataset, 100);
        recorder.RefreshSuccessful(HollowConstants.VersionNone, 100, 100);

        Assert.Equal(cycleStart, recorder.Last.CycleStartTime);
        Assert.Equal(7, recorder.Last.DeltaChainVersionCounter);
    }

    [Fact]
    public void AVersionWhoseHeaderSaysNothingReportsNothing()
    {
        Recorder recorder = new();

        recorder.RefreshStarted(HollowConstants.VersionNone, 100);
        recorder.SnapshotUpdateOccurred(Dataset((HollowHeaderTags.MetricCycleStart, "yesterday")), 100);
        recorder.RefreshSuccessful(HollowConstants.VersionNone, 100, 100);

        // An unreadable tag leaves the metric absent rather than wrong.
        Assert.Null(recorder.Last.CycleStartTime);
        Assert.Null(recorder.Last.DeltaChainVersionCounter);
    }

    [Fact]
    public void AnAnnouncementTimeIsIgnoredUntilThePinIsWellBehind()
    {
        Recorder recorder = new();

        recorder.VersionDetected(new VersionInfo(100, Metadata(), isPinned: true));
        Refresh(recorder, from: HollowConstants.VersionNone, to: 100);

        // Pinned: the announcement belongs to a version the consumer was held at, not one it follows.
        Assert.Null(recorder.Last.AnnouncementTime);

        recorder.VersionDetected(new VersionInfo(200, Metadata(), isPinned: false));
        Refresh(recorder, from: 100, to: 200);

        // The refresh that unpins moves off a held version, so its lag is not the producer's either.
        Assert.Null(recorder.Last.AnnouncementTime);

        recorder.VersionDetected(new VersionInfo(300, Metadata(), isPinned: false));
        Refresh(recorder, from: 200, to: 300);

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(Announced), recorder.Last.AnnouncementTime);
    }

    [Fact]
    public void AConsumerThatStoppedShortReportsNoAnnouncementTime()
    {
        Recorder recorder = new();

        recorder.VersionDetected(new VersionInfo(300, Metadata(), isPinned: false));
        recorder.VersionDetected(new VersionInfo(300, Metadata(), isPinned: false));

        recorder.RefreshStarted(100, 300);
        recorder.RefreshSuccessful(100, 200, 300);

        // Reaching 200 when 300 was announced is lag, and the announcement time would understate it.
        Assert.Null(recorder.Last.AnnouncementTime);
    }

    [Fact]
    public void AReporterThatThrowsDoesNotFailTheRefresh()
    {
        Recorder recorder = new() { Failure = new InvalidOperationException("the metrics backend is down") };

        Exception? raised = null;
        recorder.ReportingFailed += (_, exception) => raised = exception;

        recorder.RefreshStarted(HollowConstants.VersionNone, 100);
        recorder.RefreshSuccessful(HollowConstants.VersionNone, 100, 100);

        Assert.Same(recorder.Failure, raised);
    }

    private static void Refresh(Recorder recorder, long from, long to)
    {
        recorder.RefreshStarted(from, to);
        recorder.RefreshSuccessful(from, to, to);
    }

    private static IReadOnlyDictionary<string, string> Metadata() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [HollowHeaderTags.MetricAnnouncement] = Announced.ToString(CultureInfo.InvariantCulture),
        };

    private static HollowReadStateEngine Dataset(params (string Tag, string Value)[] headerTags)
    {
        HollowWriteStateEngine engine = new();

        HollowObjectSchema movie = new("Movie", 1, "Id");
        movie.AddField("Id", FieldType.Int);

        engine.AddTypeState(new HollowObjectTypeWriteState(movie));

        HollowObjectWriteRecord record = new(movie);
        record.SetInt("Id", 1);
        engine.Add("Movie", record);

        foreach ((string tag, string value) in headerTags)
        {
            engine.AddHeaderTag(tag, value);
        }

        engine.PrepareForWrite();

        return StateEngineRoundTripper.RoundTripSnapshot(engine);
    }

    private sealed class Recorder : RefreshMetricsListener
    {
        internal List<ConsumerRefreshMetrics> Refreshes { get; } = [];

        internal Exception? Failure { get; init; }

        internal ConsumerRefreshMetrics Last => Refreshes[^1];

        protected override void ReportRefreshMetrics(ConsumerRefreshMetrics metrics)
        {
            Refreshes.Add(metrics);

            if (Failure is not null)
            {
                throw Failure;
            }
        }
    }
}
