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

using Hollow.Core;
using Hollow.Core.Index.Key;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.Map;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Read.Engine.Set;
using Hollow.Core.Read.Iterator;
using Hollow.Core.Schema;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Tests.Core;

/// <summary>
/// Declaring a hash key on a mapped set or map is what lets a consumer find an element by its values
/// rather than by the ordinal it happens to have been assigned.
/// </summary>
public class ObjectMapperHashKeyTests
{
    [HollowPrimaryKey("Id")]
    private sealed class Movie
    {
        public int Id { get; set; }

        public string? Title { get; set; }
    }

    /// <summary>Two fields and no primary key, so nothing can be derived from it.</summary>
    private sealed class Pair
    {
        public int Left { get; set; }

        public int Right { get; set; }
    }

    private sealed class Catalogue
    {
        public HashSet<int> Years { get; set; } = [];

        public Dictionary<string, int> RatingsByTitle { get; set; } = [];

        public HashSet<Movie> Movies { get; set; } = [];

        public HashSet<Pair> Pairs { get; set; } = [];
    }

    private sealed class DeclaredKeys
    {
        [HollowHashKey("Title")]
        public HashSet<Movie> ByTitle { get; set; } = [];

        [HollowHashKey]
        public HashSet<int> Unkeyed { get; set; } = [];

        [HollowHashKey("Left")]
        public Dictionary<Pair, int> ByLeft { get; set; } = [];
    }

    private static HollowReadStateEngine RoundTrip(HollowWriteStateEngine engine)
    {
        using MemoryStream stream = new();
        new HollowBlobWriter(engine).WriteSnapshot(stream);
        stream.Position = 0;

        HollowReadStateEngine readEngine = new();
        new HollowBlobReader(readEngine).ReadSnapshot(stream);
        return readEngine;
    }

    private static (HollowReadStateEngine Consumer, int Ordinal) Map(
        object value, bool useDefaultHashKeys = true)
    {
        HollowWriteStateEngine engine = new();
        HollowObjectMapper mapper = new(engine) { UseDefaultHashKeys = useDefaultHashKeys };

        int ordinal = mapper.Add(value);
        return (RoundTrip(engine), ordinal);
    }

    private static int Referenced(HollowReadStateEngine consumer, string typeName, int ordinal, string fieldName)
    {
        HollowObjectTypeReadState state =
            Assert.IsType<HollowObjectTypeReadState>(consumer.GetTypeState(typeName));

        return state.ReadOrdinal(ordinal, state.Schema.GetPosition(fieldName));
    }

    private static HollowSetTypeReadState Sets(HollowReadStateEngine consumer, string typeName) =>
        Assert.IsType<HollowSetTypeReadState>(consumer.GetTypeState(typeName));

    private static HollowMapTypeReadState Maps(HollowReadStateEngine consumer, string typeName) =>
        Assert.IsType<HollowMapTypeReadState>(consumer.GetTypeState(typeName));

    /// <summary>
    /// A set of a scalar wrapper takes that wrapper's single field as its key, so the elements can be
    /// looked up by the value they hold.
    /// </summary>
    [Fact]
    public void ASetOfScalarsIsKeyedByItsValue()
    {
        (HollowReadStateEngine consumer, int ordinal) = Map(new Catalogue { Years = [1957, 1966, 1982] });

        HollowSetTypeReadState years = Sets(consumer, "SetOfInteger");
        Assert.Equal(new PrimaryKey("Integer", "value"), years.Schema.HashKey);

        int yearsOrdinal = Referenced(consumer, "Catalogue", ordinal, "Years");

        Assert.NotEqual(HollowConstants.OrdinalNone, years.FindElement(yearsOrdinal, 1957));
        Assert.NotEqual(HollowConstants.OrdinalNone, years.FindElement(yearsOrdinal, 1966));
        Assert.Equal(HollowConstants.OrdinalNone, years.FindElement(yearsOrdinal, 1900));
    }

    [Fact]
    public void AMapIsKeyedByItsKeyTypesValue()
    {
        (HollowReadStateEngine consumer, int ordinal) = Map(new Catalogue
        {
            RatingsByTitle = new Dictionary<string, int> { ["Persona"] = 9, ["Wild Strawberries"] = 8 },
        });

        HollowMapTypeReadState ratings = Maps(consumer, "MapOfStringToInteger");
        Assert.Equal(new PrimaryKey("String", "value"), ratings.Schema.HashKey);

        int ratingsOrdinal = Referenced(consumer, "Catalogue", ordinal, "RatingsByTitle");

        HollowObjectTypeReadState integers =
            Assert.IsType<HollowObjectTypeReadState>(consumer.GetTypeState("Integer"));
        int intValue = integers.Schema.GetPosition("value");

        Assert.Equal(9, integers.ReadInt(ratings.FindValue(ratingsOrdinal, "Persona"), intValue));
        Assert.Equal(8, integers.ReadInt(ratings.FindValue(ratingsOrdinal, "Wild Strawberries"), intValue));
        Assert.Equal(HollowConstants.OrdinalNone, ratings.FindKey(ratingsOrdinal, "Autumn Sonata"));
    }

