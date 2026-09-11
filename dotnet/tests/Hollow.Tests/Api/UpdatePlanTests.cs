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
using Hollow.Core;

namespace Hollow.Tests.Api;

/// <summary>
/// The planner decides how a consumer gets from one version to another, given what a blob store
/// happens to hold.
/// </summary>
/// <remarks>
/// These test the decision rather than the data movement: which blobs get chosen, in which order, and
/// what happens when the obvious route is unavailable.
/// </remarks>
public class UpdatePlanTests
{
    /// <summary>
    /// A blob that is never read, standing in for whatever the store holds at a given version.
    /// </summary>
    private sealed class StubBlob : Blob
    {
        internal StubBlob(long toVersion)
            : base(toVersion)
        {
        }

        internal StubBlob(long fromVersion, long toVersion)
            : base(fromVersion, toVersion)
        {
        }

        public override Stream OpenStream() => throw new NotSupportedException("nothing reads this blob");
    }

    /// <summary>
    /// A blob store described by which versions have snapshots and which transitions exist.
    /// </summary>
    private sealed class StubBlobRetriever : IBlobRetriever
    {
        internal HashSet<long> Snapshots { get; init; } = [];

        /// <summary>Forward transitions, as from-version to to-version.</summary>
        internal Dictionary<long, long> Deltas { get; init; } = [];

        /// <summary>Backward transitions, as from-version to to-version.</summary>
        internal Dictionary<long, long> ReverseDeltas { get; init; } = [];

        internal List<long> SnapshotRequests { get; } = [];

        public Blob? RetrieveSnapshotBlob(long desiredVersion)
        {
            SnapshotRequests.Add(desiredVersion);

            if (Snapshots.Contains(desiredVersion))
            {
                return new StubBlob(desiredVersion);
            }

            long nearest = Snapshots.Where(version => version < desiredVersion).DefaultIfEmpty(long.MinValue).Max();

            return nearest == long.MinValue ? null : new StubBlob(nearest);
        }

        public Blob? RetrieveDeltaBlob(long currentVersion) =>
            Deltas.TryGetValue(currentVersion, out long toVersion) ? new StubBlob(currentVersion, toVersion) : null;

        public Blob? RetrieveReverseDeltaBlob(long currentVersion) =>
            ReverseDeltas.TryGetValue(currentVersion, out long toVersion)
                ? new StubBlob(currentVersion, toVersion)
                : null;
    }

    private static StubBlobRetriever ChainOfTen() => new()
    {
        Snapshots = [.. Enumerable.Range(1, 10).Select(i => (long)i)],
        Deltas = Enumerable.Range(1, 9).ToDictionary(i => (long)i, i => (long)i + 1),
        ReverseDeltas = Enumerable.Range(2, 9).ToDictionary(i => (long)i, i => (long)i - 1),
    };

    [Fact]
    public void AnEmptyConsumerGetsASnapshot()
    {
        HollowUpdatePlanner planner = new(ChainOfTen());

        HollowUpdatePlan plan = planner.PlanInitializingUpdate(new VersionInfo(4));

        Assert.True(plan.IsSnapshotPlan);
        Assert.Equal([BlobType.Snapshot], plan.TransitionSequence);
        Assert.Equal(4, plan.DestinationVersion);
    }

    [Fact]
    public void MovingForwardsUsesDeltas()
    {
        HollowUpdatePlanner planner = new(ChainOfTen());

        HollowUpdatePlan plan = planner.PlanUpdate(2, new VersionInfo(5), allowSnapshot: true);

        Assert.False(plan.IsSnapshotPlan);
        Assert.Equal([BlobType.Delta, BlobType.Delta, BlobType.Delta], plan.TransitionSequence);
        Assert.Equal(5, plan.DestinationVersion);
    }

    [Fact]
    public void MovingBackwardsUsesReverseDeltas()
    {
        HollowUpdatePlanner planner = new(ChainOfTen());

        HollowUpdatePlan plan = planner.PlanUpdate(5, new VersionInfo(3), allowSnapshot: true);

        Assert.Equal([BlobType.ReverseDelta, BlobType.ReverseDelta], plan.TransitionSequence);
        Assert.Equal(3, plan.DestinationVersion);
    }

