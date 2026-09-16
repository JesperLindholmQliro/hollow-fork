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

using System.Diagnostics;
using System.Globalization;
using Hollow.Core;
using Hollow.Core.Read.Engine;

namespace Hollow.Api.Consumer.Metrics;

/// <summary>
/// Works out a consumer's refresh metrics from its refresh events, and hands them to a derived class
/// to report wherever it reports things.
/// </summary>
/// <remarks>
/// <para>
/// Named <c>AbstractRefreshMetricsListener</c> in Java. Its <c>RefreshMetricsReporting</c> interface
/// is not ported: a single mandatory method is what <c>protected abstract</c> is for.
/// </para>
/// <para>
/// Metrics reporting is not part of a refresh succeeding, so an exception out of
/// <see cref="ReportRefreshMetrics"/> is caught and raised on <see cref="ReportingFailed"/> rather
/// than allowed to fail the refresh.
/// </para>
/// </remarks>
public abstract class RefreshMetricsListener : HollowRefreshListener
{
    /// <summary>When a producer cycle began, per version, as the blob headers reported it.</summary>
    private readonly Dictionary<long, long> _cycleStartTimes = [];

    /// <summary>When a version was announced, as the announcement metadata reported it.</summary>
    private readonly Dictionary<long, long> _announcementTimes = [];

    private readonly Dictionary<long, long> _deltaChainVersionCounters = [];

    private long _refreshStartTimestamp;
    private long? _lastRefreshSuccessTimestamp;
    private long _consecutiveFailures;
    private bool _isInitialLoad;
    private BlobType? _overallRefreshType;

    private long _beforeVersion;
    private long _desiredVersion;
    private IReadOnlyList<BlobType> _transitionSequence = [];
    private int _successfulTransitions;

    private volatile bool _namespacePinnedPreviously;

    /// <summary>
    /// Raised when <see cref="ReportRefreshMetrics"/> throws, which is otherwise swallowed so that a
    /// reporting failure cannot fail a refresh.
    /// </summary>
    /// <remarks>
    /// Java logs this at severe. The port takes no logging dependency; see <c>PORTING.md</c>.
    /// </remarks>
    public event EventHandler<Exception>? ReportingFailed;

    /// <summary>
    /// Reports the metrics of a refresh that finished, whether it succeeded or failed.
    /// </summary>
    /// <remarks>Named <c>refreshEndMetricsReporting</c> in Java.</remarks>
    protected abstract void ReportRefreshMetrics(ConsumerRefreshMetrics metrics);

    /// <inheritdoc />
    public override void RefreshStarted(long currentVersion, long requestedVersion)
    {
        _refreshStartTimestamp = Stopwatch.GetTimestamp();
        _isInitialLoad = currentVersion == HollowConstants.VersionNone;

        _overallRefreshType = null;
        _beforeVersion = 0;
        _desiredVersion = 0;
        _transitionSequence = [];
        _successfulTransitions = 0;

        // Cleared per refresh, or a long-lived consumer accumulates an entry per version it ever saw.
        _cycleStartTimes.Clear();
        _deltaChainVersionCounters.Clear();
    }

    /// <inheritdoc />
    public override void VersionDetected(VersionInfo requestedVersion)
    {
        ArgumentNullException.ThrowIfNull(requestedVersion);

        _announcementTimes.Clear();

        if (requestedVersion.IsPinned is not bool isPinned
            || requestedVersion.AnnouncementMetadata is not { } metadata)
        {
            return;
        }

        // A pinned namespace holds the consumer at a version announced long ago, and the refresh that
        // unpins it moves off one. Either way the announcement time belongs to a version the consumer
        // was not following, so the age it would imply is not the consumer's lag.
        if (!_namespacePinnedPreviously && !isPinned)
        {
            Track(_announcementTimes, requestedVersion.Version, metadata, HollowHeaderTags.MetricAnnouncement);
        }

        _namespacePinnedPreviously = isPinned;
    }

    /// <inheritdoc />
    public override void TransitionsPlanned(
        long beforeVersion, long desiredVersion, bool isSnapshotPlan, IReadOnlyList<BlobType> transitionSequence)
    {
        _beforeVersion = beforeVersion;
        _desiredVersion = desiredVersion;
        _transitionSequence = transitionSequence;

        _overallRefreshType = isSnapshotPlan
            ? BlobType.Snapshot
            : desiredVersion > beforeVersion
                ? BlobType.Delta
                : BlobType.ReverseDelta;
    }