    /// <summary>
    /// An element type that declares a primary key uses it, which is the case the derivation exists
    /// for: the key identifies the record, so it is the natural thing to hash by.
    /// </summary>
    [Fact]
    public void AnElementTypesPrimaryKeyBecomesTheHashKey()
    {
        (HollowReadStateEngine consumer, int ordinal) = Map(new Catalogue
        {
            Movies = [new Movie { Id = 1, Title = "Persona" }, new Movie { Id = 2, Title = "Sweden" }],
        });

        HollowSetTypeReadState movies = Sets(consumer, "SetOfMovie");
        Assert.Equal(new PrimaryKey("Movie", "Id"), movies.Schema.HashKey);

        int moviesOrdinal = Referenced(consumer, "Catalogue", ordinal, "Movies");

        HollowObjectTypeReadState movieRecords =
            Assert.IsType<HollowObjectTypeReadState>(consumer.GetTypeState("Movie"));
        int titleField = movieRecords.Schema.GetPosition("Title");

        int found = movies.FindElement(moviesOrdinal, 1);
        Assert.NotEqual(HollowConstants.OrdinalNone, found);
        Assert.Equal("Persona", ReadTitle(consumer, movieRecords, found, titleField));

        Assert.Equal(HollowConstants.OrdinalNone, movies.FindElement(moviesOrdinal, 99));
    }

    /// <summary>
    /// Nothing can be derived from a type with several fields and no primary key, so it hashes by
    /// ordinal as before.
    /// </summary>
    [Fact]
    public void AnElementTypeWithNothingToDeriveFromGetsNoHashKey()
    {
        (HollowReadStateEngine consumer, int ordinal) = Map(new Catalogue
        {
            Pairs = [new Pair { Left = 1, Right = 2 }],
        });

        HollowSetTypeReadState pairs = Sets(consumer, "SetOfPair");
        Assert.Null(pairs.Schema.HashKey);
        Assert.Null(pairs.KeyDeriver);

        // Ordinal-based lookup still works, because that is how the buckets were laid out.
        int pairsOrdinal = Referenced(consumer, "Catalogue", ordinal, "Pairs");
        int element = Assert.Single(pairs.ElementOrdinals(pairsOrdinal).AsEnumerable());
        Assert.True(pairs.Contains(pairsOrdinal, element));
    }

    [Fact]
    public void ADeclaredHashKeyOverridesTheDerivedOne()
    {
        (HollowReadStateEngine consumer, int ordinal) = Map(new DeclaredKeys
        {
            ByTitle = [new Movie { Id = 1, Title = "Persona" }, new Movie { Id = 2, Title = "Sweden" }],
        });

        HollowSetTypeReadState byTitle = Sets(consumer, "SetOfMovie");

        // Movie declares a primary key of Id, but the member asked for Title.
        Assert.Equal(new PrimaryKey("Movie", "Title"), byTitle.Schema.HashKey);

        int setOrdinal = Referenced(consumer, "DeclaredKeys", ordinal, "ByTitle");

        Assert.NotEqual(HollowConstants.OrdinalNone, byTitle.FindElement(setOrdinal, "Persona"));
        Assert.NotEqual(HollowConstants.OrdinalNone, byTitle.FindElement(setOrdinal, "Sweden"));

        // The derived key is not in effect, so looking up by Id finds nothing -- and would in any case
        // be the wrong shape for a String-typed key.
        Assert.Equal(HollowConstants.OrdinalNone, byTitle.FindElement(setOrdinal, "Autumn Sonata"));
    }

    /// <summary>
    /// A path naming a reference is expanded to the value behind it, so <c>Title</c> means the string
    /// rather than the record holding it.
    /// </summary>
    [Fact]
    public void ADeclaredHashKeyPathIsAutoExpanded()
    {
        (HollowReadStateEngine consumer, _) = Map(new DeclaredKeys
        {
            ByTitle = [new Movie { Id = 1, Title = "Persona" }],
        });

        HollowSetTypeReadState byTitle = Sets(consumer, "SetOfMovie");

        Assert.Equal(
            [FieldType.String],
            Assert.IsType<HollowPrimaryKeyValueDeriver>(byTitle.KeyDeriver).FieldTypes);
    }

    /// <summary>
    /// Declaring the attribute with no paths is the way to say "hash by ordinal" for a type that would
    /// otherwise have a key derived for it.
    /// </summary>
    [Fact]
    public void AnEmptyDeclaredHashKeySuppressesTheDerivedOne()
    {
        (HollowReadStateEngine consumer, int ordinal) = Map(new DeclaredKeys { Unkeyed = [1957, 1966] });

        HollowSetTypeReadState unkeyed = Sets(consumer, "SetOfInteger");
        Assert.Null(unkeyed.Schema.HashKey);

        int setOrdinal = Referenced(consumer, "DeclaredKeys", ordinal, "Unkeyed");

        foreach (int element in unkeyed.ElementOrdinals(setOrdinal).AsEnumerable())
        {
            Assert.True(unkeyed.Contains(setOrdinal, element));
        }
    }

