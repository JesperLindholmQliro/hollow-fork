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

using Hollow.Reference.Infrastructure.Adapters;

namespace Hollow.Reference.Tests;

/// <summary>
/// The index that tells a consumer which versions it can get a snapshot of.
/// </summary>
public sealed class SnapshotIndexTests
{
    [Fact]
    public void AListOfVersionsSurvivesBeingEncoded()
    {
        long[] versions = [20240101000000001, 20240101000010002, 20240102000000003, 20240301120000004];

        Assert.Equal(versions, SnapshotIndex.Decode(SnapshotIndex.Encode(versions)));
    }

    [Fact]
    public void GapEncodingIsWhatMakesTheIndexSmall()
    {
        // Versions are minted from the clock, so a day of ten-second cycles is 8,640 versions that
        // differ in their last few digits. Written out, each is eight bytes; gap-encoded, most are one.
        long[] versions = [.. Enumerable.Range(0, 1000).Select(i => 20240101000000000L + (i * 10))];

        byte[] encoded = SnapshotIndex.Encode(versions);

        Assert.Equal(versions, SnapshotIndex.Decode(encoded));
        Assert.True(
            encoded.Length < versions.Length * sizeof(long) / 4,
            $"{encoded.Length} bytes for {versions.Length} versions is not much of a saving");
    }

    [Fact]
    public void AProducerPublishingItsFirstSnapshotHasNothingToIndex()
    {
        // Java indexes an empty list and throws on the first element; the case is real rather than
        // defensive, because every producer starts with no snapshots.
        Assert.Empty(SnapshotIndex.Encode([]));
        Assert.Empty(SnapshotIndex.Decode([]));
    }

    [Fact]
    public void OneVersionEncodesAsItself()
    {
        Assert.Equal([20240101000000001], SnapshotIndex.Decode(SnapshotIndex.Encode([20240101000000001])));
    }

    [Fact]
    public void AnUnsortedListIsRefusedRatherThanEncodedAsNonsense()
    {
        Assert.Throws<ArgumentException>(() => SnapshotIndex.Encode([20240102000000001, 20240101000000001]));
    }

    [Fact]
    public void TheNearestSnapshotAtOrBelowAVersionIsWhatAConsumerGets()
    {
        byte[] encoded = SnapshotIndex.Encode([100, 200, 300]);

        Assert.Equal(200, SnapshotIndex.GreatestAtMost(encoded, 250));
        Assert.Equal(200, SnapshotIndex.GreatestAtMost(encoded, 200));
        Assert.Equal(300, SnapshotIndex.GreatestAtMost(encoded, 100_000));
        Assert.Equal(100, SnapshotIndex.GreatestAtMost(encoded, 100));
    }

    [Fact]
    public void AVersionOlderThanEverySnapshotCannotBeReached()
    {
        // Deltas only run forwards, so there is no snapshot to start from and nothing to answer.
        Assert.Null(SnapshotIndex.GreatestAtMost(SnapshotIndex.Encode([100, 200]), 50));
    }

    [Fact]
    public void AnEmptyIndexAnswersNothing()
    {
        Assert.Null(SnapshotIndex.GreatestAtMost([], 20240101000000001));
    }
}