    /// <inheritdoc />
    public override void BlobLoaded(Blob transition) => _successfulTransitions++;

    /// <inheritdoc />
    public override void SnapshotUpdateOccurred(HollowReadStateEngine stateEngine, long version) =>
        TrackHeaderTags(stateEngine, version);

    /// <inheritdoc />
    public override void DeltaUpdateOccurred(HollowReadStateEngine stateEngine, long version) =>
        TrackHeaderTags(stateEngine, version);

    /// <inheritdoc />
    public override void RefreshSuccessful(long beforeVersion, long afterVersion, long requestedVersion)
    {
        long end = Stopwatch.GetTimestamp();

        _consecutiveFailures = 0;
        _lastRefreshSuccessTimestamp = end;

        Report(
            afterVersion,
            end,
            succeeded: true,
            sinceLastSuccess: TimeSpan.Zero,

            // A consumer that stopped short of the version it asked for has not caught up with the
            // announcement, so the announcement time would understate its lag rather than measure it.
            reachedRequestedVersion: afterVersion == requestedVersion);
    }

    /// <inheritdoc />
    public override void RefreshFailed(
        long beforeVersion, long afterVersion, long requestedVersion, Exception failureCause)
    {
        long end = Stopwatch.GetTimestamp();

        _consecutiveFailures++;

        Report(
            afterVersion,
            end,
            succeeded: false,
            sinceLastSuccess: _lastRefreshSuccessTimestamp is long last
                ? Stopwatch.GetElapsedTime(last, end)
                : null,
            reachedRequestedVersion: false);
    }

    private void Report(
        long version, long endTimestamp, bool succeeded, TimeSpan? sinceLastSuccess, bool reachedRequestedVersion)
    {
        ConsumerRefreshMetrics metrics = new()
        {
            Duration = Stopwatch.GetElapsedTime(_refreshStartTimestamp, endTimestamp),
            IsRefreshSuccess = succeeded,
            IsInitialLoad = _isInitialLoad,
            OverallRefreshType = _overallRefreshType,
            UpdatePlan = new UpdatePlanDetails
            {
                BeforeVersion = _beforeVersion,
                DesiredVersion = _desiredVersion,
                TransitionSequence = _transitionSequence,
                SuccessfulTransitionCount = _successfulTransitions,
            },
            ConsecutiveFailures = _consecutiveFailures,
            SinceLastRefreshSuccess = sinceLastSuccess,
            RefreshEndTimestamp = endTimestamp,
            CycleStartTime = TimeOf(_cycleStartTimes, version),
            AnnouncementTime = reachedRequestedVersion ? TimeOf(_announcementTimes, version) : null,
            DeltaChainVersionCounter =
                _deltaChainVersionCounters.TryGetValue(version, out long counter) ? counter : null,
        };

        try
        {
            ReportRefreshMetrics(metrics);
        }
        catch (Exception e)
        {
            ReportingFailed?.Invoke(this, e);
        }
    }

    private void TrackHeaderTags(HollowReadStateEngine stateEngine, long version)
    {
        ArgumentNullException.ThrowIfNull(stateEngine);

        Track(_cycleStartTimes, version, stateEngine.HeaderTags, HollowHeaderTags.MetricCycleStart);
        Track(
            _deltaChainVersionCounters, version, stateEngine.HeaderTags,
            HollowHeaderTags.DeltaChainVersionCounter);
    }

    /// <summary>
    /// Records what a blob header or announcement says about one version, where it says anything this
    /// port can read.
    /// </summary>
    /// <remarks>
    /// A tag that is absent or unparseable leaves the metric absent rather than wrong. Java logs a
    /// warning in the unparseable case; the port takes no logging dependency.
    /// </remarks>
    private static void Track(
        Dictionary<long, long> tracker, long version, IReadOnlyDictionary<string, string> tags, string tag)
    {
        if (tags.TryGetValue(tag, out string? value)
            && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
        {
            tracker[version] = parsed;
        }
    }

    private static DateTimeOffset? TimeOf(Dictionary<long, long> tracker, long version) =>
        tracker.TryGetValue(version, out long unixMilliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds)
            : null;
}
