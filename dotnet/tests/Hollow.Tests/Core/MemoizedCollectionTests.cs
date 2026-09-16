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

using Hollow.Core.Read.Engine;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Tests.Core;

/// <summary>
/// Collections that remember where they were written.
/// </summary>
/// <remarks>
/// The write engine already deduplicates identical records, so memoization changes no output — only
/// what it costs to arrive at it. What the tests can see is that the remembered ordinal is the right
/// one, and that it is forgotten when the cycle it belonged to ends.
/// </remarks>
public class MemoizedCollectionTests
{
    [Fact]
    public void TheSameInstanceIsWrittenOnceAndRemembered()
    {
        HollowWriteStateEngine engine = new();
        HollowObjectMapper mapper = new(engine);

        MemoizedList<string> cast = ["Pacino", "De Niro"];

        mapper.Add(new Episode(1, cast));
        mapper.Add(new Episode(2, cast));

        Assert.NotEqual(MemoizedRecord.Unassigned, cast.AssignedOrdinal);

        // The remembered ordinal is the one the list actually occupies.
        HollowReadStateEngine read = Publish(engine);

        Assert.Equal(1, read.GetTypeState("ListOfString")!.PopulatedOrdinals.Cardinality());
        Assert.Equal(
            (int)(cast.AssignedOrdinal & int.MaxValue),
            read.GetTypeState("ListOfString")!.PopulatedOrdinals.NextSetBit(0));
    }

    [Fact]
    public void MemoizingChangesNothingAboutWhatIsWritten()
    {
        MemoizedList<string> shared = ["Pacino", "De Niro"];

        HollowReadStateEngine memoized = Write(new Episode(1, shared), new Episode(2, shared));

        HollowReadStateEngine plain = Write(
            new Episode(1, ["Pacino", "De Niro"]), new Episode(2, ["Pacino", "De Niro"]));

        // Deduplication already made these the same; memoizing only saves the work of discovering it.
        Assert.Equal(
            plain.GetTypeState("ListOfString")!.PopulatedOrdinals.Cardinality(),
            memoized.GetTypeState("ListOfString")!.PopulatedOrdinals.Cardinality());

        Assert.Equal(
            plain.GetTypeState("Episode")!.PopulatedOrdinals.Cardinality(),
            memoized.GetTypeState("Episode")!.PopulatedOrdinals.Cardinality());
    }

    [Fact]
    public void AnOrdinalRememberedInAnEarlierCycleIsNotTrusted()
    {
        HollowWriteStateEngine engine = new();
        HollowObjectMapper mapper = new(engine);

        MemoizedList<string> cast = ["Pacino"];

        mapper.Add(new Episode(1, cast));

        long remembered = cast.AssignedOrdinal;

        engine.PrepareForWrite();
        engine.PrepareForNextCycle();

        mapper.Add(new Episode(1, cast));

        // A new cycle mints a new randomized tag, so the cycle bits differ and the old ordinal is
        // ignored rather than reused against a state where it may mean nothing.
        Assert.NotEqual(remembered, cast.AssignedOrdinal);
        Assert.NotEqual(
            remembered & MemoizedRecord.AssignedOrdinalCycleMask,
            cast.AssignedOrdinal & MemoizedRecord.AssignedOrdinalCycleMask);
    }

    [Fact]
    public void ASetAndAMapAreMemoizedToo()
    {
        HollowWriteStateEngine engine = new();
        HollowObjectMapper mapper = new(engine);

        MemoizedSet<string> genres = ["crime"];
        MemoizedMap<string, string> ratings = new() { ["imdb"] = "8.2" };

        mapper.Add(new Series(1, genres, ratings));
        mapper.Add(new Series(2, genres, ratings));

        Assert.NotEqual(MemoizedRecord.Unassigned, genres.AssignedOrdinal);
        Assert.NotEqual(MemoizedRecord.Unassigned, ratings.AssignedOrdinal);

        HollowReadStateEngine read = Publish(engine);

        Assert.Equal(1, read.GetTypeState("SetOfString")!.PopulatedOrdinals.Cardinality());
        Assert.Equal(1, read.GetTypeState("MapOfStringToString")!.PopulatedOrdinals.Cardinality());
    }

    [Fact]
    public void APlainCollectionIsUntouched()
    {
        HollowWriteStateEngine engine = new();
        HollowObjectMapper mapper = new(engine);

        List<string> cast = ["Pacino"];

        mapper.Add(new Episode(1, cast));

        // Nothing to remember on a type that does not implement the interface, and nothing breaks.
        Assert.Equal(1, Publish(engine).GetTypeState("ListOfString")!.PopulatedOrdinals.Cardinality());
    }

    [Fact]
    public void AModelCanMemoizeACollectionTypeOfItsOwn()
    {
        HollowWriteStateEngine engine = new();
        HollowObjectMapper mapper = new(engine);

        // Java type-tests for its own three subclasses, so a model's own collection can never be
        // memoized. Here it is an interface, so it can.
        OwnList cast = ["Pacino"];

        mapper.Add(new Episode(1, cast));

        Assert.NotEqual(MemoizedRecord.Unassigned, cast.AssignedOrdinal);
    }

    private static HollowReadStateEngine Write(params object[] records)
    {
        HollowWriteStateEngine engine = new();
        HollowObjectMapper mapper = new(engine);

        foreach (object record in records)
        {
            mapper.Add(record);
        }

        return Publish(engine);
    }

    private static HollowReadStateEngine Publish(HollowWriteStateEngine engine)
    {
        engine.PrepareForWrite();

        return StateEngineRoundTripper.RoundTripSnapshot(engine);
    }

    private sealed class OwnList : List<string>, IMemoizedRecord
    {
        public long AssignedOrdinal { get; set; } = MemoizedRecord.Unassigned;
    }

    [HollowPrimaryKey("Id")]
    private sealed record Episode(int Id, List<string> Cast);

    [HollowPrimaryKey("Id")]
    private sealed record Series(int Id, HashSet<string> Genres, Dictionary<string, string> Ratings);
}
