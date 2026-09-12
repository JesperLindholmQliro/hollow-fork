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
using Hollow.Core.Read.Iterator;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Tests.Core.Index;

/// <summary>
/// Finding the records whose indexed string starts with a given prefix.
/// </summary>
/// <remarks>
/// The cases that matter are the shapes a field path can take to reach a string — straight through a
/// reference, inline on the record, through a chain of references, across a list, a set or a map — and
/// the difference between matching a prefix of an indexed key and matching a whole key.
/// </remarks>
public class PrefixIndexTests
{
    private sealed record SimpleMovie(int Id, string Name, int YearReleased);

    private sealed record InlineMovie(int Id, [property: HollowInline] string Name, int YearReleased);

    private sealed record Name(string N);

    private sealed record MovieWithReferenceName(int Id, Name Name, int YearReleased);

    private sealed record InlineName([property: HollowInline] string N);

    private sealed record MovieWithReferenceToInlineName(int Id, InlineName Name, int YearReleased);

    private sealed record Actor(string ActorName);

    private sealed record MovieWithActorList(int Id, string Title, List<Actor> Actors);

    private sealed record MovieWithNameSet(int Id, string Title, HashSet<string> Actors);

    private sealed record MovieWithActorMap(int Id, string Title, Dictionary<int, Actor> IdToActor);

    private static readonly (int Id, string Name, int Year)[] SimpleMovies =
    [
        (1, "The Matrix", 1999),
        (2, "Blood Diamond", 2006),
        (3, "Rush", 2013),
        (4, "Rocky", 1976),
        (5, "The Matrix Reloaded", 2003),
        (6, "The Matrix Resurrections", 2021),
    ];

    private static (HollowWriteStateEngine Write, HollowReadStateEngine Read, HollowObjectMapper Mapper)
        Populate(IEnumerable<object> records)
    {
        HollowWriteStateEngine writeEngine = new();
        HollowReadStateEngine readEngine = new();
        HollowObjectMapper mapper = new(writeEngine);

        foreach (object record in records)
        {
            mapper.Add(record);
        }

        StateEngineRoundTripper.RoundTripSnapshot(writeEngine, readEngine);

        return (writeEngine, readEngine, mapper);
    }

    private static HashSet<int> Ordinals(IHollowOrdinalIterator iterator) => [.. iterator.AsEnumerable()];

    /// <summary>Splits each key on whitespace, so a query matches a word anywhere in the title.</summary>
    private static IEnumerable<string> SplitOnWhitespace(IEnumerable<string> keys) =>
        keys.SelectMany(static key => key.Split(' '));

    /// <summary>
    /// The same six movies laid out four ways, to show that the path shape is what varies and the
    /// answers do not.
    /// </summary>
    [Theory]
    [InlineData("simple", "SimpleMovie", "Name")]
    [InlineData("inline", "InlineMovie", "Name")]
    [InlineData("reference", "MovieWithReferenceName", "Name.N.value")]
    [InlineData("referenceToInline", "MovieWithReferenceToInlineName", "Name.N")]
    public void EveryShapeOfPathToAStringIndexesTheSame(string shape, string type, string fieldPath)
    {
        IEnumerable<object> records = shape switch
        {
            "simple" => SimpleMovies.Select(object (m) => new SimpleMovie(m.Id, m.Name, m.Year)),
            "inline" => SimpleMovies.Select(object (m) => new InlineMovie(m.Id, m.Name, m.Year)),
            "reference" => SimpleMovies.Select(
                object (m) => new MovieWithReferenceName(m.Id, new Name(m.Name), m.Year)),
            _ => SimpleMovies.Select(
                object (m) => new MovieWithReferenceToInlineName(m.Id, new InlineName(m.Name), m.Year)),
        };

        (_, HollowReadStateEngine readEngine, _) = Populate(records);

        using HollowPrefixIndex index = new(readEngine, type, fieldPath);

        Assert.Equal(2, Ordinals(index.FindKeysWithPrefix("R")).Count);
        Assert.Equal(3, Ordinals(index.FindKeysWithPrefix("th")).Count);
        Assert.Equal(3, Ordinals(index.FindKeysWithPrefix("the")).Count);
        Assert.Single(Ordinals(index.FindKeysWithPrefix("blOO")));
        Assert.Empty(Ordinals(index.FindKeysWithPrefix("ttt")));
    }

