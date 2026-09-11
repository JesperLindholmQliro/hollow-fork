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
