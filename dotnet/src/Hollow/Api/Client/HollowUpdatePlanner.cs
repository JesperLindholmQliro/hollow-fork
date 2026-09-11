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

using Hollow.Api.Consumer;
using Hollow.Core;

namespace Hollow.Api.Client;

/// <summary>
/// Works out how a consumer should get from one version to another, by asking a blob store what it
/// holds.
/// </summary>
/// <remarks>
/// Following deltas is preferred: it keeps the consumer's ordinals stable and touches only what
/// changed. A snapshot is planned when the consumer has no data, when deltas cannot reach the
/// destination, or when there are more deltas between here and there than
/// <see cref="IDoubleSnapshotConfig.MaxDeltasBeforeDoubleSnapshot"/> allows.
/// </remarks>
public sealed class HollowUpdatePlanner
{
    private readonly IBlobRetriever _blobRetriever;
    private readonly IDoubleSnapshotConfig _doubleSnapshotConfig;
    private readonly IUpdatePlanBlobVerifier _blobVerifier;

    /// <summary>
    /// Initialises a planner over <paramref name="blobRetriever"/>.
    /// </summary>
    public HollowUpdatePlanner(
        IBlobRetriever blobRetriever,
        IDoubleSnapshotConfig? doubleSnapshotConfig = null,
        IUpdatePlanBlobVerifier? blobVerifier = null)
    {
        ArgumentNullException.ThrowIfNull(blobRetriever);

        _blobRetriever = blobRetriever;
        _doubleSnapshotConfig = doubleSnapshotConfig ?? DoubleSnapshotConfig.Default;
        _blobVerifier = blobVerifier ?? UpdatePlanBlobVerifier.Default;
    }

    /// <summary>
    /// Plans the initial load of a consumer that holds no data yet.
    /// </summary>
    public HollowUpdatePlan PlanInitializingUpdate(VersionInfo desiredVersion)
    {
        ArgumentNullException.ThrowIfNull(desiredVersion);

        return PlanUpdate(HollowConstants.VersionNone, desiredVersion, allowSnapshot: true);
    }

    /// <summary>
    /// Plans the move from <paramref name="currentVersion"/> to <paramref name="desiredVersion"/>.
    /// </summary>
    /// <param name="currentVersion">
    /// The consumer's version, or <see cref="HollowConstants.VersionNone"/> when it holds no data.
    /// </param>
    /// <param name="desiredVersion">The version to reach.</param>
    /// <param name="allowSnapshot">
    /// Whether a snapshot may be planned when deltas cannot reach the destination.
    /// </param>
    /// <returns>
    /// A plan, which may stop short of <paramref name="desiredVersion"/> when the store does not hold
    /// what would be needed to reach it.
    /// </returns>
    public HollowUpdatePlan PlanUpdate(long currentVersion, VersionInfo desiredVersion, bool allowSnapshot)
    {
        ArgumentNullException.ThrowIfNull(desiredVersion);

        if (desiredVersion.Version == currentVersion)
        {
            return HollowUpdatePlan.DoNothing;
        }

        if (currentVersion == HollowConstants.VersionNone)
        {
            return SnapshotPlan(desiredVersion);
        }

        HollowUpdatePlan deltaPlan = DeltaPlan(
            currentVersion, desiredVersion.Version, _doubleSnapshotConfig.MaxDeltasBeforeDoubleSnapshot);

        long deltaDestination = deltaPlan.DestinationVersionOr(currentVersion);

        if (deltaDestination == desiredVersion.Version || !allowSnapshot)
        {
            return deltaPlan;
        }

        HollowUpdatePlan snapshotPlan = SnapshotPlan(desiredVersion);
        long snapshotDestination = snapshotPlan.DestinationVersionOr(currentVersion);

        // Prefer the snapshot when it reaches the destination exactly, when the deltas would overshoot
        // it, or when it simply gets closer from below than the deltas do.
        bool snapshotIsBetter = snapshotDestination == desiredVersion.Version
            || (deltaDestination > desiredVersion.Version && snapshotDestination < desiredVersion.Version)
            || (snapshotDestination < desiredVersion.Version && snapshotDestination > deltaDestination);

        return snapshotIsBetter ? snapshotPlan : deltaPlan;
    }