    /// <summary>
    /// A path that stops at a reference is extended to the value behind it, so <c>Name</c> reaches the
    /// string rather than the <c>String</c> record's ordinal.
    /// </summary>
    [Fact]
    public void APathThatStopsAtAReferenceIsExtendedToTheValue()
    {
        (_, HollowReadStateEngine readEngine, _) = Populate(
            SimpleMovies.Select(object (m) => new MovieWithReferenceName(m.Id, new Name(m.Name), m.Year)));

        using HollowPrefixIndex index = new(readEngine, "MovieWithReferenceName", "Name.N");

        Assert.Equal(3, Ordinals(index.FindKeysWithPrefix("the")).Count);
    }

    [Fact]
    public void AnEmptyPrefixReachesEveryIndexedRecord()
    {
        (_, HollowReadStateEngine readEngine, _) = Populate(
            SimpleMovies.Select(object (m) => new SimpleMovie(m.Id, m.Name, m.Year)));

        using HollowPrefixIndex index = new(readEngine, "SimpleMovie", "Name");

        Assert.Equal(SimpleMovies.Length, Ordinals(index.FindKeysWithPrefix("")).Count);
    }

    /// <summary>
    /// Tokenizing on whitespace changes what a prefix reaches: a query matches a word anywhere in the
    /// title rather than only the start of it, and the whole title is no longer a key.
    /// </summary>
    [Fact]
    public void ATokenizerIndexesEachWordSeparately()
    {
        (_, HollowReadStateEngine readEngine, _) = Populate(
            SimpleMovies.Select(object (m) => new SimpleMovie(m.Id, m.Name, m.Year)));

        using HollowPrefixIndex index =
            new(readEngine, "SimpleMovie", "Name.value", tokenizer: SplitOnWhitespace);

        Assert.Equal(3, Ordinals(index.FindKeysWithPrefix("th")).Count);

        // "Matrix" is the second word of three titles, which an untokenized index would not find.
        Assert.Equal(3, Ordinals(index.FindKeysWithPrefix("matrix")).Count);
        Assert.Equal(2, Ordinals(index.FindKeysWithPrefix("re")).Count);

        // The whitespace itself is never part of a key now, so nothing starts with "the ".
        Assert.Empty(Ordinals(index.FindKeysWithPrefix("the ")));

        Assert.Equal(SimpleMovies.Length, Ordinals(index.FindKeysWithPrefix("")).Count);
    }

    /// <summary>
    /// The longest-match query answers a different question from the prefix query: it matches whole
    /// indexed keys against the start of the argument, not the other way round.
    /// </summary>
    [Fact]
    public void TheLongestMatchIsOfAWholeIndexedKey()
    {
        (_, HollowReadStateEngine readEngine, _) = Populate(
            SimpleMovies.Select(object (m) => new SimpleMovie(m.Id, m.Name, m.Year)));

        using HollowPrefixIndex index =
            new(readEngine, "SimpleMovie", "Name.value", tokenizer: SplitOnWhitespace);

        // "rush" is an indexed key, and so is a prefix of "rushing".
        Assert.NotEmpty(index.FindLongestMatch("rush"));
        Assert.NotEmpty(index.FindLongestMatch("rushing"));
        Assert.NotEmpty(index.FindLongestMatch("the"));

        // "resurrect" is a prefix of the indexed "resurrections", which is the wrong way round.
        Assert.Empty(index.FindLongestMatch("resurrect"));
        Assert.Empty(index.FindLongestMatch("doesnotexist"));

        // Neither an empty string nor null is ever indexed.
        Assert.Empty(index.FindLongestMatch(string.Empty));
        Assert.Empty(index.FindLongestMatch(null));
    }

