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

using System.Diagnostics;
using Hollow.Reference.Infrastructure;
using Hollow.Reference.Infrastructure.Storage.Local;

namespace Hollow.Reference.Tests;

/// <summary>The pretend network that keeps the local mode from being unrealistically quick.</summary>
public sealed class SimulatedLatencyTests
{
    [Fact]
    public void ASmallReadCostsTheRoundTripAndLittleElse()
    {
        SimulatedLatency latency = new(
            TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(40));

        Assert.Equal(TimeSpan.FromMilliseconds(25), latency.Compute(0));
        Assert.Equal(TimeSpan.FromMilliseconds(25), latency.Compute(-1));
    }

    [Fact]
    public void ALargeReadCostsTheBandwidthToo()
    {
        SimulatedLatency latency = new(
            TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(40));

        Assert.Equal(TimeSpan.FromMilliseconds(25 + 40), latency.Compute(1024 * 1024));
        Assert.Equal(TimeSpan.FromMilliseconds(25 + 400), latency.Compute(10 * 1024 * 1024));
    }

    [Fact]
    public async Task NoneCostsNothingAtAll()
    {
        Assert.False(SimulatedLatency.None.IsEnabled);

        Stopwatch elapsed = Stopwatch.StartNew();
        await SimulatedLatency.None.DelayAsync(100 * 1024 * 1024, Token);

        // Not a timing assertion so much as a promise that the tests are not paying for this.
        Assert.True(elapsed.Elapsed < TimeSpan.FromMilliseconds(100), $"waited {elapsed.Elapsed}");
    }

    [Fact]
    public async Task AConfiguredDelayIsActuallyWaitedFor()
    {
        SimulatedLatency latency = new(TimeSpan.FromMilliseconds(50), TimeSpan.Zero);

        Stopwatch elapsed = Stopwatch.StartNew();
        await latency.DelayAsync(cancellationToken: Token);

        Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(40), $"waited only {elapsed.Elapsed}");
    }

    [Fact]
    public void ItCanBeBuiltFromTheConfiguredOptions()
    {
        SimulatedLatency latency = new(new LatencyOptions
        {
            PerOperation = TimeSpan.FromMilliseconds(5),
            PerMegabyte = TimeSpan.Zero,
        });

        Assert.True(latency.IsEnabled);
        Assert.Equal(TimeSpan.FromMilliseconds(5), latency.Compute(0));
    }

    [Fact]
    public void ANegativeDelayIsRefusedRatherThanIgnored()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SimulatedLatency(TimeSpan.FromMilliseconds(-1), TimeSpan.Zero));
    }
}
