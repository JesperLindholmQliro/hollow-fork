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

using Hollow.Api.Producer;
using Hollow.Core.Read.Engine;
using BlobType = Hollow.Api.Consumer.BlobType;

namespace Hollow.Api.Metrics;

/// <summary>
/// How a consumer's refreshes have gone, and what the state it holds costs.
/// </summary>
public class HollowConsumerMetrics : HollowMetrics
{
    private long _lastRefreshStartTicks;
    private long _lastRefreshEndTicks;
    private int _refreshesSucceeded;
    private int _refreshesFailed;

    /// <summary>How many refreshes have completed.</summary>
    public int RefreshesSucceeded => Volatile.Read(ref _refreshesSucceeded);

    /// <summary>How many refreshes have thrown.</summary>
    public int RefreshesFailed => Volatile.Read(ref _refreshesFailed);

    /// <summary>
    /// When the last refresh started, as <see cref="System.Diagnostics.Stopwatch"/> ticks.
    /// </summary>
    /// <remarks>
    /// Java holds these as <c>AtomicLong</c> and hands the boxes themselves to callers, so anything
    /// holding one can set it. They are values here, and the atomicity that actually mattered — a
    /// torn 64-bit read on a 32-bit runtime — is kept with <see cref="Interlocked"/>.
    /// </remarks>
    public long LastRefreshStartTicks
    {
        get => Interlocked.Read(ref _lastRefreshStartTicks);
        set => Interlocked.Exchange(ref _lastRefreshStartTicks, value);
    }

    /// <summary>
    /// When the last refresh finished, as <see cref="System.Diagnostics.Stopwatch"/> ticks.
    /// </summary>
    public long LastRefreshEndTicks
    {
        get => Interlocked.Read(ref _lastRefreshEndTicks);
        set => Interlocked.Exchange(ref _lastRefreshEndTicks, value);
    }

    /// <summary>Records a refresh that completed, and what the state it produced costs.</summary>
    public void UpdateTypeStateMetrics(HollowReadStateEngine stateEngine, long version)
    {
        Interlocked.Increment(ref _refreshesSucceeded);
        Update(stateEngine, version);
    }

    /// <summary>Records a refresh that threw.</summary>
    public void UpdateRefreshFailed() => Interlocked.Increment(ref _refreshesFailed);
}

/// <summary>
/// How a producer's cycles and publishes have gone, and what the state it produced costs.
/// </summary>
/// <remarks>
/// Snapshots may be published on another thread while a cycle runs, so every counter here is
/// interlocked. Java guards only the two snapshot counters, and races on the rest — which is
/// survivable for a metric, but there is no reason to reproduce it when the fix is free.
/// </remarks>
public class HollowProducerMetrics : HollowMetrics
{
    private int _cyclesCompleted;
    private int _cyclesSucceeded;
    private int _cyclesFailed;
    private int _snapshotsCompleted;
    private int _snapshotsFailed;
    private int _deltasCompleted;
    private int _deltasFailed;
    private int _reverseDeltasCompleted;
    private int _reverseDeltasFailed;

    /// <summary>How many cycles have run, whether or not they succeeded.</summary>
    public int CyclesCompleted => Volatile.Read(ref _cyclesCompleted);

    /// <summary>How many cycles have succeeded.</summary>
    public int CyclesSucceeded => Volatile.Read(ref _cyclesSucceeded);

    /// <summary>How many cycles have failed.</summary>
    public int CyclesFailed => Volatile.Read(ref _cyclesFailed);

    /// <summary>How many snapshots have been published.</summary>
    public int SnapshotsCompleted => Volatile.Read(ref _snapshotsCompleted);

    /// <summary>How many snapshot publishes have failed.</summary>
    public int SnapshotsFailed => Volatile.Read(ref _snapshotsFailed);

    /// <summary>How many deltas have been published.</summary>
    public int DeltasCompleted => Volatile.Read(ref _deltasCompleted);

    /// <summary>How many delta publishes have failed.</summary>
    public int DeltasFailed => Volatile.Read(ref _deltasFailed);

    /// <summary>How many reverse deltas have been published.</summary>
    public int ReverseDeltasCompleted => Volatile.Read(ref _reverseDeltasCompleted);

    /// <summary>How many reverse delta publishes have failed.</summary>
    public int ReverseDeltasFailed => Volatile.Read(ref _reverseDeltasFailed);

    /// <summary>
    /// Records a cycle that has ended, and what the state it produced costs.
    /// </summary>
    /// <param name="status">How the cycle turned out.</param>
    /// <param name="readState">
    /// The state the cycle produced, or <see langword="null"/> where it produced none — a cycle that
    /// found nothing to change ends without one.
    /// </param>
    /// <param name="version">The version the cycle ran under.</param>
    public void UpdateCycleMetrics(Status status, IReadState? readState, long version)
    {
        ArgumentNullException.ThrowIfNull(status);

        Interlocked.Increment(ref _cyclesCompleted);

        if (status.Type == StatusType.Fail)
        {
            Interlocked.Increment(ref _cyclesFailed);

            return;
        }

        Interlocked.Increment(ref _cyclesSucceeded);

        if (readState is null)
        {
            Update(version);
        }
        else
        {
            Update(readState.StateEngine, version);
        }
    }

    /// <summary>Records a publish that has ended.</summary>
    /// <param name="status">How the publish turned out.</param>
    /// <param name="blob">The blob that was published.</param>
    public void UpdateBlobTypeMetrics(Status status, Blob blob)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(blob);

        bool succeeded = status.Type == StatusType.Success;

        switch (blob.BlobType)
        {
            case BlobType.Snapshot when succeeded:
                Interlocked.Increment(ref _snapshotsCompleted);
                break;

            case BlobType.Snapshot:
                Interlocked.Increment(ref _snapshotsFailed);
                break;

            case BlobType.Delta when succeeded:
                Interlocked.Increment(ref _deltasCompleted);
                break;

            case BlobType.Delta:
                Interlocked.Increment(ref _deltasFailed);
                break;

            case BlobType.ReverseDelta when succeeded:
                Interlocked.Increment(ref _reverseDeltasCompleted);
                break;

            case BlobType.ReverseDelta:
                Interlocked.Increment(ref _reverseDeltasFailed);
                break;

            default:
                throw new ArgumentException($"unknown blob type {blob.BlobType}", nameof(blob));
        }
    }
}