    [Fact]
    public void BeingAlreadyThereIsNoWork()
    {
        HollowUpdatePlanner planner = new(ChainOfTen());

        HollowUpdatePlan plan = planner.PlanUpdate(4, new VersionInfo(4), allowSnapshot: true);

        Assert.Equal(0, plan.TransitionCount);
        Assert.Equal(HollowConstants.VersionNone, plan.DestinationVersion);
        Assert.Equal(4, plan.DestinationVersionOr(4));
    }

    [Fact]
    public void TooManyDeltasBecomeASnapshot()
    {
        HollowUpdatePlanner planner = new(
            ChainOfTen(), new DoubleSnapshotConfig { MaxDeltasBeforeDoubleSnapshot = 3 });

        HollowUpdatePlan plan = planner.PlanUpdate(1, new VersionInfo(9), allowSnapshot: true);

        Assert.True(plan.IsSnapshotPlan);
        Assert.Equal(9, plan.DestinationVersion);
        Assert.Equal(1, plan.TransitionCount);
    }

    /// <summary>
    /// A consumer that forbids snapshots gets as far as the deltas take it and no further.
    /// </summary>
    [Fact]
    public void WithoutASnapshotThePlanStopsWhereTheDeltasDo()
    {
        StubBlobRetriever blobStore = new()
        {
            Snapshots = [1, 2, 3],
            Deltas = new Dictionary<long, long> { [1] = 2 },
        };

        HollowUpdatePlanner planner = new(blobStore);

        HollowUpdatePlan plan = planner.PlanUpdate(1, new VersionInfo(3), allowSnapshot: false);

        Assert.Equal([BlobType.Delta], plan.TransitionSequence);
        Assert.Equal(2, plan.DestinationVersion);
    }

    /// <summary>
    /// When the exact snapshot is missing, the plan starts from the nearest earlier one and catches up.
    /// </summary>
    [Fact]
    public void AMissingSnapshotIsMadeUpForWithDeltas()
    {
        StubBlobRetriever blobStore = new()
        {
            Snapshots = [1],
            Deltas = new Dictionary<long, long> { [1] = 2, [2] = 3 },
        };

        HollowUpdatePlanner planner = new(blobStore);

        HollowUpdatePlan plan = planner.PlanInitializingUpdate(new VersionInfo(3));

        Assert.True(plan.IsSnapshotPlan);
        Assert.Equal([BlobType.Snapshot, BlobType.Delta, BlobType.Delta], plan.TransitionSequence);
        Assert.Equal(3, plan.DestinationVersion);
    }

    [Fact]
    public void AnEmptyStoreYieldsNoPlan()
    {
        HollowUpdatePlanner planner = new(new StubBlobRetriever());

        HollowUpdatePlan plan = planner.PlanInitializingUpdate(new VersionInfo(5));

        Assert.Equal(0, plan.TransitionCount);
        Assert.Equal(HollowConstants.VersionNone, plan.DestinationVersion);
    }

    /// <summary>
    /// A snapshot older than everything the store holds cannot be reached at all.
    /// </summary>
    [Fact]
    public void AVersionOlderThanTheStoreYieldsNoPlan()
    {
        HollowUpdatePlanner planner = new(ChainOfTen());

        HollowUpdatePlan plan = planner.PlanInitializingUpdate(new VersionInfo(0));

        Assert.Equal(0, plan.TransitionCount);
    }

    /// <summary>
    /// A watcher that reports which versions were announced, so the fallback verification has something
    /// to consult.
    /// </summary>
    private sealed class AnnouncedVersionsWatcher(params long[] announced) : IAnnouncementWatcher
    {
        public long GetLatestVersion() => announced.Max();

        public void SubscribeToUpdates(HollowConsumer consumer)
        {
        }