    [Fact]
    public void TheIndexKnowsWhichKeysItHolds()
    {
        (_, HollowReadStateEngine readEngine, _) = Populate(
            SimpleMovies.Select(object (m) => new SimpleMovie(m.Id, m.Name, m.Year)));

        using HollowPrefixIndex index = new(readEngine, "SimpleMovie", "Name");

        Assert.True(index.Contains("the matrix"));
        Assert.True(index.Contains("rocky"));

        // A prefix of a key is not itself a key.
        Assert.False(index.Contains("the"));
        Assert.False(index.Contains("the matri"));
        Assert.False(index.Contains("nothing like it"));
    }

    [Fact]
    public void ACaseSensitiveIndexMatchesOnlyTheCasingThatWasIndexed()
    {
        (_, HollowReadStateEngine readEngine, _) = Populate(
            SimpleMovies.Select(object (m) => new SimpleMovie(m.Id, m.Name, m.Year)));

        using HollowPrefixIndex index = new(
            readEngine, "SimpleMovie", "Name.value", caseSensitive: true, tokenizer: SplitOnWhitespace);

        Assert.Empty(Ordinals(index.FindKeysWithPrefix("th")));
        Assert.Equal(3, Ordinals(index.FindKeysWithPrefix("Th")).Count);

        Assert.Empty(Ordinals(index.FindKeysWithPrefix("matrix")));
        Assert.Equal(3, Ordinals(index.FindKeysWithPrefix("Matrix")).Count);

        Assert.Empty(index.FindLongestMatch("rush"));
        Assert.NotEmpty(index.FindLongestMatch("Rush"));
    }

    [Theory]
    [InlineData("MovieWithActorList", "Actors.element.ActorName")]
    [InlineData("MovieWithActorList", "Actors.element.ActorName.value")]
    public void APathMayCrossAList(string type, string fieldPath)
    {
        (_, HollowReadStateEngine readEngine, _) = Populate(
        [
            new MovieWithActorList(
                1,
                "The Matrix",
                [new Actor("Keanu Reeves"), new Actor("Laurence Fishburne"), new Actor("Carrie-Anne Moss")]),
        ]);

        using HollowPrefixIndex index = new(readEngine, type, fieldPath);

        Assert.Single(Ordinals(index.FindKeysWithPrefix("kea")));
        Assert.Single(Ordinals(index.FindKeysWithPrefix("carr")));
        Assert.Empty(Ordinals(index.FindKeysWithPrefix("aaa")));
    }

    [Fact]
    public void APathMayCrossASet()
    {
        (_, HollowReadStateEngine readEngine, _) = Populate(
        [
            new MovieWithNameSet(
                1, "The Matrix", ["Keanu Reeves", "Laurence Fishburne", "Carrie-Anne Moss"]),
        ]);

        using HollowPrefixIndex index = new(readEngine, "MovieWithNameSet", "Actors.element");

        Assert.Single(Ordinals(index.FindKeysWithPrefix("kea")));
        Assert.Empty(Ordinals(index.FindKeysWithPrefix("aaa")));
    }

    [Fact]
    public void APathMayCrossAMapsValues()
    {
        (_, HollowReadStateEngine readEngine, _) = Populate(
        [
            new MovieWithActorMap(
                1,
                "The Matrix",
                new Dictionary<int, Actor>
                {
                    [1] = new Actor("Keanu Reeves"),
                    [2] = new Actor("Laurence Fishburne"),
                    [3] = new Actor("Carrie-Anne Moss"),
                }),
        ]);

        using HollowPrefixIndex index =
            new(readEngine, "MovieWithActorMap", "IdToActor.value.ActorName");

        Assert.Single(Ordinals(index.FindKeysWithPrefix("carr")));
        Assert.Empty(Ordinals(index.FindKeysWithPrefix("aaa")));
    }

