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

namespace Hollow.Tests.Core.Memory;

/// <summary>
/// Equality across bit sets built with different segment sizes, which Java refuses to answer and .NET
/// may not refuse.
/// </summary>
public sealed class ThreadSafeBitSetEqualityTests
{
    [Fact]
    public void BitSetsWithDifferentSegmentSizesAreUnequalRatherThanIncomparable()
    {
        ThreadSafeBitSet small = new(6);
        ThreadSafeBitSet large = new(10);

        small.Set(1);
        large.Set(1);

        // Java throws IllegalArgumentException here. Equals may not throw on .NET, so the same bits in
        // differently-sized segments are simply not equal.
        Assert.False(small.Equals(large));
        Assert.False(large.Equals(small));
        Assert.False(small.Equals((object)large));
    }

    [Fact]
    public void ACollectionLookupAgainstAMismatchedBitSetDoesNotThrow()
    {
        // This is what the throw used to break: every one of these routes through Equals.
        ThreadSafeBitSet small = new(6);
        ThreadSafeBitSet large = new(10);

        small.Set(3);
        large.Set(3);

        List<ThreadSafeBitSet> sets = [small];

        Assert.DoesNotContain(large, sets);
        Assert.Single(sets.Distinct());

        Dictionary<ThreadSafeBitSet, int> byBitSet = new() { [small] = 1 };

        Assert.False(byBitSet.TryGetValue(large, out _));
    }

    [Fact]
    public void TheSameSegmentSizeStillComparesByBits()
    {
        ThreadSafeBitSet one = new(6);
        ThreadSafeBitSet other = new(6);

        one.Set(2);
        other.Set(2);

        Assert.Equal(one, other);
        Assert.Equal(one.GetHashCode(), other.GetHashCode());

        other.Set(5);

        Assert.NotEqual(one, other);
    }
}
