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
using Hollow.Api.Custom;
using Hollow.Core;
using Hollow.Core.Memory;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Filter;
using Hollow.Core.Schema;
using Hollow.Core.Util;

namespace Hollow.Api.Client;

/// <summary>
/// The refresh machinery behind a <see cref="HollowConsumer"/>: plans a move to a requested version and
/// carries it out, keeping the consumer on its previous data if anything goes wrong.
/// </summary>
/// <remarks>
/// Split out from <see cref="HollowConsumer"/> as it is in Java. A consumer owns exactly one of these
/// and serialises calls to <see cref="UpdateTo(VersionInfo)"/>.
/// </remarks>
public sealed class HollowClientUpdater
{
    private readonly Lock _updateLock = new();
    private readonly HollowUpdatePlanner _planner;
    private readonly TaskCompletionSource<long> _initialLoad =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly FailedTransitionTracker _failedTransitionTracker = new();
    private readonly IHollowApiFactory _apiFactory;
    private readonly IDoubleSnapshotConfig _doubleSnapshotConfig;
    private readonly ITypeFilter? _filter;
    private readonly MemoryMode _memoryMode;

    /// <summary>
    /// The listeners, held in an immutable array so that a refresh can take a snapshot of them without
    /// locking and a concurrent add or remove takes effect on the next refresh instead.
    /// </summary>
    private volatile IRefreshListener[] _refreshListeners;

    /// <summary>
    /// The current data. Volatile because a reader may take it without holding
    /// <see cref="_updateLock"/>; it is only ever replaced by a snapshot refresh.
    /// </summary>
    private volatile HollowDataHolder? _dataHolder;

    private bool _forceDoubleSnapshot;

    /// <summary>
    /// Initialises an updater over <paramref name="blobRetriever"/>.
    /// </summary>
    public HollowClientUpdater(
        IBlobRetriever blobRetriever,
        IEnumerable<IRefreshListener>? refreshListeners = null,
        IDoubleSnapshotConfig? doubleSnapshotConfig = null,
        IUpdatePlanBlobVerifier? blobVerifier = null,
        ITypeFilter? filter = null,
        MemoryMode memoryMode = MemoryMode.OnHeap,
        IHollowApiFactory? apiFactory = null)
    {
        ArgumentNullException.ThrowIfNull(blobRetriever);

        _apiFactory = apiFactory ?? DefaultHollowApiFactory.Instance;
        _doubleSnapshotConfig = doubleSnapshotConfig ?? DoubleSnapshotConfig.Default;
        _planner = new HollowUpdatePlanner(blobRetriever, _doubleSnapshotConfig, blobVerifier);
        _refreshListeners = [.. (refreshListeners ?? []).Distinct()];
        _filter = filter;
        _memoryMode = memoryMode;
    }

    /// <summary>
    /// Completes with the version of the first data this updater successfully loaded.
    /// </summary>
    public Task<long> InitialLoad => _initialLoad.Task;

    /// <summary>
    /// The state engine holding the current data, or <see langword="null"/> before the first refresh.
    /// </summary>
    public HollowReadStateEngine? StateEngine => _dataHolder?.StateEngine;

    /// <summary>
    /// The typed API over the current data, or <see langword="null"/> before the first refresh.
    /// </summary>
    public HollowApi? Api => _dataHolder?.Api;

    /// <summary>
    /// The version currently held, or <see cref="HollowConstants.VersionNone"/> before the first
    /// refresh.
    /// </summary>
    public long CurrentVersionId => _dataHolder?.CurrentVersion ?? HollowConstants.VersionNone;

    /// <summary>The number of distinct snapshots that have failed to apply.</summary>
    public int NumFailedSnapshotTransitions => _failedTransitionTracker.NumFailedSnapshotTransitions;

    /// <summary>The number of distinct deltas that have failed to apply.</summary>
    public int NumFailedDeltaTransitions => _failedTransitionTracker.NumFailedDeltaTransitions;

    /// <summary>
    /// Forgets the recorded transition failures, so they may be tried again on the next refresh.
    /// </summary>
    public void ClearFailedTransitions() => _failedTransitionTracker.Clear();

