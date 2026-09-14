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

namespace Hollow.Api.Consumer;

/// <summary>
/// When a consumer is allowed to abandon its delta chain and load a fresh snapshot.
/// </summary>
/// <remarks>
/// <para>
/// Applying a snapshot when the consumer already has data — a "double snapshot" — momentarily holds two
/// copies of the dataset in memory and invalidates every index built over the old one. It is how a
/// consumer recovers from a forked or broken delta chain, but it is expensive enough that a consumer
/// under memory pressure may forbid it and follow deltas only.
/// </para>
/// <para>
/// Named <c>HollowConsumer.DoubleSnapshotConfig</c> in Java; the <c>I</c> prefix follows the .NET
/// interface naming convention.
/// </para>
/// </remarks>
public interface IDoubleSnapshotConfig
{
    /// <summary>Whether a snapshot may be loaded over an already-populated state.</summary>
    bool AllowDoubleSnapshot { get; }

    /// <summary>
    /// How many deltas a single refresh may chain before a snapshot becomes the cheaper way to catch
    /// up.
    /// </summary>
    int MaxDeltasBeforeDoubleSnapshot { get; }

    /// <summary>
    /// Whether a changed data model should force a snapshot, detected from the schema hash the
    /// producer puts in the announcement metadata.
    /// </summary>
    /// <remarks>
    /// Deltas carry records, not a data model, so a consumer following deltas keeps the schemas it
    /// first loaded. Only a snapshot picks up a new field or type.
    /// </remarks>
    bool DoubleSnapshotOnSchemaChange => false;
}

/// <summary>
/// The double-snapshot policy a consumer uses unless it is given another.
/// </summary>
public sealed class DoubleSnapshotConfig : IDoubleSnapshotConfig
{
    /// <summary>Allows double snapshots, and chains at most 32 deltas per refresh.</summary>
    public static IDoubleSnapshotConfig Default { get; } = new DoubleSnapshotConfig();

    /// <summary>Never loads a snapshot over an already-populated state.</summary>
    public static IDoubleSnapshotConfig DeltasOnly { get; } = new DoubleSnapshotConfig
    {
        AllowDoubleSnapshot = false,
        MaxDeltasBeforeDoubleSnapshot = int.MaxValue,
    };

    /// <inheritdoc />
    public bool AllowDoubleSnapshot { get; init; } = true;

    /// <inheritdoc />
    public int MaxDeltasBeforeDoubleSnapshot { get; init; } = 32;

    /// <inheritdoc />
    public bool DoubleSnapshotOnSchemaChange { get; init; }
}

/// <summary>
/// Whether a snapshot the update planner falls back to has to have been announced.
/// </summary>
/// <remarks>
/// When the exact snapshot a consumer asked for is missing, the planner takes the nearest earlier one.
/// That snapshot may be one a producer wrote and then abandoned, which no consumer was ever meant to
/// see. Verifying the fallback against the announcement history avoids landing on one.
/// </remarks>
public interface IUpdatePlanBlobVerifier
{
    /// <summary>
    /// Whether to discard a fallback snapshot that was never announced.
    /// </summary>
    bool AnnouncementVerificationEnabled { get; }

    /// <summary>
    /// How many successively older snapshots to try before giving up.
    /// </summary>
    int AnnouncementVerificationMaxLookback { get; }

    /// <summary>
    /// The watcher consulted for a version's announcement status, or <see langword="null"/> when none
    /// is available — in which case verification cannot happen and the fallback is taken as it is.
    /// </summary>
    IAnnouncementWatcher? AnnouncementWatcher { get; }
}

/// <summary>
/// The fallback-snapshot policy a consumer uses unless it is given another.
/// </summary>
public sealed class UpdatePlanBlobVerifier : IUpdatePlanBlobVerifier
{
    /// <summary>Verification off, which is what a consumer without an announcement watcher gets.</summary>
    public static IUpdatePlanBlobVerifier Default { get; } = new UpdatePlanBlobVerifier();

    /// <inheritdoc />
    public bool AnnouncementVerificationEnabled { get; init; }