    /// <summary>
    /// Builds a plan that loads the nearest snapshot at or before the desired version and then catches
    /// up with deltas.
    /// </summary>
    private HollowUpdatePlan SnapshotPlan(VersionInfo desiredVersion)
    {
        HollowUpdatePlan plan = new();

        long snapshotVersion = IncludeNearestSnapshot(plan, desiredVersion);

        // VersionLatest here means no snapshot was found at all, so there is nothing to build on.
        if (snapshotVersion == HollowConstants.VersionLatest || snapshotVersion > desiredVersion.Version)
        {
            return HollowUpdatePlan.DoNothing;
        }

        plan.AppendPlan(DeltaPlan(snapshotVersion, desiredVersion.Version, int.MaxValue));

        return plan;
    }

    private HollowUpdatePlan DeltaPlan(long currentVersion, long desiredVersion, int maxDeltas)
    {
        HollowUpdatePlan plan = new();

        if (currentVersion < desiredVersion)
        {
            int applied = 0;
            while (currentVersion < desiredVersion && applied < maxDeltas)
            {
                currentVersion = IncludeNextDelta(plan, currentVersion, desiredVersion);
                applied++;
            }
        }
        else if (currentVersion > desiredVersion)
        {
            int applied = 0;
            while (currentVersion > desiredVersion && applied < maxDeltas)
            {
                currentVersion = IncludeNextReverseDelta(plan, currentVersion);
                if (currentVersion == HollowConstants.VersionNone)
                {
                    break;
                }

                applied++;
            }
        }

        return plan;
    }

    /// <summary>
    /// Adds the next forward delta, unless it would take the consumer past the desired version.
    /// </summary>
    /// <returns>
    /// The version reached, or <see cref="HollowConstants.VersionLatest"/> when there is no further
    /// delta — which stops the caller's loop at the head of the chain.
    /// </returns>
    private long IncludeNextDelta(HollowUpdatePlan plan, long currentVersion, long desiredVersion)
    {
        if (_blobRetriever.RetrieveDeltaBlob(currentVersion) is not { } transition)
        {
            return HollowConstants.VersionLatest;
        }

        if (transition.ToVersion <= desiredVersion)
        {
            plan.Add(transition);
        }

        return transition.ToVersion;
    }

    /// <summary>
    /// Adds the next reverse delta.
    /// </summary>
    /// <returns>
    /// The version reached, or <see cref="HollowConstants.VersionNone"/> when the store holds no
    /// reverse delta from here.
    /// </returns>
    private long IncludeNextReverseDelta(HollowUpdatePlan plan, long currentVersion)
    {
        if (_blobRetriever.RetrieveReverseDeltaBlob(currentVersion) is not { } transition)
        {
            return HollowConstants.VersionNone;
        }

        plan.Add(transition);

        return transition.ToVersion;
    }

    /// <summary>
    /// Adds the newest snapshot at or before the desired version.
    /// </summary>
    /// <returns>
    /// The version of the snapshot added, or <see cref="HollowConstants.VersionLatest"/> when none was.
    /// </returns>
    private long IncludeNearestSnapshot(HollowUpdatePlan plan, VersionInfo desiredVersionInfo)
    {
        long desiredVersion = desiredVersionInfo.Version;

        if (_blobRetriever.RetrieveSnapshotBlob(desiredVersion) is not { } transition)
        {
            return HollowConstants.VersionLatest;
        }

        if (transition.ToVersion == desiredVersion)
        {
            plan.Add(transition);
            return transition.ToVersion;
        }

        // The exact snapshot is missing, so this is a fallback onto an older one. When the consumer is
        // chasing an announced version, an older snapshot that was never announced is one a producer
        // wrote and abandoned — keep looking back for one that was.
        bool mustVerify = _blobVerifier.AnnouncementVerificationEnabled && desiredVersionInfo.WasAnnounced == true;

        if (!mustVerify)
        {
            plan.Add(transition);
            return transition.ToVersion;
        }

        for (int lookback = 1; lookback <= _blobVerifier.AnnouncementVerificationMaxLookback; lookback++)
        {
            AnnouncementStatus status =
                _blobVerifier.AnnouncementWatcher?.GetVersionAnnouncementStatus(transition.ToVersion)
                ?? AnnouncementStatus.Unknown;

            // Unknown means the watcher cannot tell us, in which case refusing every fallback would be
            // worse than taking one: proceed, as a consumer with no watcher at all would.
            if (status is AnnouncementStatus.Announced or AnnouncementStatus.Unknown)
            {
                plan.Add(transition);
                return transition.ToVersion;
            }

            if (_blobRetriever.RetrieveSnapshotBlob(transition.ToVersion - 1) is not { } older)
            {
                break;
            }

            transition = older;
        }

        return HollowConstants.VersionLatest;
    }
}
