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
using Hollow.Core.Read.Engine.List;
using Hollow.Core.Read.Engine.Map;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Read.Engine.Set;
using Hollow.Core.Read.Iterator;
using Hollow.Core.Schema;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Tests.Core;

/// <summary>
/// Exercises the object mapper end to end: a POCO goes in, a blob comes out, and the read state engine
/// has to yield the same values back.
/// </summary>
public class ObjectMapperTests
{
    private sealed class Movie
    {
        public int Id { get; set; }

        public string? Title { get; set; }

        public long BoxOffice { get; set; }

        public bool Released { get; set; }

        [HollowInline]
        public string? InlineTag { get; set; }

        [HollowTransient]
        public string? Ignored { get; set; }
    }

    private sealed class Country
    {
        public string? Name { get; set; }
    }

    private sealed class MovieWithCountry
    {
        public string? Title { get; set; }

        public Country? Country { get; set; }
    }

    private sealed class Catalogue
    {
        public string? Name { get; set; }

        public List<string> Tags { get; set; } = [];

        public HashSet<int> Years { get; set; } = [];

        public Dictionary<string, int> RatingsByTitle { get; set; } = [];
    }

    [HollowTypeName("Film")]
    private sealed class RenamedMovie
    {
        public int Id { get; set; }
    }

    [HollowPrimaryKey("id")]
    private sealed class KeyedMovie
    {
        public int Id { get; set; }
    }

    private enum Genre
    {
        Drama,
        Comedy,
    }

    private sealed class MovieWithGenre
    {
        public string? Title { get; set; }

        public Genre Genre { get; set; }
    }

    private sealed class TreeNode
    {
        public string? Label { get; set; }

        public List<TreeNode> Children { get; set; } = [];
    }

    private static HollowReadStateEngine RoundTrip(HollowWriteStateEngine writeEngine)
    {
        using MemoryStream stream = new();
        new HollowBlobWriter(writeEngine).WriteSnapshot(stream);

        stream.Position = 0;
        HollowReadStateEngine readEngine = new();
        new HollowBlobReader(readEngine).ReadSnapshot(stream);

        return readEngine;
    }

    /// <summary>
    /// Reads the single string a wrapper record holds, following the reference from
    /// <paramref name="fromState"/>.
    /// </summary>
    private static string? FollowStringReference(
        HollowReadStateEngine engine, HollowObjectTypeReadState fromState, int ordinal, string fieldName)
    {
        int referenced = fromState.ReadOrdinal(ordinal, fromState.Schema.GetPosition(fieldName));
        if (referenced == -1)
        {
            return null;
        }

        HollowObjectTypeReadState stringState =
            Assert.IsType<HollowObjectTypeReadState>(engine.GetTypeState("String"));
        return stringState.ReadString(referenced, stringState.Schema.GetPosition("value"));
    }

    [Fact]
    public void ScalarsAndStringsRoundTripThroughTheMapper()
    {
        HollowWriteStateEngine engine = new();
        HollowObjectMapper mapper = new(engine);

        int ordinal = mapper.Add(new Movie
        {
            Id = 42,
            Title = "The Matrix",
            BoxOffice = 463_517_383L,
            Released = true,
            InlineTag = "inline",
            Ignored = "should not appear",
        });

        HollowReadStateEngine readEngine = RoundTrip(engine);
        HollowObjectTypeReadState movies =
            Assert.IsType<HollowObjectTypeReadState>(readEngine.GetTypeState("Movie"));

        // The transient member must be absent from the schema entirely.
        Assert.Equal(-1, movies.Schema.GetPosition("Ignored"));

        // Non-nullable primitives are inlined; string is a reference unless marked inline.
        Assert.Equal(FieldType.Int, movies.Schema.GetFieldType("Id"));
        Assert.Equal(FieldType.Long, movies.Schema.GetFieldType("BoxOffice"));
        Assert.Equal(FieldType.Boolean, movies.Schema.GetFieldType("Released"));
        Assert.Equal(FieldType.Reference, movies.Schema.GetFieldType("Title"));
        Assert.Equal(FieldType.String, movies.Schema.GetFieldType("InlineTag"));

        Assert.Equal(42, movies.ReadInt(ordinal, movies.Schema.GetPosition("Id")));
        Assert.Equal(463_517_383L, movies.ReadLong(ordinal, movies.Schema.GetPosition("BoxOffice")));
        Assert.True(movies.ReadBoolean(ordinal, movies.Schema.GetPosition("Released")));
        Assert.Equal("inline", movies.ReadString(ordinal, movies.Schema.GetPosition("InlineTag")));
        Assert.Equal("The Matrix", FollowStringReference(readEngine, movies, ordinal, "Title"));
    }

