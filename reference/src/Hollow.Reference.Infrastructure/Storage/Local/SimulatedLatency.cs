/*
 *  Copyright 2016 Netflix, Inc.
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

namespace Hollow.Reference.Infrastructure.Storage.Local;

/// <summary>
/// Makes the local mode wait before it answers, so that a directory behaves a little like a bucket.
/// </summary>
/// <remarks>
/// <para>
/// A fixed cost per operation stands in for the round trip, and a cost per megabyte for the bandwidth.
/// Neither is a measurement of anything; the point is only that reads and writes take long enough for
/// the ordering problems of a real deployment to be reachable on one machine — a consumer refreshing
/// while the producer is mid-cycle, a watcher firing before the blob it refers to has landed.
/// </para>
/// <para>
/// A <see cref="SimulatedLatency"/> holds no state, so one instance serves every caller.
/// </para>
/// </remarks>
public sealed class SimulatedLatency
{
    private const double BytesPerMegabyte = 1024 * 1024;

    /// <summary>A latency that costs nothing, for tests and for anyone who would rather it did not.</summary>
    public static SimulatedLatency None { get; } = new(TimeSpan.Zero, TimeSpan.Zero);

    private readonly TimeSpan _perOperation;
    private readonly TimeSpan _perMegabyte;

    public SimulatedLatency(TimeSpan perOperation, TimeSpan perMegabyte)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(perOperation, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(perMegabyte, TimeSpan.Zero);

        _perOperation = perOperation;
        _perMegabyte = perMegabyte;
    }

    public SimulatedLatency(LatencyOptions options)
        : this(
            (options ?? throw new ArgumentNullException(nameof(options))).PerOperation,
            options.PerMegabyte)
    {
    }

    /// <summary>Whether this latency would ever delay anything.</summary>
    public bool IsEnabled => _perOperation > TimeSpan.Zero || _perMegabyte > TimeSpan.Zero;

    /// <summary>Waits for as long as moving <paramref name="bytes"/> is being pretended to take.</summary>
    public Task DelayAsync(long bytes = 0, CancellationToken cancellationToken = default)
    {
        TimeSpan delay = Compute(bytes);

        return delay > TimeSpan.Zero
            ? Task.Delay(delay, cancellationToken)
            : Task.CompletedTask;
    }

    /// <summary>How long <see cref="DelayAsync"/> would wait for <paramref name="bytes"/>.</summary>
    public TimeSpan Compute(long bytes)
    {
        if (bytes <= 0)
        {
            return _perOperation;
        }

        return _perOperation + _perMegabyte * (bytes / BytesPerMegabyte);
    }
}
