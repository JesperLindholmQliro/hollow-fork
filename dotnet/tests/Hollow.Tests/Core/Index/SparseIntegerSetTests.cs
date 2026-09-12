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

using Hollow.Core.Index;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Tests.Core.Index;

/// <summary>
/// A membership test over integers that are sparse: identifiers scattered across a wide range rather
/// than packed near zero.
/// </summary>
/// <remarks>
/// The point of the structure is that it costs roughly what the values need rather than what the range
/// is, so the tests here are as much about the storage as about the answers: a set holding a handful of
/// values including <see cref="int.MaxValue"/> must not have allocated anything like two billion bits.
/// </remarks>
public class SparseIntegerSetTests
{
    private sealed record Video(int Value);

    private sealed record Movie(Video Id, string Title, int ReleaseYear);

    private static readonly Movie[] Movies =
    [
        new(new Video(1), "The Matrix", 1999),
        new(new Video(2), "Blood Diamond", 2006),
        new(new Video(3), "Rush", 2013),
        new(new Video(4), "Rocky", 1976),
        new(new Video(40), "Inglourious Basterds", 2009),
        new(new Video(512), "Avatar", 2009),
        new(new Video(513), "Harry Potter and the Half-Blood Prince", 2009),
        new(new Video(0), "The Hangover", 2009),
        new(new Video(int.MaxValue), "Sherlock Holmes", 2009),
        new(new Video(28), "Up", 2009),
        new(new Video(30), "The Girl with the Dragon Tattoo", 2009),
        new(new Video(77), "District 9", 2009),
        new(new Video(66), "Law Abiding Citizen", 2009),
        new(new Video(55), "Moon", 2009),
    ];

    /// <summary>Indexes only the movies released in 2009, which is 10 of the 14.</summary>
    private static IndexPredicate ReleasedIn2009(HollowReadStateEngine readEngine)
    {
        HollowObjectTypeReadState movies =
            (HollowObjectTypeReadState)readEngine.GetTypeState("Movie")!;
        int yearPosition = movies.Schema.GetPosition("ReleaseYear");

        return ordinal => movies.ReadInt(ordinal, yearPosition) == 2009;
    }

    private static (HollowWriteStateEngine Write, HollowReadStateEngine Read, HollowObjectMapper Mapper)
        Populated(IEnumerable<Movie> movies)
    {
        HollowWriteStateEngine writeEngine = new();
        HollowReadStateEngine readEngine = new();
        HollowObjectMapper mapper = new(writeEngine);

        mapper.InitializeTypeState(typeof(Movie));

        foreach (Movie movie in movies)
        {
            mapper.Add(movie);
        }

        StateEngineRoundTripper.RoundTripSnapshot(writeEngine, readEngine);

        return (writeEngine, readEngine, mapper);
    }

    [Fact]
    public void TheSetHoldsExactlyTheValuesThePredicateSelected()
    {
        (_, HollowReadStateEngine readEngine, _) = Populated(Movies);

        HollowSparseIntegerSet set =
            new(readEngine, "Movie", "Id.Value", ReleasedIn2009(readEngine));

        // The 2009 releases are in.
        foreach (int id in new[] { 0, 28, 30, 40, 55, 66, 77, 512, 513, int.MaxValue })
        {
            Assert.True(set.Get(id), $"expected {id} to be indexed");
        }

        // The others are not, even though their records are in the dataset.
        foreach (int id in new[] { 1, 2, 3, 4 })
        {
            Assert.False(set.Get(id), $"expected {id} not to be indexed");
        }

        Assert.Equal(10, set.Cardinality());
    }

    [Fact]
    public void AValueOutsideTheSetIsSimplyAbsent()
    {
        (_, HollowReadStateEngine readEngine, _) = Populated(Movies);

        HollowSparseIntegerSet set =
            new(readEngine, "Movie", "Id.Value", ReleasedIn2009(readEngine));

        Assert.False(set.Get(-1));
        Assert.False(set.Get(514));
        Assert.False(set.Get(int.MaxValue - 1));
    }

    /// <summary>
    /// The whole reason for the structure: holding <see cref="int.MaxValue"/> must not cost anything
    /// like a bit set over the whole range would.
    /// </summary>
    [Fact]
    public void HoldingAVeryLargeValueCostsAlmostNothing()
    {
        (_, HollowReadStateEngine readEngine, _) = Populated(Movies);

        HollowSparseIntegerSet set =
            new(readEngine, "Movie", "Id.Value", ReleasedIn2009(readEngine));

        // A plain bit set over the same values would need int.MaxValue bits. The bucket directory has
        // to reach that far, but nothing beneath it does.
        long bitsUsed = set.EstimateBitsUsed();

        Assert.True(bitsUsed < int.MaxValue / 32, $"expected a compact set, got {bitsUsed} bits");
    }

