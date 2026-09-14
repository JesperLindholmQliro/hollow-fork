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
using Hollow.Core.Read;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.DataAccess.Proxy;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Filter;
using Hollow.Core.Tools.History;

namespace Hollow.Api.Client;

/// <summary>
/// One read state engine and the version it currently holds, plus the machinery for moving it along an
/// update plan.
/// </summary>
/// <remarks>
/// A consumer replaces its data holder wholesale when it loads a snapshot and keeps the same one while
/// it follows deltas. That is what lets a reader hold on to a state engine across a delta refresh but
/// not across a double snapshot.
/// </remarks>
internal sealed class HollowDataHolder
{
    private readonly HollowReadStateEngine _stateEngine;
    private readonly HollowBlobReader _reader;
    private readonly IHollowApiFactory _apiFactory;
    private readonly IDoubleSnapshotConfig _doubleSnapshotConfig;
    private readonly FailedTransitionTracker _failedTransitionTracker;
    private readonly ITypeFilter? _filter;
    private readonly IObjectLongevityConfig _objectLongevityConfig;
    private readonly StaleReferenceDetector? _staleReferenceDetector;

    /// <summary>
    /// The historical state the previous transition produced, held weakly.
    /// </summary>
    /// <remarks>
    /// Weakly, because the chain exists only for the references that are still out there. Once the
    /// last record from a given state has been collected, nothing can ask that state anything, and
    /// holding it would pin every state after it too.
    /// </remarks>
    private WeakReference<HollowHistoricalStateDataAccess>? _priorHistoricalDataAccess;

    internal HollowDataHolder(
        HollowReadStateEngine stateEngine,
        IHollowApiFactory apiFactory,
        IDoubleSnapshotConfig doubleSnapshotConfig,
        FailedTransitionTracker failedTransitionTracker,
        ITypeFilter? filter,
        IObjectLongevityConfig? objectLongevityConfig = null,
        StaleReferenceDetector? staleReferenceDetector = null)
    {
        _stateEngine = stateEngine;
        _reader = new HollowBlobReader(stateEngine);
        _apiFactory = apiFactory;
        _doubleSnapshotConfig = doubleSnapshotConfig;
        _failedTransitionTracker = failedTransitionTracker;
        _filter = filter;
        _objectLongevityConfig = objectLongevityConfig ?? ObjectLongevityConfig.Default;
        _staleReferenceDetector = staleReferenceDetector;
    }

    internal HollowReadStateEngine StateEngine => _stateEngine;

    /// <summary>
    /// The typed API over this holder's data, or <see langword="null"/> until a snapshot has been read.
    /// </summary>
    /// <remarks>
    /// Built once per snapshot and kept across deltas, since the type APIs read through the state
    /// engine itself and anything caching underneath is its own delta listener.
    /// </remarks>
    internal HollowApi? Api { get; private set; }

    internal long CurrentVersion { get; private set; } = HollowConstants.VersionNone;

    /// <summary>
    /// Applies <paramref name="updatePlan"/>, leaving this holder on the plan's destination version.
    /// </summary>
    /// <param name="updatePlan">The blobs to apply.</param>
    /// <param name="refreshListeners">The listeners to notify as each blob lands.</param>
    /// <param name="onSnapshotLoaded">
    /// Run once the snapshot has been read, before any following deltas. A consumer uses it to publish
    /// this holder, so that a listener calling back into the consumer sees the new state rather than
    /// the old one.
    /// </param>
    internal void Update(
        HollowUpdatePlan updatePlan, IReadOnlyList<IRefreshListener> refreshListeners, Action onSnapshotLoaded)
    {
        // Only refuse a known-bad transition when the consumer could recover with a snapshot. A
        // delta-only consumer would otherwise be stuck on stale data forever after one network blip,
        // since a "failed transition" covers everything from a corrupt blob to a listener that threw.
        if (_doubleSnapshotConfig.AllowDoubleSnapshot && _failedTransitionTracker.AnyTransitionWasFailed(updatePlan))
        {
            throw new InvalidOperationException("The update plan contains a transition that is known to fail.");
        }

        if (updatePlan.IsSnapshotPlan)
        {
            ApplySnapshotPlan(updatePlan, refreshListeners, onSnapshotLoaded);
        }
        else
        {
            foreach (Blob transition in updatePlan)
            {
                ApplyDeltaTransition(transition, isSnapshotPlan: false, refreshListeners);
            }
        }
    }

    private void ApplySnapshotPlan(
        HollowUpdatePlan updatePlan, IReadOnlyList<IRefreshListener> refreshListeners, Action onSnapshotLoaded)
    {
        ApplySnapshotTransition(updatePlan.SnapshotTransition!, refreshListeners, onSnapshotLoaded);

        foreach (Blob transition in updatePlan.DeltaTransitions)
        {
            ApplyDeltaTransition(transition, isSnapshotPlan: true, refreshListeners);
        }

        try
        {
            foreach (IRefreshListener listener in refreshListeners)
            {
                listener.SnapshotUpdateOccurred(_stateEngine, updatePlan.DestinationVersion);
            }
        }
        catch
        {
            // A listener that cannot cope with this data makes the whole plan unusable, not just the
            // transition that happened to be last.
            _failedTransitionTracker.MarkAllTransitionsAsFailed(updatePlan);
            throw;
        }
    }

