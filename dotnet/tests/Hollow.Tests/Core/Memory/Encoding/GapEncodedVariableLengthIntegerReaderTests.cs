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

using Hollow.Core.Memory;
using Hollow.Core.Memory.Encoding;
using Hollow.Core.Memory.Pool;

namespace Hollow.Tests.Core.Memory.Encoding;

/// <summary>
/// The gap-encoded ordinal sequence a delta carries its removals and additions in.
/// </summary>
/// <remarks>
/// Ported from <c>GapEncodedVariableLengthIntegerReaderTest</c>. The split and join cases matter more
/// here than anywhere else: they are what resharding moves a type's removals through, and getting the
/// ordinal arithmetic wrong there produces a delta that removes the wrong records rather than one that
/// fails to parse.
/// </remarks>
public sealed class GapEncodedVariableLengthIntegerReaderTests
{
    [Fact]
    public void TheValuesComeBackOutAndComeBackOutAgainAfterAReset()
    {
        GapEncodedVariableLengthIntegerReader reader = Reader(1, 10, 100, 105, 107, 200);

        AssertValues(reader, 1, 10, 100, 105, 107, 200);

        reader.Reset();

        AssertValues(reader, 1, 10, 100, 105, 107, 200);
    }

    [Fact]
    public void ASequenceWithNoBytesIsEmptyAndOneWithAnOrdinalIsNot()
    {
        Assert.False(Reader(20).IsEmpty);
        Assert.True(Reader().IsEmpty);
        Assert.True(GapEncodedVariableLengthIntegerReader.EmptyReader.IsEmpty);
    }

    [Fact]
    public void CountingTheRemainingOrdinalsConsumesTheReader()
    {
        GapEncodedVariableLengthIntegerReader reader = Reader(1, 10, 100, 105, 107, 200);

        Assert.Equal(6, reader.RemainingElements());
        Assert.Equal(0, reader.RemainingElements());

        reader.Reset();

        Assert.Equal(6, reader.RemainingElements());
    }

    // ── Splitting, which is what a growing shard count does to a delta's removals ────────────────

    [Fact]
    public void SplittingSendsEachOrdinalToTheShardItWillLandIn()
    {
        GapEncodedVariableLengthIntegerReader reader = Reader(1, 10, 100, 105, 107, 200);

        GapEncodedVariableLengthIntegerReader[] splitByTwo = reader.Split(2);

        Assert.Equal(2, splitByTwo.Length);

        // The original ordinal is split[i] * numSplits + i, so 10, 100 and 200 are the even ones and
        // 1, 105 and 107 the odd.
        AssertValues(splitByTwo[0], 5, 50, 100);
        AssertValues(splitByTwo[1], 0, 52, 53);
    }

    [Fact]
    public void SplittingFarEnoughLeavesMostOfTheShardsEmpty()
    {
        GapEncodedVariableLengthIntegerReader reader = Reader(1, 10, 100, 105, 107, 200);

        GapEncodedVariableLengthIntegerReader[] split = reader.Split(256);

        Assert.Equal(256, split.Length);

        AssertValues(split[1], 0);
        AssertValues(split[200], 0);
        Assert.Same(GapEncodedVariableLengthIntegerReader.EmptyReader, split[0]);
        Assert.Same(GapEncodedVariableLengthIntegerReader.EmptyReader, split[255]);
    }

    [Fact]
    public void SplittingNothingGivesNothingBackAPieceAtATime()
    {
        GapEncodedVariableLengthIntegerReader[] split =
            GapEncodedVariableLengthIntegerReader.EmptyReader.Split(2);

        Assert.Equal(2, split.Length);
        Assert.Same(GapEncodedVariableLengthIntegerReader.EmptyReader, split[0]);
        Assert.Same(GapEncodedVariableLengthIntegerReader.EmptyReader, split[1]);
    }

    [Fact]
    public void ASplitThatIsNotAPowerOfTwoIsRefused()
    {
        // Java throws IllegalStateException; an argument that is not one of the values the method
        // accepts is an ArgumentOutOfRangeException here.
        GapEncodedVariableLengthIntegerReader reader = Reader(1, 10, 100);

        Assert.Throws<ArgumentOutOfRangeException>(() => reader.Split(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.Split(3));
    }

    // ── Joining, which is what a shrinking shard count does ──────────────────────────────────────

    [Fact]
    public void JoiningInterleavesTheOrdinalsBackTogether()
    {
        GapEncodedVariableLengthIntegerReader[] from =
        [
            Reader(1, 10, 100, 105, 107, 200),
            Reader(5, 76, 100, 102, 109, 200, 201),
        ];

        // Ordinal o of source i becomes o * n + i, so 1 from source 0 is 2 and 5 from source 1 is 11.
        AssertValues(
            GapEncodedVariableLengthIntegerReader.Join(from),
            2, 11, 20, 153, 200, 201, 205, 210, 214, 219, 400, 401, 403);
    }

    [Fact]
    public void JoiningTakesMissingAndEmptySourcesInItsStride()
    {
        GapEncodedVariableLengthIntegerReader[] from =
        [
            Reader(0, 1, 2, 3),
            null!,
            Reader(3),
            GapEncodedVariableLengthIntegerReader.EmptyReader,
        ];

        // The lone 3 in source 2 becomes 3 * 4 + 2 = 14.
        AssertValues(GapEncodedVariableLengthIntegerReader.Join(from), 0, 4, 8, 12, 14);
    }

    [Fact]
    public void JoiningNothingToNothingIsNothing()
    {
        Assert.Same(
            GapEncodedVariableLengthIntegerReader.EmptyReader,
            GapEncodedVariableLengthIntegerReader.Join(
                [GapEncodedVariableLengthIntegerReader.EmptyReader,
                 GapEncodedVariableLengthIntegerReader.EmptyReader]));

        Assert.Same(
            GapEncodedVariableLengthIntegerReader.EmptyReader,
            GapEncodedVariableLengthIntegerReader.Join([null, null]));
    }

    [Fact]
    public void JoiningOneSourceLeavesItAlone()
    {
        AssertValues(
            GapEncodedVariableLengthIntegerReader.Join([Reader(1, 10, 100, 105, 107, 200)]),
            1, 10, 100, 105, 107, 200);
    }

    [Fact]
    public void JoiningNothingAtAllIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => GapEncodedVariableLengthIntegerReader.Join(null!));
        Assert.Throws<ArgumentException>(() => GapEncodedVariableLengthIntegerReader.Join([]));
    }

    /// <summary>Builds a reader over <paramref name="values"/>, which have to be ascending.</summary>
    private static GapEncodedVariableLengthIntegerReader Reader(params int[] values)
    {
        ByteDataArray buffer = new(WastefulRecycler.SmallArrayRecycler);
        int previous = 0;

        foreach (int value in values)
        {
            VarInt.WriteVInt(buffer, value - previous);
            previous = value;
        }

        return new GapEncodedVariableLengthIntegerReader(buffer.UnderlyingArray, (int)buffer.Length);
    }

    /// <summary>Walks <paramref name="reader"/> to its end, which has to be exactly where it is said to be.</summary>
    private static void AssertValues(
        GapEncodedVariableLengthIntegerReader reader, params int[] expected)
    {
        foreach (int value in expected)
        {
            Assert.Equal(value, reader.NextElement());
            reader.Advance();
        }

        Assert.Equal(int.MaxValue, reader.NextElement());
    }
}