    [Fact]
    public void AnEmptyTypeGivesAnEmptySetThatADeltaCanFill()
    {
        (HollowWriteStateEngine writeEngine, HollowReadStateEngine readEngine, HollowObjectMapper mapper) =
            Populated([]);

        HollowSparseIntegerSet set = new(readEngine, "Movie", "Id.Value", _ => true);
        Assert.Equal(0, set.Cardinality());

        set.ListenForDeltaUpdates();

        foreach (Movie movie in Movies)
        {
            mapper.Add(movie);
        }

        StateEngineRoundTripper.RoundTripDelta(writeEngine, readEngine);

        Assert.Equal(Movies.Length, set.Cardinality());
        Assert.True(set.Get(int.MaxValue));
    }

    [Fact]
    public void ADeltaDropsAValueWhoseRecordNoLongerQualifies()
    {
        (HollowWriteStateEngine writeEngine, HollowReadStateEngine readEngine, HollowObjectMapper mapper) =
            Populated(Movies);

        HollowSparseIntegerSet set =
            new(readEngine, "Movie", "Id.Value", ReleasedIn2009(readEngine));
        set.ListenForDeltaUpdates();

        Assert.True(set.Get(512));

        // Avatar moves out of 2009, so the predicate stops selecting it.
        foreach (Movie movie in Movies)
        {
            mapper.Add(movie.Id.Value == 512 ? movie with { ReleaseYear = 1999 } : movie);
        }

        StateEngineRoundTripper.RoundTripDelta(writeEngine, readEngine);

        Assert.False(set.Get(512));
        Assert.True(set.Get(513));
        Assert.Equal(9, set.Cardinality());
    }

    [Fact]
    public void ADeltaAddsAValueBeyondTheEndOfTheSet()
    {
        (HollowWriteStateEngine writeEngine, HollowReadStateEngine readEngine, HollowObjectMapper mapper) =
            Populated(Movies.Where(movie => movie.Id.Value < 100));

        HollowSparseIntegerSet set = new(readEngine, "Movie", "Id.Value", _ => true);
        set.ListenForDeltaUpdates();

        Assert.False(set.Get(70000));

        // Past the compacted set's last bucket, so it has to be widened rather than written into.
        foreach (Movie movie in Movies.Where(movie => movie.Id.Value < 100))
        {
            mapper.Add(movie);
        }

        mapper.Add(new Movie(new Video(70000), "Something Later", 2020));

        StateEngineRoundTripper.RoundTripDelta(writeEngine, readEngine);

        Assert.True(set.Get(70000));
        Assert.True(set.Get(1));
    }

    [Fact]
    public void DetachingStopsTheSetFollowingDeltas()
    {
        (HollowWriteStateEngine writeEngine, HollowReadStateEngine readEngine, HollowObjectMapper mapper) =
            Populated(Movies.Where(movie => movie.Id.Value < 100));

        HollowSparseIntegerSet set = new(readEngine, "Movie", "Id.Value", _ => true);
        set.ListenForDeltaUpdates();
        set.DetachFromDeltaUpdates();

        foreach (Movie movie in Movies.Where(movie => movie.Id.Value < 100))
        {
            mapper.Add(movie);
        }

        mapper.Add(new Movie(new Video(99), "Late Arrival", 2020));

        StateEngineRoundTripper.RoundTripDelta(writeEngine, readEngine);

        Assert.False(set.Get(99));
    }

    /// <summary>
    /// Zero is the marker the storage uses for an empty slot, so a set holding zero has to record it
    /// separately. Getting that wrong loses the value or, worse, reports every empty slot as holding it.
    /// </summary>
    [Fact]
    public void ZeroIsHeldLikeAnyOtherValue()
    {
        (_, HollowReadStateEngine readEngine, _) = Populated(Movies);

        HollowSparseIntegerSet set = new(readEngine, "Movie", "Id.Value", _ => true);

        Assert.True(set.Get(0));
        Assert.False(set.Get(5));
        Assert.Equal(Movies.Length, set.Cardinality());
    }

    /// <summary>
    /// Values close together share a 64-bit word, and values a few thousand apart share a bucket. Both
    /// are the cases where the offset arithmetic has to insert a word in the middle rather than append.
    /// </summary>
    [Theory]
    [InlineData(new[] { 0, 1, 2, 63, 64, 65 })]
    [InlineData(new[] { 4095, 4096, 4097, 8191, 8192 })]
    [InlineData(new[] { 500, 100, 300, 200, 400 })]
    [InlineData(new[] { 1_000_000, 7, 65_536, 4_095, 12 })]
    public void ValuesAreFoundWhateverOrderTheyLandIn(int[] values)
    {
        (_, HollowReadStateEngine readEngine, _) =
            Populated([.. values.Select((value, i) => new Movie(new Video(value), $"movie-{i}", 2000))]);

        HollowSparseIntegerSet set = new(readEngine, "Movie", "Id.Value", _ => true);

        foreach (int value in values)
        {
            Assert.True(set.Get(value), $"expected {value} to be indexed");
        }

        Assert.Equal(values.Length, set.Cardinality());

        // And nothing else within the range is.
        foreach (int value in Enumerable.Range(0, 200).Where(value => !values.Contains(value)))
        {
            Assert.False(set.Get(value), $"expected {value} not to be indexed");
        }
    }
}
