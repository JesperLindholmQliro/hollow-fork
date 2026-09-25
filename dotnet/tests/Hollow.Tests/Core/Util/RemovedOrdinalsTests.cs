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

using Hollow.Core.Util;

namespace Hollow.Tests.Core.Util;

/// <summary>
/// The walk over what a transition removed, and — flipped — over what it added.
/// </summary>
/// <remarks>
/// Ported from <c>RemovedOrdinalIteratorTest</c>. Java's cursor is a sequence here, so the extra cases
/// below are about what that changes: enumerating twice walks twice, and counting does not consume.
/// </remarks>
public sealed class RemovedOrdinalsTests
{
    private static BitSet Previous => BitSetOf(1, 2, 3, 4, 6, 7, 9, 10);

    private static BitSet Current => BitSetOf(1, 3, 4, 5, 7, 8, 9);

    [Fact]
    public void WhatWasThereAndIsNotAnyMore()
    {
        Assert.Equal([2, 6, 10], new RemovedOrdinals(Previous, Current));
    }

    [Fact]
    public void FlippedItIsWhatWasNotThereAndNowIs()
    {
        Assert.Equal([5, 8], new RemovedOrdinals(Previous, Current, flip: true));
    }

    [Fact]
    public void NothingRemovedIsAnEmptyWalkRatherThanAFailedOne()
    {
        Assert.Empty(new RemovedOrdinals(Previous, Previous));
        Assert.Empty(new RemovedOrdinals(BitSetOf(), BitSetOf(1, 2, 3)));
    }

    [Fact]
    public void EverythingRemovedIsEveryOrdinal()
    {
        Assert.Equal([1, 2, 3, 4, 6, 7, 9, 10], new RemovedOrdinals(Previous, BitSetOf()));
    }

    [Fact]
    public void WalkingItTwiceWalksItTwice()
    {
        // Java's cursor is consumed and has a reset() to start again; a sequence starts again by being
        // enumerated again, and Count does not consume it.
        RemovedOrdinals removed = new(Previous, Current);

        Assert.Equal(3, removed.Count());
        Assert.Equal([2, 6, 10], removed);
        Assert.Equal(3, removed.Count());
    }

    [Fact]
    public void TheEndOfTheWalkIsFixedWhenItIsMadeRatherThanWhenItRuns()
    {
        // The two bit sets belong to a listener that the next transition writes to, so a walk built now
        // and enumerated later must not wander into ordinals that were set in between. Java takes the
        // length at construction; this keeps that.
        BitSet previous = BitSetOf(1, 2, 3);
        BitSet current = BitSetOf(1, 3);

        RemovedOrdinals removed = new(previous, current);

        previous.Set(100);

        Assert.Equal([2], removed);
    }

    private static BitSet BitSetOf(params int[] setBits)
    {
        BitSet bitSet = new(setBits.Length == 0 ? 1 : setBits.Max() + 1);

        foreach (int bit in setBits)
        {
            bitSet.Set(bit);
        }

        return bitSet;
    }
}
