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
using Hollow.Core.Read;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Filter;

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
    private readonly IDoubleSnapshotConfig _doubleSnapshotConfig;
    private readonly FailedTransitionTracker _failedTransitionTracker;
    private readonly ITypeFilter? _filter;

    internal HollowDataHolder(
        HollowReadStateEngine stateEngine,
        IDoubleSnapshotConfig doubleSnapshotConfig,
        FailedTransitionTracker failedTransitionTracker,
        ITypeFilter? filter)
    {
        _stateEngine = stateEngine;
        _reader = new HollowBlobReader(stateEngine);
        _doubleSnapshotConfig = doubleSnapshotConfig;
        _failedTransitionTracker = failedTransitionTracker;
        _filter = filter;
    }

    internal HollowReadStateEngine StateEngine => _stateEngine;

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

            CurrentVersion = blob.ToVersion;

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
}