    [Fact]
    public void AMapCanDeclareAHashKeyOverItsKeyType()
    {
        (HollowReadStateEngine consumer, int ordinal) = Map(new DeclaredKeys
        {
            ByLeft = new Dictionary<Pair, int>
            {
                [new Pair { Left = 1, Right = 2 }] = 10,
                [new Pair { Left = 3, Right = 4 }] = 20,
            },
        });

        HollowMapTypeReadState byLeft = Maps(consumer, "MapOfPairToInteger");
        Assert.Equal(new PrimaryKey("Pair", "Left"), byLeft.Schema.HashKey);

        int mapOrdinal = Referenced(consumer, "DeclaredKeys", ordinal, "ByLeft");

        HollowObjectTypeReadState integers =
            Assert.IsType<HollowObjectTypeReadState>(consumer.GetTypeState("Integer"));
        int intValue = integers.Schema.GetPosition("value");

        Assert.Equal(10, integers.ReadInt(byLeft.FindValue(mapOrdinal, 1), intValue));
        Assert.Equal(20, integers.ReadInt(byLeft.FindValue(mapOrdinal, 3), intValue));
        Assert.Equal(HollowConstants.OrdinalNone, byLeft.FindKey(mapOrdinal, 5));
    }

    /// <summary>
    /// Turning the derivation off restores the pre-hash-key behaviour for a whole model at once, which
    /// is what a caller wants if they rely on ordinal-based lookup.
    /// </summary>
    [Fact]
    public void DerivationCanBeTurnedOffForTheWholeModel()
    {
        (HollowReadStateEngine consumer, int ordinal) = Map(
            new Catalogue
            {
                Years = [1957, 1966],
                RatingsByTitle = new Dictionary<string, int> { ["Persona"] = 9 },
                Movies = [new Movie { Id = 1, Title = "Persona" }],
            },
            useDefaultHashKeys: false);

        Assert.Null(Sets(consumer, "SetOfInteger").Schema.HashKey);
        Assert.Null(Sets(consumer, "SetOfMovie").Schema.HashKey);
        Assert.Null(Maps(consumer, "MapOfStringToInteger").Schema.HashKey);

        int yearsOrdinal = Referenced(consumer, "Catalogue", ordinal, "Years");
        HollowSetTypeReadState years = Sets(consumer, "SetOfInteger");

        foreach (int element in years.ElementOrdinals(yearsOrdinal).AsEnumerable())
        {
            Assert.True(years.Contains(yearsOrdinal, element));
        }
    }

    private sealed class ConflictingKeys
    {
        [HollowHashKey("Id")]
        public HashSet<Movie> ById { get; set; } = [];

        [HollowHashKey("Title")]
        public HashSet<Movie> ByTitle { get; set; } = [];
    }

    /// <summary>
    /// Both members map to the type name <c>SetOfMovie</c> but want its records hashed differently.
    /// Java would silently keep whichever was registered first; saying so is more useful.
    /// </summary>
    [Fact]
    public void TwoHashKeysForOneTypeNameAreRejected()
    {
        HollowWriteStateEngine engine = new();
        HollowObjectMapper mapper = new(engine);

        HollowMappingException e = Assert.Throws<HollowMappingException>(
            () => mapper.Add(new ConflictingKeys()));

        Assert.Contains("SetOfMovie", e.Message, StringComparison.Ordinal);
        Assert.Contains("HollowTypeName", e.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A hash key has to survive into the blob for a consumer to use it, and a set written with one has
    /// to still contain exactly the elements that went in.
    /// </summary>
    [Fact]
    public void AKeyedSetStillHoldsEveryElement()
    {
        int[] years = [.. Enumerable.Range(1950, 60)];

        (HollowReadStateEngine consumer, int ordinal) = Map(new Catalogue { Years = [.. years] });

        HollowSetTypeReadState set = Sets(consumer, "SetOfInteger");
        int setOrdinal = Referenced(consumer, "Catalogue", ordinal, "Years");

        Assert.Equal(years.Length, set.Size(setOrdinal));

        HollowObjectTypeReadState integers =
            Assert.IsType<HollowObjectTypeReadState>(consumer.GetTypeState("Integer"));
        int intValue = integers.Schema.GetPosition("value");

        Assert.Equal(
            years,
            set.ElementOrdinals(setOrdinal).AsEnumerable().Select(o => integers.ReadInt(o, intValue)).Order());

        foreach (int year in years)
        {
            Assert.Equal(
                year,
                integers.ReadInt(set.FindElement(setOrdinal, year), intValue));
        }
    }

    private static string? ReadTitle(
        HollowReadStateEngine consumer, HollowObjectTypeReadState movies, int ordinal, int titleField)
    {
        HollowObjectTypeReadState strings =
            Assert.IsType<HollowObjectTypeReadState>(consumer.GetTypeState("String"));

        return strings.ReadString(
            movies.ReadOrdinal(ordinal, titleField), strings.Schema.GetPosition("value"));
    }
}