    [Fact]
    public void NullMembersAreLeftUnset()
    {
        HollowWriteStateEngine engine = new();
        HollowObjectMapper mapper = new(engine);

        int ordinal = mapper.Add(new Movie { Id = 1, Title = null, InlineTag = null });

        HollowReadStateEngine readEngine = RoundTrip(engine);
        HollowObjectTypeReadState movies =
            Assert.IsType<HollowObjectTypeReadState>(readEngine.GetTypeState("Movie"));

        Assert.True(movies.IsNull(ordinal, movies.Schema.GetPosition("Title")));
        Assert.True(movies.IsNull(ordinal, movies.Schema.GetPosition("InlineTag")));
        Assert.Equal(1, movies.ReadInt(ordinal, movies.Schema.GetPosition("Id")));
    }

    [Fact]
    public void NestedObjectsBecomeReferencedTypes()
    {
        HollowWriteStateEngine engine = new();
        HollowObjectMapper mapper = new(engine);

        int ordinal = mapper.Add(new MovieWithCountry
        {
            Title = "Persona",
            Country = new Country { Name = "Sweden" },
        });

        HollowReadStateEngine readEngine = RoundTrip(engine);

        HollowObjectTypeReadState movies =
            Assert.IsType<HollowObjectTypeReadState>(readEngine.GetTypeState("MovieWithCountry"));
        HollowObjectTypeReadState countries =
            Assert.IsType<HollowObjectTypeReadState>(readEngine.GetTypeState("Country"));

        int countryOrdinal = movies.ReadOrdinal(ordinal, movies.Schema.GetPosition("Country"));
        Assert.Equal("Sweden", FollowStringReference(readEngine, countries, countryOrdinal, "Name"));
    }

    [Fact]
    public void CollectionsBecomeListSetAndMapTypes()
    {
        HollowWriteStateEngine engine = new();
        HollowObjectMapper mapper = new(engine);

        int ordinal = mapper.Add(new Catalogue
        {
            Name = "Criterion",
            Tags = ["classic", "restored"],
            Years = [1957, 1966],
            RatingsByTitle = new Dictionary<string, int> { ["Persona"] = 9, ["Wild Strawberries"] = 8 },
        });

        HollowReadStateEngine readEngine = RoundTrip(engine);

        HollowObjectTypeReadState catalogues =
            Assert.IsType<HollowObjectTypeReadState>(readEngine.GetTypeState("Catalogue"));
        HollowListTypeReadState tags =
            Assert.IsType<HollowListTypeReadState>(readEngine.GetTypeState("ListOfString"));
        HollowSetTypeReadState years =
            Assert.IsType<HollowSetTypeReadState>(readEngine.GetTypeState("SetOfInteger"));
        HollowMapTypeReadState ratings =
            Assert.IsType<HollowMapTypeReadState>(readEngine.GetTypeState("MapOfStringToInteger"));

        HollowObjectTypeReadState strings =
            Assert.IsType<HollowObjectTypeReadState>(readEngine.GetTypeState("String"));
        HollowObjectTypeReadState integers =
            Assert.IsType<HollowObjectTypeReadState>(readEngine.GetTypeState("Integer"));

        int stringValue = strings.Schema.GetPosition("value");
        int intValue = integers.Schema.GetPosition("value");

        int tagsOrdinal = catalogues.ReadOrdinal(ordinal, catalogues.Schema.GetPosition("Tags"));
        Assert.Equal(
            ["classic", "restored"],
            tags.OrdinalIterator(tagsOrdinal).AsEnumerable().Select(o => strings.ReadString(o, stringValue)));

        int yearsOrdinal = catalogues.ReadOrdinal(ordinal, catalogues.Schema.GetPosition("Years"));
        Assert.Equal(
            [1957, 1966],
            years.OrdinalIterator(yearsOrdinal).AsEnumerable().Select(o => integers.ReadInt(o, intValue)).Order());

        int ratingsOrdinal = catalogues.ReadOrdinal(ordinal, catalogues.Schema.GetPosition("RatingsByTitle"));
        Dictionary<string, int> readRatings = [];
        IHollowMapEntryOrdinalIterator iterator = ratings.OrdinalIterator(ratingsOrdinal);
        while (iterator.Next())
        {
            readRatings[strings.ReadString(iterator.Key, stringValue)!] = integers.ReadInt(iterator.Value, intValue);
        }

        Assert.Equal(9, readRatings["Persona"]);
        Assert.Equal(8, readRatings["Wild Strawberries"]);
        Assert.Equal(2, readRatings.Count);
    }

    [Fact]
    public void RepeatedValuesDeduplicateThroughReferencedTypes()
    {
        HollowWriteStateEngine engine = new();
        HollowObjectMapper mapper = new(engine);

        // Two movies with the same title: the title should be stored once.
        mapper.Add(new MovieWithCountry { Title = "Persona", Country = new Country { Name = "Sweden" } });
        mapper.Add(new MovieWithCountry { Title = "Persona", Country = new Country { Name = "Sweden" } });

        HollowReadStateEngine readEngine = RoundTrip(engine);

        // "Persona" and "Sweden" are two distinct strings, each stored once.
        Assert.Equal(2, readEngine.GetTypeState("String")!.PopulatedOrdinals.Cardinality());
        Assert.Equal(1, readEngine.GetTypeState("Country")!.PopulatedOrdinals.Cardinality());
        Assert.Equal(1, readEngine.GetTypeState("MovieWithCountry")!.PopulatedOrdinals.Cardinality());
    }

