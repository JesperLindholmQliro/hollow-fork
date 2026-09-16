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

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Hollow.Api.Producer.Listener;
using Hollow.Core;
using Hollow.Core.Read.Engine;

namespace Hollow.Api.Producer.Metrics;

/// <summary>
/// Works out a producer's cycle and announcement metrics from its lifecycle events, and hands them to
/// a derived class to report wherever it reports things.
/// </summary>
/// <remarks>
/// <para>
/// Named <c>AbstractProducerMetricsListener</c> in Java, where the <c>Abstract</c> prefix does the
/// work that the <c>abstract</c> keyword does here.
/// </para>
/// <para>
/// Java splits the two reporting methods out into a <c>ProducerMetricsReporting</c> interface with
/// default no-op methods, so that a subclass can implement one and ignore the other. Protected
/// virtual methods say the same thing without a second type, so that interface is not ported.
/// </para>
/// <para>
/// The counters are per-instance and span cycles, so one listener has to stay registered with one
/// producer for its whole life — a fresh instance reports a consecutive-failure count of zero however
/// long the producer has been failing.
/// </para>
/// </remarks>
public abstract class ProducerMetricsListener : HollowProducerListener
{
    private long _consecutiveFailures;
    private long? _lastCycleSuccessTimestamp;
    private long? _lastAnnouncementSuccessTimestamp;

    /// <summary>Reports the metrics of a cycle that finished or was skipped.</summary>
    /// <remarks>Named <c>cycleMetricsReporting</c> in Java.</remarks>
    protected virtual void ReportCycleMetrics(CycleMetrics metrics)
    {
    }

    /// <summary>Reports the metrics of an announcement that finished.</summary>
    /// <remarks>Named <c>announcementMetricsReporting</c> in Java.</remarks>
    protected virtual void ReportAnnouncementMetrics(AnnouncementMetrics metrics)
    {
    }

    /// <summary>
    /// Reports a cycle that never ran, most often because this producer is not the primary one.
    /// </summary>
    /// <remarks>
    /// Leader election favours long-lived leaders, so a producer that is not primary skips cycle after
    /// cycle and that is healthy. A skip therefore reports neither a duration nor an outcome, and
    /// leaves the failure count and the last success where they were.
    /// </remarks>
    public override void OnCycleSkip(CycleSkipReason reason) =>
        ReportCycleMetrics(new CycleMetrics
        {
            ConsecutiveFailures = _consecutiveFailures,
            LastCycleSuccessTimestamp = _lastCycleSuccessTimestamp,
        });

    /// <inheritdoc />
    public override void OnCycleComplete(
        Status status, IReadState? readState, long version, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(status);

        bool succeeded = status.Type == StatusType.Success;

        if (succeeded)
        {
            _consecutiveFailures = 0;
            _lastCycleSuccessTimestamp = Stopwatch.GetTimestamp();
        }
        else
        {
            _consecutiveFailures++;
        }

        ReportCycleMetrics(new CycleMetrics
        {
            ConsecutiveFailures = _consecutiveFailures,
            CycleDuration = elapsed,
            IsCycleSuccess = succeeded,
            LastCycleSuccessTimestamp = _lastCycleSuccessTimestamp,
        });
    }

    /// <inheritdoc />
    /// <remarks>
    /// The data size is measured here rather than at cycle end because this is the last point at which
    /// the announced state is still the one in hand.
    /// </remarks>
    public override void OnAnnouncementComplete(
        Status status, IReadState? readState, long version, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(status);

        bool succeeded = status.Type == StatusType.Success;

        if (succeeded)
        {
            _lastAnnouncementSuccessTimestamp = Stopwatch.GetTimestamp();
        }

        // A failed announcement can arrive without a state, and a metric missing its sizes is better
        // than a metrics listener that throws inside the producer's cycle.
        HollowReadStateEngine? stateEngine = readState?.StateEngine;

        ReportAnnouncementMetrics(new AnnouncementMetrics
        {
            DataSizeBytes = stateEngine?.ApproxDataSize ?? 0,
            NumShardsPerType = stateEngine?.NumShardsPerType ?? ReadOnlyDictionary<string, int>.Empty,
            ShardSizePerType = stateEngine?.ApproxShardSizePerType ?? ReadOnlyDictionary<string, long>.Empty,
            AnnouncementDuration = elapsed,
            IsAnnouncementSuccess = succeeded,
            LastAnnouncementSuccessTimestamp = _lastAnnouncementSuccessTimestamp,
            DeltaChainVersionCounter = DeltaChainVersionCounter(stateEngine),
        });
    }

    /// <summary>
    /// The delta chain counter the blob header carries, or nothing where it carries none this port can
    /// read.
    /// </summary>
    private static long? DeltaChainVersionCounter(HollowReadStateEngine? stateEngine) =>
        stateEngine is not null
        && stateEngine.HeaderTags.TryGetValue(HollowHeaderTags.DeltaChainVersionCounter, out string? value)
        && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long counter)
            ? counter
            : null;
}