    /// <summary>
    /// Makes the next refresh load a snapshot rather than follow deltas.
    /// </summary>
    public void ForceDoubleSnapshotNextUpdate() => _forceDoubleSnapshot = true;

    /// <summary>Adds a refresh listener, which takes effect on the next refresh.</summary>
    public void AddRefreshListener(IRefreshListener refreshListener, HollowConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(refreshListener);

        lock (_updateLock)
        {
            if (_refreshListeners.Contains(refreshListener))
            {
                return;
            }

            (refreshListener as IRefreshRegistrationListener)?.OnBeforeAddition(consumer);
            _refreshListeners = [.. _refreshListeners, refreshListener];
        }
    }

    /// <summary>Removes a refresh listener, which takes effect on the next refresh.</summary>
    public void RemoveRefreshListener(IRefreshListener refreshListener, HollowConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(refreshListener);

        lock (_updateLock)
        {
            if (!_refreshListeners.Contains(refreshListener))
            {
                return;
            }

            _refreshListeners = [.. _refreshListeners.Where(listener => !ReferenceEquals(listener, refreshListener))];
            (refreshListener as IRefreshRegistrationListener)?.OnAfterRemoval(consumer);
        }
    }

    /// <summary>
    /// Moves the data to <paramref name="requestedVersion"/>, or as close to it as the blob store
    /// allows.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the data now sits on exactly the requested version — including when
    /// it already did and nothing was applied.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// No plan could be built, because neither that version nor any qualifying earlier one could be
    /// retrieved.
    /// </exception>
    public bool UpdateTo(long requestedVersion) => UpdateTo(new VersionInfo(requestedVersion));

    /// <summary>
    /// Moves the data to <paramref name="requestedVersionInfo"/>, or as close to it as the blob store
    /// allows.
    /// </summary>
    /// <inheritdoc cref="UpdateTo(long)" path="/returns|/exception"/>
    public bool UpdateTo(VersionInfo requestedVersionInfo)
    {
        ArgumentNullException.ThrowIfNull(requestedVersionInfo);

        lock (_updateLock)
        {
            return UpdateToUnderLock(requestedVersionInfo);
        }
    }