    [Fact]
    public void TypeNameAttributeRenamesTheType()
    {
        HollowWriteStateEngine engine = new();
        HollowObjectMapper mapper = new(engine);
        mapper.Add(new RenamedMovie { Id = 1 });

        HollowReadStateEngine readEngine = RoundTrip(engine);

        Assert.NotNull(readEngine.GetTypeState("Film"));
        Assert.Null(readEngine.GetTypeState("RenamedMovie"));
    }

    [Fact]
    public void PrimaryKeyAttributeReachesTheSchema()
    {
        HollowWriteStateEngine engine = new();
        HollowObjectMapper mapper = new(engine);
        mapper.Add(new KeyedMovie { Id = 1 });

        HollowObjectTypeReadState movies =
            Assert.IsType<HollowObjectTypeReadState>(RoundTrip(engine).GetTypeState("KeyedMovie"));

        Assert.NotNull(movies.Schema.PrimaryKey);
        Assert.Equal(["id"], movies.Schema.PrimaryKey!.FieldPaths);
    }

    [Fact]
    public void EnumsAreStoredByName()
    {
        HollowWriteStateEngine engine = new();
        HollowObjectMapper mapper = new(engine);
        int ordinal = mapper.Add(new MovieWithGenre { Title = "Some Like It Hot", Genre = Genre.Comedy });

        HollowReadStateEngine readEngine = RoundTrip(engine);
        HollowObjectTypeReadState movies =
            Assert.IsType<HollowObjectTypeReadState>(readEngine.GetTypeState("MovieWithGenre"));
        HollowObjectTypeReadState genres =
            Assert.IsType<HollowObjectTypeReadState>(readEngine.GetTypeState("Genre"));

        int genreOrdinal = movies.ReadOrdinal(ordinal, movies.Schema.GetPosition("Genre"));
        Assert.Equal("Comedy", genres.ReadString(genreOrdinal, genres.Schema.GetPosition("_name")));
    }

    [Fact]
    public void InitializeTypeStateRegistersSchemasWithoutRecords()
    {
        HollowWriteStateEngine engine = new();
        HollowObjectMapper mapper = new(engine);
        mapper.InitializeTypeState<MovieWithCountry>();

        HollowReadStateEngine readEngine = RoundTrip(engine);

        Assert.NotNull(readEngine.GetTypeState("MovieWithCountry"));
        Assert.NotNull(readEngine.GetTypeState("Country"));
        Assert.NotNull(readEngine.GetTypeState("String"));
        Assert.Equal(0, readEngine.GetTypeState("MovieWithCountry")!.PopulatedOrdinals.Cardinality());
    }

    /// <summary>
    /// A type that references itself must not send the mapper into infinite recursion while it is
    /// building schemas.
    /// </summary>
    [Fact]
    public void SelfReferencingTypesMap()
    {
        HollowWriteStateEngine engine = new();
        HollowObjectMapper mapper = new(engine);

        TreeNode root = new()
        {
            Label = "root",
            Children =
            [
                new TreeNode { Label = "left" },
                new TreeNode { Label = "right", Children = [new TreeNode { Label = "leaf" }] },
            ],
        };

        int ordinal = mapper.Add(root);

        HollowReadStateEngine readEngine = RoundTrip(engine);

        HollowObjectTypeReadState nodes =
            Assert.IsType<HollowObjectTypeReadState>(readEngine.GetTypeState("TreeNode"));
        HollowListTypeReadState children =
            Assert.IsType<HollowListTypeReadState>(readEngine.GetTypeState("ListOfTreeNode"));

        Assert.Equal("root", FollowStringReference(readEngine, nodes, ordinal, "Label"));

        int childrenOrdinal = nodes.ReadOrdinal(ordinal, nodes.Schema.GetPosition("Children"));
        int[] childOrdinals = [.. children.OrdinalIterator(childrenOrdinal).AsEnumerable()];

        Assert.Equal(2, childOrdinals.Length);
        Assert.Equal(
            ["left", "right"],
            childOrdinals.Select(o => FollowStringReference(readEngine, nodes, o, "Label")));

        // And the grandchild, one level deeper.
        int rightChildren = nodes.ReadOrdinal(childOrdinals[1], nodes.Schema.GetPosition("Children"));
        int leaf = Assert.Single(children.OrdinalIterator(rightChildren).AsEnumerable());
        Assert.Equal("leaf", FollowStringReference(readEngine, nodes, leaf, "Label"));
    }

    [Fact]
    public void NullsInsideACollectionAreRejected()
    {
        HollowWriteStateEngine engine = new();
        HollowObjectMapper mapper = new(engine);

        Catalogue catalogue = new() { Name = "x", Tags = [null!] };

        Assert.Throws<HollowMappingException>(() => mapper.Add(catalogue));
    }
}