        public AnnouncementStatus GetVersionAnnouncementStatus(long version) =>
            announced.Contains(version) ? AnnouncementStatus.Announced : AnnouncementStatus.NotAnnounced;
    }

    /// <summary>
    /// A producer that wrote a snapshot and abandoned it leaves a version no consumer should land on.
    /// With verification on, the planner keeps looking back for one that was announced.
    /// </summary>
    [Fact]
    public void AFallbackSnapshotThatWasNeverAnnouncedIsSkipped()
    {
        StubBlobRetriever blobStore = new() { Snapshots = [1, 2, 3] };

        HollowUpdatePlanner planner = new(
            blobStore,
            DoubleSnapshotConfig.Default,
            new UpdatePlanBlobVerifier
            {
                AnnouncementVerificationEnabled = true,
                AnnouncementWatcher = new AnnouncedVersionsWatcher(1, 4),
            });

        // Version 4 has no snapshot. Version 3 has one but was never announced, so the planner keeps
        // going back and settles on version 1.
        HollowUpdatePlan plan = planner.PlanInitializingUpdate(
            new VersionInfo(4, announcementMetadata: null, isPinned: null, wasAnnounced: true));

        Assert.True(plan.IsSnapshotPlan);
        Assert.Equal(1, plan.DestinationVersion);
    }

    /// <summary>
    /// Verification only applies to a fallback, and only when the consumer is chasing something that
    /// was itself announced.
    /// </summary>
    [Fact]
    public void AnExactSnapshotIsTakenWithoutVerification()
    {
        StubBlobRetriever blobStore = new() { Snapshots = [1, 2, 3] };

        HollowUpdatePlanner planner = new(
            blobStore,
            DoubleSnapshotConfig.Default,
            new UpdatePlanBlobVerifier
            {
                AnnouncementVerificationEnabled = true,
                AnnouncementWatcher = new AnnouncedVersionsWatcher(1),
            });

        HollowUpdatePlan plan = planner.PlanInitializingUpdate(
            new VersionInfo(3, announcementMetadata: null, isPinned: null, wasAnnounced: true));

        Assert.Equal(3, plan.DestinationVersion);
    }

    /// <summary>
    /// Without a watcher there is nothing to verify against, so refusing every fallback would be worse
    /// than taking one.
    /// </summary>
    [Fact]
    public void VerificationWithoutAWatcherTakesTheFallbackAnyway()
    {
        StubBlobRetriever blobStore = new() { Snapshots = [1, 2] };

        HollowUpdatePlanner planner = new(
            blobStore,
            DoubleSnapshotConfig.Default,
            new UpdatePlanBlobVerifier { AnnouncementVerificationEnabled = true });

        HollowUpdatePlan plan = planner.PlanInitializingUpdate(
            new VersionInfo(5, announcementMetadata: null, isPinned: null, wasAnnounced: true));

        Assert.Equal(2, plan.DestinationVersion);
    }

    [Fact]
    public void APlanDescribesItself()
    {
        HollowUpdatePlanner planner = new(ChainOfTen());

        HollowUpdatePlan plan = planner.PlanUpdate(1, new VersionInfo(3), allowSnapshot: true);

        Assert.Equal("delta to 2, delta to 3", plan.ToString());
        Assert.Equal("", HollowUpdatePlan.DoNothing.ToString());
    }

    [Fact]
    public void APlanSeparatesItsSnapshotFromItsDeltas()
    {
        StubBlobRetriever blobStore = new()
        {
            Snapshots = [1],
            Deltas = new Dictionary<long, long> { [1] = 2, [2] = 3 },
        };

        HollowUpdatePlan plan = new HollowUpdatePlanner(blobStore).PlanInitializingUpdate(new VersionInfo(3));

        Assert.Equal(1, plan.SnapshotTransition!.ToVersion);
        Assert.Equal([2, 3], plan.DeltaTransitions.Select(transition => transition.ToVersion));
        Assert.Null(HollowUpdatePlan.DoNothing.SnapshotTransition);
    }
}