    private bool UpdateToUnderLock(VersionInfo requestedVersionInfo)
    {
        long requestedVersion = requestedVersionInfo.Version;

        if (requestedVersion == CurrentVersionId)
        {
            if (requestedVersion == HollowConstants.VersionNone && _dataHolder is null)
            {
                // Nothing has ever been announced. Sitting on an empty state is better than failing,
                // but the next refresh has to be a snapshot however the config is set.
                _dataHolder = NewDataHolder();
                _forceDoubleSnapshot = true;
            }

            return true;
        }

        // Take the listeners once: an add or remove during the refresh belongs to the next one.
        IRefreshListener[] listeners = _refreshListeners;

        foreach (IRefreshListener listener in listeners)
        {
            listener.VersionDetected(requestedVersionInfo);
        }

        long beforeVersion = CurrentVersionId;

        foreach (IRefreshListener listener in listeners)
        {
            listener.RefreshStarted(beforeVersion, requestedVersion);
        }

        try
        {
            HollowUpdatePlan updatePlan = ShouldCreateSnapshotPlan(requestedVersionInfo)
                ? _planner.PlanInitializingUpdate(requestedVersionInfo)
                : _planner.PlanUpdate(
                    _dataHolder!.CurrentVersion, requestedVersionInfo, _doubleSnapshotConfig.AllowDoubleSnapshot);

            foreach (IRefreshListener listener in listeners)
            {
                if (listener is ITransitionAwareRefreshListener transitionAware)
                {
                    transitionAware.TransitionsPlanned(
                        beforeVersion, requestedVersion, updatePlan.IsSnapshotPlan, updatePlan.TransitionSequence);
                }
            }

            if (updatePlan.DestinationVersion == HollowConstants.VersionNone
                && requestedVersion != HollowConstants.VersionLatest)
            {
                string message =
                    $"Could not create an update plan for version {requestedVersion.Invariant()}, because that "
                    + "version and every qualifying previous version could not be retrieved.";

                if (beforeVersion != HollowConstants.VersionNone)
                {
                    message +=
                        $" The consumer will remain on version {beforeVersion.Invariant()} until the next attempt.";
                }

                throw new InvalidOperationException(message);
            }

            if (updatePlan.TransitionCount == 0 && requestedVersion == HollowConstants.VersionLatest)
            {
                throw new InvalidOperationException(
                    "Could not create an update plan, because no existing versions could be retrieved.");
            }

            if (updatePlan.DestinationVersionOr(requestedVersion) == CurrentVersionId)
            {
                return true;
            }

            if (updatePlan.IsSnapshotPlan)
            {
                HollowDataHolder? previous = _dataHolder;

                // A holder that has never loaded a version holds nothing worth protecting, so the
                // double-snapshot restriction does not apply to it. Java compares against null here,
                // which means a delta-only consumer that initialised to an empty state — because
                // nothing had been announced yet — could never load any data at all.
                bool holdsData = previous is not null && previous.CurrentVersion != HollowConstants.VersionNone;

                if (!holdsData || _doubleSnapshotConfig.AllowDoubleSnapshot)
                {
                    HollowDataHolder next = NewDataHolder();

                    try
                    {
                        // The new holder is published as soon as the snapshot is readable, so that a
                        // listener calling back into the consumer sees the new data rather than the old.
                        next.Update(updatePlan, listeners, () => _dataHolder = next);
                    }
                    catch
                    {
                        _dataHolder = previous;
                        throw;
                    }

                    _forceDoubleSnapshot = false;
                }
            }
            else
            {
                _dataHolder!.Update(updatePlan, listeners, static () => { });
            }

            foreach (IRefreshListener listener in listeners)
            {
                listener.RefreshSuccessful(beforeVersion, CurrentVersionId, requestedVersion);
            }

            _initialLoad.TrySetResult(CurrentVersionId);

            return CurrentVersionId == requestedVersion;
        }
        catch (Exception e)
        {
            // Whatever went wrong, the delta chain is no longer trustworthy from here.
            _forceDoubleSnapshot = true;

            foreach (IRefreshListener listener in listeners)
            {
                listener.RefreshFailed(beforeVersion, CurrentVersionId, requestedVersion, e);
            }

            // InitialLoad is deliberately not failed: a producer that publishes often gives the consumer
            // another chance in a moment, and a caller awaiting the first load would rather wait.
            throw;
        }
    }

    /// <summary>
    /// Whether the next refresh has to start from a snapshot rather than follow deltas.
    /// </summary>
    internal bool ShouldCreateSnapshotPlan(VersionInfo incomingVersionInfo)
    {
        if (CurrentVersionId == HollowConstants.VersionNone
            || (_forceDoubleSnapshot && _doubleSnapshotConfig.AllowDoubleSnapshot))
        {
            return true;
        }

        if (!_doubleSnapshotConfig.DoubleSnapshotOnSchemaChange || !_doubleSnapshotConfig.AllowDoubleSnapshot)
        {
            return false;
        }

        // Detecting a schema change depends on the producer publishing its schema hash and on the
        // announcement mechanism passing the metadata through. Without both, deltas carry on and the
        // consumer keeps the data model it has.
        if (incomingVersionInfo.AnnouncementMetadata is not { } metadata
            || !metadata.TryGetValue(HollowHeaderTags.SchemaHash, out string? incomingHash))
        {
            return false;
        }

        return !string.Equals(new HollowSchemaHash(StateEngine!).Hash, incomingHash, StringComparison.Ordinal);
    }

    private HollowDataHolder NewDataHolder()
    {
        // Reuse the previous state's recycler: its pooled segments are exactly the sizes the new state
        // needs, which is most of the point of loading a snapshot over an existing consumer.
        HollowReadStateEngine stateEngine = _dataHolder is { } existing
            ? new HollowReadStateEngine(_memoryMode, existing.StateEngine.MemoryRecycler)
            : new HollowReadStateEngine(_memoryMode);

        return new HollowDataHolder(
            stateEngine, _apiFactory, _doubleSnapshotConfig, _failedTransitionTracker, _filter);
    }
}
