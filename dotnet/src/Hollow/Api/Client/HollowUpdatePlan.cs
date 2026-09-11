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

using System.Collections;
using Hollow.Api.Consumer;
using Hollow.Core;
using Hollow.Core.Util;

namespace Hollow.Api.Client;

/// <summary>
/// The blobs a consumer has to apply, in order, to get from where it is to where it wants to be.
/// </summary>
/// <remarks>
/// A plan is either a chain of deltas — forwards or backwards — or a snapshot followed by zero or more
/// deltas. The empty plan means there is nothing to do.
/// </remarks>
public sealed class HollowUpdatePlan : IEnumerable<Blob>
{
    private readonly List<Blob> _transitions;

    /// <summary>
    /// Initialises an empty plan.
    /// </summary>
    public HollowUpdatePlan() => _transitions = [];

    private HollowUpdatePlan(List<Blob> transitions) => _transitions = transitions;

    /// <summary>A plan that applies nothing.</summary>
    public static HollowUpdatePlan DoNothing { get; } = new([]);

    /// <summary>The blobs this plan applies, in order.</summary>
    public IReadOnlyList<Blob> Transitions => _transitions;

    /// <summary>Whether this plan starts by replacing the consumer's data with a snapshot.</summary>
    public bool IsSnapshotPlan => _transitions.Count > 0 && _transitions[0].IsSnapshot;

    /// <summary>
    /// The snapshot this plan starts with, or <see langword="null"/> when it is a delta-only plan.
    /// </summary>
    public Blob? SnapshotTransition => IsSnapshotPlan ? _transitions[0] : null;

    /// <summary>The deltas this plan applies after any snapshot.</summary>
    public IReadOnlyList<Blob> DeltaTransitions =>
        IsSnapshotPlan ? _transitions[1..] : _transitions;

    /// <summary>What this plan applies, in order.</summary>
    public IReadOnlyList<BlobType> TransitionSequence =>
        [.. _transitions.Select(transition => transition.BlobType)];

    /// <summary>The number of blobs this plan applies.</summary>
    public int TransitionCount => _transitions.Count;

    /// <summary>
    /// The version this plan arrives at, or <see cref="HollowConstants.VersionNone"/> when it applies
    /// nothing.
    /// </summary>
    public long DestinationVersion =>
        _transitions.Count == 0 ? HollowConstants.VersionNone : _transitions[^1].ToVersion;

    /// <summary>
    /// The version this plan arrives at, falling back to <paramref name="currentVersion"/> when it
    /// applies nothing.
    /// </summary>
    public long DestinationVersionOr(long currentVersion) =>
        DestinationVersion == HollowConstants.VersionNone ? currentVersion : DestinationVersion;

    /// <summary>Appends a blob to this plan.</summary>
    public void Add(Blob transition)
    {
        ArgumentNullException.ThrowIfNull(transition);

        _transitions.Add(transition);
    }

    /// <summary>Appends everything <paramref name="plan"/> applies to this plan.</summary>
    public void AppendPlan(HollowUpdatePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        _transitions.AddRange(plan._transitions);
    }

    /// <inheritdoc />
    public IEnumerator<Blob> GetEnumerator() => _transitions.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public override string ToString() =>
        string.Join(
            ", ",
            _transitions.Select(transition =>
                $"{transition.BlobType.GetPrefix()} to {transition.ToVersion.Invariant()}"));
}