    /// <inheritdoc />
    public int AnnouncementVerificationMaxLookback { get; init; } = 5;

    /// <inheritdoc />
    public IAnnouncementWatcher? AnnouncementWatcher { get; init; }
}

/// <summary>
/// Whether records stay readable after the state they came from has moved on, and for how long.
/// </summary>
/// <remarks>
/// <para>
/// Hollow reuses its memory. A record read out of a consumer is a handle — a type and an ordinal —
/// not a copy, so once a delta lands, that ordinal may hold a different record and the handle
/// silently starts reading it. Ordinarily that is fine, because a caller reads what it needs and lets
/// go. It is not fine when a reference outlives a refresh: a cached object, a request that took longer
/// than the cycle, a background task holding a list.
/// </para>
/// <para>
/// With <see cref="EnableLongLivedObjectSupport"/> on, the consumer builds its API over a
/// <see cref="Core.Read.DataAccess.Proxy.HollowProxyDataAccess"/>, and on each delta points the
/// outgoing proxy at a historical state holding exactly the records that transition removed. Every
/// reference a caller already holds keeps reading what it always read. The cost is that those
/// historical states are retained, which is what the periods below bound.
/// </para>
/// <para>
/// Named <c>HollowConsumer.ObjectLongevityConfig</c> in Java; the <c>I</c> prefix follows the .NET
/// interface naming convention.
/// </para>
/// </remarks>
public interface IObjectLongevityConfig
{
    /// <summary>Whether a reference keeps reading its own data after a refresh.</summary>
    bool EnableLongLivedObjectSupport { get; }

    /// <summary>
    /// How long after a refresh a reference is left entirely alone.
    /// </summary>
    /// <remarks>
    /// Long enough to cover whatever legitimately outlives one cycle. Nothing is flagged or dropped
    /// during it.
    /// </remarks>
    TimeSpan GracePeriod { get; }

    /// <summary>
    /// How long after the grace period the consumer watches whether the stale data is actually read.
    /// </summary>
    /// <remarks>
    /// Reads still succeed throughout. What the window decides is whether the data may be dropped at
    /// the end of it: a reference that was read is presumed still in use and kept.
    /// </remarks>
    TimeSpan UsageDetectionPeriod { get; }

    /// <summary>
    /// Whether to drop the data behind a stale reference once both periods have passed and no read was
    /// seen.
    /// </summary>
    /// <remarks>
    /// Without this the historical states are held until the references to them are collected, which is
    /// safe but unbounded — a single leaked reference pins every state since it was taken.
    /// </remarks>
    bool DropDataAutomatically { get; }

    /// <summary>
    /// Whether to drop the data even though a read was seen during the usage detection window.
    /// </summary>
    /// <remarks>
    /// For finding the leaks rather than tolerating them: a read after this point throws, which turns a
    /// silent over-long reference into a stack trace naming the code that holds it.
    /// </remarks>
    bool ForceDropData { get; }
}

/// <summary>
/// The object longevity policy a consumer uses unless it is given another.
/// </summary>
public sealed class ObjectLongevityConfig : IObjectLongevityConfig
{
    /// <summary>Longevity off, which is what a consumer gets unless it asks otherwise.</summary>
    public static IObjectLongevityConfig Default { get; } = new ObjectLongevityConfig();

    /// <summary>
    /// Longevity on, with Java's hour-long grace and usage detection periods and automatic dropping.
    /// </summary>
    public static IObjectLongevityConfig Enabled { get; } = new ObjectLongevityConfig
    {
        EnableLongLivedObjectSupport = true,
        DropDataAutomatically = true,
    };

    /// <inheritdoc />
    public bool EnableLongLivedObjectSupport { get; init; }

    /// <inheritdoc />
    public TimeSpan GracePeriod { get; init; } = TimeSpan.FromHours(1);

    /// <inheritdoc />
    public TimeSpan UsageDetectionPeriod { get; init; } = TimeSpan.FromHours(1);

    /// <inheritdoc />
    public bool DropDataAutomatically { get; init; }

    /// <inheritdoc />
    public bool ForceDropData { get; init; }
}
