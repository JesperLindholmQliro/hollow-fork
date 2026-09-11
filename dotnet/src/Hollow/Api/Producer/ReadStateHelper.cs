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

using Hollow.Core;
using Hollow.Core.Read.Engine;

namespace Hollow.Api.Producer;

/// <summary>
/// A version and the read state engine holding it.
/// </summary>
internal sealed class ReadState(long version, HollowReadStateEngine stateEngine) : IReadState
{
    /// <inheritdoc />
    public long Version { get; } = version;

    /// <inheritdoc />
    public HollowReadStateEngine StateEngine { get; } = stateEngine;
}

/// <summary>
/// The producer's current read state and the pending one a cycle is building, and the transitions
/// between them.
/// </summary>
/// <remarks>
/// <para>
/// A producer keeps a read state of its own so that it can check the blobs it just wrote, validate the
/// data a consumer would see, and give a populator access to the previous cycle. The integrity check
/// moves data between the two engines as it applies deltas, which is why swapping them is a distinct
/// operation from committing.
/// </para>
/// <para>
/// Every method returns a new instance; none mutates.
/// </para>
/// </remarks>
internal sealed class ReadStateHelper
{
    private ReadStateHelper(IReadState? current, IReadState? pending)
    {
        Current = current;
        Pending = pending;
    }

    /// <summary>The state the last successful cycle produced.</summary>
    internal IReadState? Current { get; }

    /// <summary>The state the cycle in flight is building.</summary>
    internal IReadState? Pending { get; }

    /// <summary>Whether a previous cycle has succeeded.</summary>
    internal bool HasCurrent => Current is not null;

    /// <summary>The version being built, or none when no cycle is in flight.</summary>
    internal long PendingVersion => Pending?.Version ?? HollowConstants.VersionNone;

    /// <summary>A producer starting a delta chain from nothing.</summary>
    internal static ReadStateHelper NewDeltaChain() => new(current: null, pending: null);

    /// <summary>A producer continuing the delta chain <paramref name="state"/> belongs to.</summary>
    internal static ReadStateHelper Restored(IReadState state) => new(state, pending: null);

    /// <summary>
    /// Begins a cycle producing <paramref name="version"/>, into a fresh read state engine.
    /// </summary>
    internal ReadStateHelper RoundTrip(long version)
    {
        if (Pending is not null)
        {
            throw new InvalidOperationException("A cycle is already in flight.");
        }

        return new ReadStateHelper(Current, new ReadState(version, new HollowReadStateEngine()));
    }

    /// <summary>
    /// Exchanges the two state engines while keeping each version where it belongs.
    /// </summary>
    /// <remarks>
    /// The integrity check applies the forward delta to the current engine, which leaves that engine
    /// holding the pending version's data and vice versa. Swapping fixes the labels rather than moving
    /// the data back, which is the cheaper half of the same result.
    /// </remarks>
    internal ReadStateHelper Swap() =>
        new(new ReadState(Current!.Version, Pending!.StateEngine), new ReadState(Pending.Version, Current.StateEngine));

    /// <summary>Accepts the pending state as the current one.</summary>
    internal ReadStateHelper Commit()
    {
        if (Pending is null)
        {
            throw new InvalidOperationException("No cycle is in flight.");
        }

        return new ReadStateHelper(Pending, pending: null);
    }

    /// <summary>
    /// Abandons the pending state, keeping the current version but reusing the pending engine — which
    /// the caller has just wound back to the current version with a reverse delta.
    /// </summary>
    internal ReadStateHelper Rollback()
    {
        if (Pending is null)
        {
            throw new InvalidOperationException("No cycle is in flight.");
        }

        return new ReadStateHelper(new ReadState(Current!.Version, Pending.StateEngine), pending: null);
    }
}
