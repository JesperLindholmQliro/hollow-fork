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
/// The fixed-capacity ordinal map the history keeps one of per type per state.
/// </summary>
public class IntMapTests
{
    /// <summary>A key that was never put has no value, which is what -1 means here.</summary>
    [Fact]
    public void AKeyThatWasNeverPutReadsAsMinusOne()
    {
        IntMap map = new(16);

        Assert.Equal(-1, map.Get(0));
        Assert.Equal(-1, map.Get(12345));
        Assert.Equal(0, map.Count);
    }

    /// <summary>Every key put comes back, including the dense ascending runs ordinals actually are.</summary>
    [Fact]
    public void EveryKeyPutComesBack()
    {
        IntMap map = new(1000);

        for (int i = 0; i < 1000; i++)
        {
            map.Put(i, i * 7);
        }

        Assert.Equal(1000, map.Count);

        for (int i = 0; i < 1000; i++)
        {
            Assert.Equal(i * 7, map.Get(i));
        }
    }

    /// <summary>Putting a key twice replaces its value rather than adding an entry.</summary>
    [Fact]
    public void PuttingAKeyTwiceReplacesIt()
    {
        IntMap map = new(4);

        map.Put(3, 30);
        map.Put(3, 31);

        Assert.Equal(31, map.Get(3));
        Assert.Equal(1, map.Count);
    }

    /// <summary>
    /// The entries can be enumerated, which is how a state's whole mapping is copied out.
    /// </summary>
    [Fact]
    public void TheEntriesCanBeEnumerated()
    {
        IntMap map = new(8);

        for (int i = 0; i < 8; i++)
        {
            map.Put(i, i + 100);
        }

        Assert.Equal(
            Enumerable.Range(0, 8).Select(i => (i, i + 100)),
            map.Entries().OrderBy(entry => entry.Key));
    }

    /// <summary>
    /// A map sized for nothing still works, which is what a type with no unmatched records asks for.
    /// </summary>
    [Fact]
    public void AMapSizedForNothingStillReads()
    {
        IntMap map = new(0);

        Assert.Equal(-1, map.Get(0));
    }

    /// <summary>
    /// Overfilling is a sizing mistake, and saying so beats Java's behaviour of probing forever.
    /// </summary>
    [Fact]
    public void OverfillingThrowsRatherThanHanging()
    {
        IntMap map = new(0);

        // A map sized for nothing still has a bucket or two; fill past them.
        Assert.Throws<InvalidOperationException>(() =>
        {
            for (int i = 0; i < 64; i++)
            {
                map.Put(i, i);
            }
        });
    }

    /// <summary>
    /// -1 is what an empty bucket is marked with, so it cannot also be a key.
    /// </summary>
    [Fact]
    public void ANegativeKeyIsRefused()
    {
        IntMap map = new(4);

        Assert.Throws<ArgumentOutOfRangeException>(() => map.Put(-1, 0));
    }
}