    private void ApplySnapshotTransition(
        Blob snapshotBlob, IReadOnlyList<IRefreshListener> refreshListeners, Action onSnapshotLoaded)
    {
        try
        {
            using (Stream stream = snapshotBlob.OpenStream())
            using (HollowBlobInput input = HollowBlobInput.Serial(stream, leaveOpen: true))
            {
                _reader.ReadSnapshot(input, _filter);
            }

            CurrentVersion = snapshotBlob.ToVersion;

            foreach (IRefreshListener listener in refreshListeners)
            {
                listener.BlobLoaded(snapshotBlob);
            }

            // A snapshot read into this holder's own state engine leaves the outgoing API's caches and
            // indexes registered against type states that now hold different records. Nothing else will
            // ever let go of them, so this is where they are released.
            Api?.DetachCaches();

            // Before the consumer publishes this holder, so that a listener reaching back for
            // HollowConsumer.Api never sees the API that belonged to the data this one replaced.
            Api = _apiFactory.CreateApi(DataAccessForNewApi());
            _staleReferenceDetector?.NewApiHandle(Api);

            onSnapshotLoaded();

            foreach (IRefreshListener listener in refreshListeners)
            {
                if (listener is ITransitionAwareRefreshListener transitionAware)
                {
                    transitionAware.SnapshotApplied(_stateEngine, snapshotBlob.ToVersion);
                }
            }
        }
        catch
        {
            _failedTransitionTracker.MarkFailedTransition(snapshotBlob);
            throw;
        }
    }

    private void ApplyDeltaTransition(
        Blob blob, bool isSnapshotPlan, IReadOnlyList<IRefreshListener> refreshListeners)
    {
        try
        {
            using (Stream stream = blob.OpenStream())
            using (HollowBlobInput input = HollowBlobInput.Serial(stream, leaveOpen: true))
            {
                _reader.ApplyDelta(input);
            }

            long previousVersion = CurrentVersion;

            CurrentVersion = blob.ToVersion;

            MoveApiOnToTheNewState(previousVersion);

            foreach (IRefreshListener listener in refreshListeners)
            {
                listener.BlobLoaded(blob);

                if (!isSnapshotPlan)
                {
                    listener.DeltaUpdateOccurred(_stateEngine, blob.ToVersion);
                }

                if (listener is ITransitionAwareRefreshListener transitionAware)
                {
                    transitionAware.DeltaApplied(_stateEngine, blob.ToVersion);
                }
            }
        }
        catch
        {
            _failedTransitionTracker.MarkFailedTransition(blob);
            throw;
        }
    }

    /// <summary>
    /// What a newly built API should read through: the live state, or a proxy onto it when longevity
    /// is on.
    /// </summary>
    private IHollowDataAccess DataAccessForNewApi()
    {
        if (!_objectLongevityConfig.EnableLongLivedObjectSupport)
        {
            return _stateEngine;
        }

        HollowProxyDataAccess dataAccess = new();
        dataAccess.SetDataAccess(_stateEngine);

        return dataAccess;
    }

    /// <summary>
    /// Points the API at the state the delta just produced, preserving whatever the old API's records
    /// could read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With longevity off this is almost nothing: the API already reads through the state engine, which
    /// the delta has moved on, so a caller's references now read the new data. That is the ordinary
    /// Hollow contract.
    /// </para>
    /// <para>
    /// With it on, three things happen. A historical state is built holding the records this transition
    /// removed; the <em>outgoing</em> proxy — the one every existing reference reads through — is
    /// pointed at it, so those references keep their values; and a new API is built over a fresh proxy
    /// onto the live state, for everything obtained from here on.
    /// </para>
    /// </remarks>
    private void MoveApiOnToTheNewState(long previousVersion)
    {
        if (Api is null)
        {
            return;
        }

        if (!_objectLongevityConfig.EnableLongLivedObjectSupport)
        {
            if (!ReferenceEquals(Api.DataAccess, _stateEngine))
            {
                Api = _apiFactory.CreateApi(_stateEngine);
            }

            _priorHistoricalDataAccess = null;

            return;
        }

        IHollowDataAccess previousDataAccess = Api.DataAccess;

        HollowHistoricalStateDataAccess priorState =
            new HollowHistoricalStateCreator().CreateBasedOnNewDelta(previousVersion, _stateEngine);

        HollowProxyDataAccess newDataAccess = new();
        newDataAccess.SetDataAccess(_stateEngine);

        Api = _apiFactory.CreateApi(newDataAccess, Api);

        if (previousDataAccess is HollowProxyDataAccess previousProxy)
        {
            previousProxy.SetDataAccess(priorState);
        }

        WireHistoricalStateChain(priorState);

        _staleReferenceDetector?.NewApiHandle(Api);
    }

    /// <summary>
    /// Links the previous historical state to this one.
    /// </summary>
    /// <remarks>
    /// A historical state holds only the records its own transition removed, so a reference reading
    /// through it may ask for a record it does not have — one that survived that transition and was
    /// removed by a later one, or one that is still live. Answering means walking forward along the
    /// chain to whichever state does have it, ending at the live read state.
    /// </remarks>
    private void WireHistoricalStateChain(HollowHistoricalStateDataAccess nextPriorState)
    {
        if (_priorHistoricalDataAccess is not null
            && _priorHistoricalDataAccess.TryGetTarget(out HollowHistoricalStateDataAccess? dataAccess))
        {
            dataAccess.NextState = nextPriorState;
        }

        _priorHistoricalDataAccess = new WeakReference<HollowHistoricalStateDataAccess>(nextPriorState);
    }
}
