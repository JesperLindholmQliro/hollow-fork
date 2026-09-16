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

namespace Hollow.Api.Producer.Metrics;

/// <summary>
/// What one of a producer's cycles cost and whether it worked.
/// </summary>
/// <remarks>
/// Java builds this through a nested <c>Builder</c>, because a class with eight final fields and
/// several optional ones has no other readable construction. An init-only record needs no builder, so
/// the builders of <c>api.producer.metrics</c> are not ported.
/// </remarks>
public sealed record CycleMetrics
{
    /// <summary>How many cycles have failed in a row, counting this one.</summary>
    public long ConsecutiveFailures { get; init; }

    /// <summary>
    /// How long the cycle took, or <see langword="null"/> where it was skipped and never ran.
    /// </summary>
    public TimeSpan? CycleDuration { get; init; }

    /// <summary>
    /// Whether the cycle succeeded, or <see langword="null"/> where it was skipped — a skipped cycle
    /// is neither a success nor a failure.
    /// </summary>
    public bool? IsCycleSuccess { get; init; }

    /// <summary>
    /// When a cycle last succeeded, or <see langword="null"/> until one has.
    /// </summary>
    /// <remarks>
    /// A <see cref="Stopwatch.GetTimestamp"/> reading, so it bears no relation to the wall clock and
    /// survives the clock being set. Turn it into an age with
    /// <see cref="Stopwatch.GetElapsedTime(long)"/>. Java calls this <c>lastCycleSuccessTimeNano</c>
    /// and hands out raw <c>System.nanoTime</c>; the unit differs here, which is why the name does too.
    /// </remarks>
    public long? LastCycleSuccessTimestamp { get; init; }
}

/// <summary>
/// What a producer announced, how big it was, and whether the announcement worked.
/// </summary>
public sealed record AnnouncementMetrics
{
    /// <summary>An approximation of the announced dataset's footprint, in bytes.</summary>
    public long DataSizeBytes { get; init; }

    /// <summary>How many shards each type of the announced dataset is split into.</summary>
    public IReadOnlyDictionary<string, int> NumShardsPerType { get; init; } =
        ReadOnlyDictionary<string, int>.Empty;

    /// <summary>An approximation of one shard's footprint, for each type, in bytes.</summary>
    public IReadOnlyDictionary<string, long> ShardSizePerType { get; init; } =
        ReadOnlyDictionary<string, long>.Empty;

    /// <summary>How long the announcement took.</summary>
    public TimeSpan AnnouncementDuration { get; init; }

    /// <summary>Whether the announcement succeeded.</summary>
    public bool IsAnnouncementSuccess { get; init; }

    /// <summary>
    /// When an announcement last succeeded, as a <see cref="Stopwatch.GetTimestamp"/> reading, or
    /// <see langword="null"/> until one has.
    /// </summary>
    public long? LastAnnouncementSuccessTimestamp { get; init; }

    /// <summary>
    /// How many versions this delta chain has been through, where the blob header says so.
    /// </summary>
    /// <remarks>
    /// A chain that restarted resets the counter, so a consumer's counter going backwards is how it
    /// finds out. Absent, or unparseable, leaves this <see langword="null"/>.
    /// </remarks>
    public long? DeltaChainVersionCounter { get; init; }
}
