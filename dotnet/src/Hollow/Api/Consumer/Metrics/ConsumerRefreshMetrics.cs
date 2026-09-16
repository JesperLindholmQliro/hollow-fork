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

namespace Hollow.Api.Consumer.Metrics;

/// <summary>
/// What one of a consumer's refreshes cost and whether it worked.
/// </summary>
/// <remarks>
/// Java builds this through a nested <c>Builder</c>; an init-only record needs none, so the builder is
/// not ported. Java also declares <c>UpdatePlanDetails</c> twice — once nested here and once as a
/// top-level class that nothing uses — and only the nested one is ported.
/// </remarks>
public sealed record ConsumerRefreshMetrics
{
    /// <summary>How long the refresh took, from its start to its success or failure.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Whether the refresh reached a version.</summary>
    public bool IsRefreshSuccess { get; init; }

    /// <summary>Whether this was the consumer's first load rather than a move from one version to another.</summary>
    public bool IsInitialLoad { get; init; }

    /// <summary>
    /// What the refresh amounted to overall, or <see langword="null"/> where it failed before a plan
    /// was made.
    /// </summary>
    /// <remarks>
    /// A plan containing a snapshot counts as a snapshot however many deltas follow it, because the
    /// snapshot is what the refresh cost.
    /// </remarks>
    public BlobType? OverallRefreshType { get; init; }

    /// <summary>What the refresh planned to do, and how far it got.</summary>
    /// <remarks>Named <c>updatePlanDetails</c> in Java.</remarks>
    public UpdatePlanDetails UpdatePlan { get; init; } = new();

    /// <summary>How many refreshes have failed in a row, counting this one.</summary>
    public long ConsecutiveFailures { get; init; }

    /// <summary>
    /// How long ago the previous refresh succeeded; zero on a success, and <see langword="null"/> on a
    /// failure where none has succeeded yet.
    /// </summary>
    /// <remarks>Named <c>refreshSuccessAgeMillisOptional</c> in Java.</remarks>
    public TimeSpan? SinceLastRefreshSuccess { get; init; }

    /// <summary>
    /// When the refresh ended, as a <see cref="Stopwatch.GetTimestamp"/> reading.
    /// </summary>
    /// <remarks>
    /// Monotonic, so it bears no relation to the wall clock. Java hands out <c>System.nanoTime</c>
    /// under the name <c>refreshEndTimeNano</c>; the unit differs here, which is why the name does too.
    /// </remarks>
    public long RefreshEndTimestamp { get; init; }

    /// <summary>
    /// When the producer's cycle for the loaded version began, where the blob header says so.
    /// </summary>
    /// <remarks>
    /// This one really is wall-clock time — the producer writes it into the header as Unix
    /// milliseconds — so unlike <see cref="RefreshEndTimestamp"/> it is a point in time and typed as
    /// one. Java calls both of them timestamps and leaves the difference to be discovered.
    /// </remarks>
    public DateTimeOffset? CycleStartTime { get; init; }

    /// <summary>
    /// When the loaded version was announced, where the announcement metadata says so and the
    /// consumer reached the version it asked for.
    /// </summary>
    public DateTimeOffset? AnnouncementTime { get; init; }

    /// <summary>
    /// How many versions the loaded delta chain has been through, where the blob header says so.
    /// </summary>
    public long? DeltaChainVersionCounter { get; init; }
}

/// <summary>
/// What a refresh planned to do, and how much of it it managed.
/// </summary>
/// <remarks>
/// A refresh is generally several transitions, and a failure part way leaves the consumer at whatever
/// version the last successful transition reached. The count and the kinds are what say whether a
/// refresh was expensive, and how far a failed one got.
/// </remarks>
public sealed record UpdatePlanDetails
{
    /// <summary>The version the consumer held when the refresh started.</summary>
    public long BeforeVersion { get; init; }

    /// <summary>
    /// The version the refresh aimed at, which may be <see cref="Core.HollowConstants.VersionLatest"/>
    /// rather than a version that exists.
    /// </summary>
    public long DesiredVersion { get; init; }

    /// <summary>The transitions the plan consists of, in order.</summary>
    public IReadOnlyList<BlobType> TransitionSequence { get; init; } = [];

    /// <summary>How many of those transitions loaded.</summary>
    /// <remarks>Named <c>numSuccessfulTransitions</c> in Java.</remarks>
    public int SuccessfulTransitionCount { get; init; }
}
