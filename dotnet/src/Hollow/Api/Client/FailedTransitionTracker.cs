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

namespace Hollow.Api.Client;

/// <summary>
/// Remembers the blobs that failed to apply, so a consumer does not keep retrying a transition it has
/// already proved it cannot make.
/// </summary>
/// <remarks>
/// This only bites when double snapshots are allowed: a consumer that can only follow deltas would be
/// stuck forever on stale data if a transitory failure were treated as permanent. Call
/// <see cref="Clear"/> to let the failures be reattempted.
/// </remarks>
public sealed class FailedTransitionTracker
{
    private readonly HashSet<long> _failedSnapshots = [];
    private readonly HashSet<(long FromVersion, long ToVersion)> _failedDeltas = [];

    /// <summary>The number of distinct snapshots recorded as failed.</summary>
    public int NumFailedSnapshotTransitions => _failedSnapshots.Count;

    /// <summary>The number of distinct deltas recorded as failed.</summary>
    public int NumFailedDeltaTransitions => _failedDeltas.Count;

    /// <summary>Records every blob in <paramref name="plan"/> as failed.</summary>
    public void MarkAllTransitionsAsFailed(HollowUpdatePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        foreach (Blob transition in plan)
        {
            MarkFailedTransition(transition);
        }
    }

    /// <summary>Records <paramref name="transition"/> as failed.</summary>
    public void MarkFailedTransition(Blob transition)
    {
        ArgumentNullException.ThrowIfNull(transition);

        if (transition.IsSnapshot)
        {
            _failedSnapshots.Add(transition.ToVersion);
        }
        else
        {
            _failedDeltas.Add((transition.FromVersion, transition.ToVersion));
        }
    }

    /// <summary>Whether any blob in <paramref name="plan"/> is already known to fail.</summary>
    public bool AnyTransitionWasFailed(HollowUpdatePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return plan.Any(TransitionWasFailed);
    }

    /// <summary>Forgets every recorded failure, so the transitions may be tried again.</summary>
    public void Clear()
    {
        _failedSnapshots.Clear();
        _failedDeltas.Clear();
    }

    private bool TransitionWasFailed(Blob transition) =>
        transition.IsSnapshot
            ? _failedSnapshots.Contains(transition.ToVersion)
            : _failedDeltas.Contains((transition.FromVersion, transition.ToVersion));
}
