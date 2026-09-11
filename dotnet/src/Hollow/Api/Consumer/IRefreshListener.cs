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

using Hollow.Core.Read.Engine;

namespace Hollow.Api.Consumer;

/// <summary>
/// Observes a consumer's refreshes, so that whatever is built over the data can be kept in step with
/// it.
/// </summary>
/// <remarks>
/// <para>
/// The two methods that matter for an index are <see cref="SnapshotUpdateOccurred"/> and
/// <see cref="DeltaUpdateOccurred"/>: exactly one of the two is called per refresh. A snapshot update
/// means the data was replaced wholesale and anything derived from it must be rebuilt; a delta update
/// means it changed incrementally and a derived structure can be updated in place.
/// </para>
/// <para>
/// Anything a listener throws fails the refresh, which leaves the consumer on its previous version and
/// marks the transition as failed.
/// </para>
/// <para>
/// <strong>Port note.</strong> Java passes a generated <c>HollowAPI</c> alongside the read state
/// engine. Code generation is not ported, so these take the read state engine alone.
/// </para>
/// </remarks>
public interface IRefreshListener
{
    /// <summary>
    /// Called when a refresh has picked a version to move to, before any work happens.
    /// </summary>
    void VersionDetected(VersionInfo requestedVersion)
    {
    }

    /// <summary>
    /// Called when a refresh begins.
    /// </summary>
    /// <param name="currentVersion">The version the consumer is on.</param>
    /// <param name="requestedVersion">The version it is heading for.</param>
    void RefreshStarted(long currentVersion, long requestedVersion);

    /// <summary>
    /// Called once per refresh, at the end, when the refresh loaded a snapshot — either because this
    /// is the consumer's first data or because it had to abandon its delta chain.
    /// </summary>
    /// <remarks>Rebuild any indexing here: none of the previous ordinals still mean anything.</remarks>
    void SnapshotUpdateOccurred(HollowReadStateEngine stateEngine, long version);

    /// <summary>
    /// Called once per delta applied, when the refresh consisted only of deltas.
    /// </summary>
    /// <remarks>
    /// Not called during a snapshot refresh, even though deltas may be applied as part of one — see
    /// <see cref="ITransitionAwareRefreshListener.DeltaApplied"/> for the per-transition callback that
    /// is.
    /// </remarks>
    void DeltaUpdateOccurred(HollowReadStateEngine stateEngine, long version);

    /// <summary>
    /// Called for each blob applied, snapshot or delta.
    /// </summary>
    void BlobLoaded(Blob transition);

    /// <summary>
    /// Called when a refresh finishes without error.
    /// </summary>
    /// <param name="beforeVersion">The version the consumer was on when the refresh started.</param>
    /// <param name="afterVersion">The version it is on now.</param>
    /// <param name="requestedVersion">The version it was asked for, which it may not have reached.</param>
    void RefreshSuccessful(long beforeVersion, long afterVersion, long requestedVersion);

    /// <summary>
    /// Called when a refresh fails.
    /// </summary>
    /// <param name="beforeVersion">The version the consumer was on when the refresh started.</param>
    /// <param name="afterVersion">The version it is on now, which may be neither of the others.</param>
    /// <param name="requestedVersion">The version it was asked for.</param>
    /// <param name="failureCause">What went wrong.</param>
    void RefreshFailed(long beforeVersion, long afterVersion, long requestedVersion, Exception failureCause);
}

/// <summary>
/// Observes each individual transition within a refresh, rather than only the refresh as a whole.
/// </summary>
public interface ITransitionAwareRefreshListener : IRefreshListener
{
    /// <summary>
    /// Called after the update plan is built and before it runs, once per refresh.
    /// </summary>
    /// <param name="beforeVersion">The version the consumer is on.</param>
    /// <param name="desiredVersion">The version it was asked for, which may be unreachable.</param>
    /// <param name="isSnapshotPlan">Whether the plan begins with a snapshot.</param>
    /// <param name="transitionSequence">What the plan will apply, in order.</param>
    void TransitionsPlanned(
        long beforeVersion, long desiredVersion, bool isSnapshotPlan, IReadOnlyList<BlobType> transitionSequence)
    {
    }

    /// <summary>
    /// Called whenever a snapshot is applied, which is at most once per refresh and always first.
    /// </summary>
    void SnapshotApplied(HollowReadStateEngine stateEngine, long version);

    /// <summary>
    /// Called whenever a delta is applied, including the deltas that follow a snapshot within one
    /// refresh.
    /// </summary>
    void DeltaApplied(HollowReadStateEngine stateEngine, long version);
}

/// <summary>
/// Notified when it is added to or removed from a consumer, for a listener that has to hold resources
/// tied to one.
/// </summary>
public interface IRefreshRegistrationListener
{
    /// <summary>Called before this listener is added to <paramref name="consumer"/>.</summary>
    void OnBeforeAddition(HollowConsumer consumer);

    /// <summary>Called after this listener is removed from <paramref name="consumer"/>.</summary>
    void OnAfterRemoval(HollowConsumer consumer);
}

/// <summary>
/// A refresh listener that does nothing, to be derived from and overridden selectively.
/// </summary>
/// <remarks>Named <c>HollowConsumer.AbstractRefreshListener</c> in Java.</remarks>
public abstract class HollowRefreshListener : ITransitionAwareRefreshListener
{
    /// <inheritdoc />
    public virtual void VersionDetected(VersionInfo requestedVersion)
    {
    }

    /// <inheritdoc />
    public virtual void RefreshStarted(long currentVersion, long requestedVersion)
    {
    }

    /// <inheritdoc />
    public virtual void TransitionsPlanned(
        long beforeVersion, long desiredVersion, bool isSnapshotPlan, IReadOnlyList<BlobType> transitionSequence)
    {
    }

    /// <inheritdoc />
    public virtual void SnapshotUpdateOccurred(HollowReadStateEngine stateEngine, long version)
    {
    }

    /// <inheritdoc />
    public virtual void DeltaUpdateOccurred(HollowReadStateEngine stateEngine, long version)
    {
    }

    /// <inheritdoc />
    public virtual void BlobLoaded(Blob transition)
    {
    }

    /// <inheritdoc />
    public virtual void RefreshSuccessful(long beforeVersion, long afterVersion, long requestedVersion)
    {
    }

    /// <inheritdoc />
    public virtual void RefreshFailed(
        long beforeVersion, long afterVersion, long requestedVersion, Exception failureCause)
    {
    }

    /// <inheritdoc />
    public virtual void SnapshotApplied(HollowReadStateEngine stateEngine, long version)
    {
    }

    /// <inheritdoc />
    public virtual void DeltaApplied(HollowReadStateEngine stateEngine, long version)
    {
    }
}