    /// <summary>
    /// Several records sharing a key is the case the tree grows each node's ordinal store for, so it
    /// is the one where the growth has to preserve what was already there.
    /// </summary>
    [Fact]
    public void ManyRecordsCanShareOneKey()
    {
        const int movieCount = 10;

        (_, HollowReadStateEngine readEngine, _) = Populate(
        [
            .. Enumerable.Range(0, movieCount).Select(
                i => new MovieWithActorList(i, $"The Matrix {i}", [new Actor("Keanu Reeves")])),
        ]);

        using HollowPrefixIndex index =
            new(readEngine, "MovieWithActorList", "Actors.element.ActorName");

        Assert.Equal(movieCount, Ordinals(index.FindKeysWithPrefix("kea")).Count);
        Assert.Equal(movieCount, index.FindLongestMatch("keanu reeves").Count);

        // The node had to grow past the default of one ordinal to hold them.
        Assert.True(
            index.UsageStats().MaxValuesPerNode >= movieCount,
            $"expected room for {movieCount} ordinals, got {index.UsageStats().MaxValuesPerNode}");
    }

    /// <summary>
    /// Saying up front how many records share a key avoids the growth entirely, which is the whole
    /// reason the estimate is a constructor argument.
    /// </summary>
    [Fact]
    public void TheDuplicateEstimateAvoidsGrowingTheOrdinalStore()
    {
        (_, HollowReadStateEngine readEngine, _) = Populate(
        [
            .. Enumerable.Range(0, 10).Select(
                i => new MovieWithActorList(i, $"The Matrix {i}", [new Actor("Keanu Reeves")])),
        ]);

        using HollowPrefixIndex index = new(
            readEngine, "MovieWithActorList", "Actors.element.ActorName", estimatedMaxStringDuplicates: 10);

        Assert.Equal(10, Ordinals(index.FindKeysWithPrefix("kea")).Count);
        Assert.Equal(10, index.UsageStats().MaxValuesPerNode);
    }

    [Fact]
    public void AnIndexFollowsDeltas()
    {
        List<SimpleMovie> movies =
        [
            new(1, "007 James Bond", 1999),
            new(2, "龍爭虎鬥", 2006),
            new(3, "Rush", 2013),
            new(4, "Rocky", 1976),
            new(5, "The Matrix Reloaded", 2003),
            new(6, "The Matrix Resurrections", 2021),
        ];

        (HollowWriteStateEngine writeEngine, HollowReadStateEngine readEngine, HollowObjectMapper mapper) =
            Populate(movies);

        using HollowPrefixIndex index = new(readEngine, "SimpleMovie", "Name");
        index.ListenForDeltaUpdates();

        // Non-ASCII keys are code units like any other; nothing about the tree assumes Latin script.
        Assert.Equal(["龍爭虎鬥"], Titles(readEngine, index.FindKeysWithPrefix("龍")));
        Assert.Equal(["007 James Bond"], Titles(readEngine, index.FindKeysWithPrefix("00")));

        movies[3] = movies[3] with { Name = "Rocky 2" };
        movies.Add(new SimpleMovie(7, "As Good as It Gets", 1997));
        movies.Add(new SimpleMovie(8, "0 dark thirty", 1997));

        foreach (SimpleMovie movie in movies)
        {
            mapper.Add(movie);
        }

        StateEngineRoundTripper.RoundTripDelta(writeEngine, readEngine);

        Assert.Equal(["As Good as It Gets"], Titles(readEngine, index.FindKeysWithPrefix("as")));
        Assert.Equal(
            ["Rocky 2", "Rush"], Titles(readEngine, index.FindKeysWithPrefix("R")).OrderBy(t => t, StringComparer.Ordinal));
        Assert.Equal(["Rocky 2"], Titles(readEngine, index.FindKeysWithPrefix("rocky 2")));
        Assert.Equal(
            ["0 dark thirty", "007 James Bond"],
            Titles(readEngine, index.FindKeysWithPrefix("0")).OrderBy(t => t, StringComparer.Ordinal));

        // The old key is gone, not merely shadowed by the new one.
        Assert.False(index.Contains("rocky"));
        Assert.True(index.Contains("rocky 2"));

        index.DetachFromDeltaUpdates();
    }

    [Fact]
    public void DetachingStopsTheIndexFollowingDeltas()
    {
        List<SimpleMovie> movies = [.. SimpleMovies.Select(m => new SimpleMovie(m.Id, m.Name, m.Year))];

        (HollowWriteStateEngine writeEngine, HollowReadStateEngine readEngine, HollowObjectMapper mapper) =
            Populate(movies);

        using HollowPrefixIndex index = new(readEngine, "SimpleMovie", "Name");
        index.ListenForDeltaUpdates();
        index.DetachFromDeltaUpdates();

        foreach (SimpleMovie movie in movies)
        {
            mapper.Add(movie);
        }

        mapper.Add(new SimpleMovie(7, "Zodiac", 2007));
        StateEngineRoundTripper.RoundTripDelta(writeEngine, readEngine);

        Assert.Empty(Ordinals(index.FindKeysWithPrefix("zod")));
    }

    [Fact]
    public void AnEmptyTypeIndexesToAnEmptyTreeThatADeltaCanFill()
    {
        HollowWriteStateEngine writeEngine = new();
        HollowReadStateEngine readEngine = new();
        HollowObjectMapper mapper = new(writeEngine);
        mapper.InitializeTypeState(typeof(SimpleMovie));

        StateEngineRoundTripper.RoundTripSnapshot(writeEngine, readEngine);

        using HollowPrefixIndex index = new(readEngine, "SimpleMovie", "Name");
        index.ListenForDeltaUpdates();

        Assert.Empty(Ordinals(index.FindKeysWithPrefix("")));
        Assert.False(index.Contains("anything"));

        foreach ((int id, string name, int year) in SimpleMovies)
        {
            mapper.Add(new SimpleMovie(id, name, year));
        }

        StateEngineRoundTripper.RoundTripDelta(writeEngine, readEngine);

        Assert.Equal(3, Ordinals(index.FindKeysWithPrefix("the")).Count);
    }

    [Fact]
    public void APathThatDoesNotReachAStringIsRefused()
    {
        (_, HollowReadStateEngine readEngine, _) = Populate(
            SimpleMovies.Select(object (m) => new SimpleMovie(m.Id, m.Name, m.Year)));

        Assert.Throws<ArgumentException>(
            () => new HollowPrefixIndex(readEngine, "SimpleMovie", "Id"));
    }

    [Fact]
    public void AnUnbindablePathIsRefused()
    {
        (_, HollowReadStateEngine readEngine, _) = Populate(
            SimpleMovies.Select(
                object (m) => new MovieWithReferenceName(m.Id, new Name(m.Name), m.Year)));

        Assert.Throws<FieldPathException>(
            () => new HollowPrefixIndex(readEngine, "MovieWithReferenceName", "Name.noSuchField"));

        Assert.Throws<FieldPathException>(
            () => new HollowPrefixIndex(readEngine, "NoSuchType", "Name"));
    }

    /// <summary>The titles of the records a query returned.</summary>
    private static List<string> Titles(HollowReadStateEngine readEngine, IHollowOrdinalIterator iterator)
    {
        HollowObjectTypeReadState movies =
            (HollowObjectTypeReadState)readEngine.GetTypeState("SimpleMovie")!;
        HollowObjectTypeReadState strings =
            (HollowObjectTypeReadState)readEngine.GetTypeState("String")!;

        int namePosition = movies.Schema.GetPosition("Name");
        int valuePosition = strings.Schema.GetPosition("value");

        return
        [
            .. Ordinals(iterator)
                .Select(ordinal => strings.ReadString(movies.ReadOrdinal(ordinal, namePosition), valuePosition)!)
                .Order(StringComparer.Ordinal),
        ];
    }
}
