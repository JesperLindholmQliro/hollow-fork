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
/// Exercises <c>HollowCollectionTypeName</c> and <c>HollowMapTypeName</c>: naming the type a
/// collection's elements, keys or values are stored as, rather than sharing the dataset-wide one.
/// </summary>
/// <remarks>
/// Java ships the two annotations with no tests at all. These are written from their documentation,
/// which states the point plainly: a <c>List&lt;Integer&gt;</c> otherwise puts its elements in the
/// dataset's shared <c>Integer</c> type, alongside every other loose integer, and a type of its own
/// means a smaller ordinal pool and fewer bits per reference.
/// </remarks>
public class ObjectMapperCollectionTypeNameTests
{
    [Fact]
    public void AListsElementTypeCanBeNamed()
    {
        HollowWriteStateEngine engine = Map(typeof(NamedListElements));

        // The list keeps the name its CLR type gives it — Java derives the collection's own name from
        // the type, not from what its elements ended up called — and its elements move to MovieId.
        Assert.Equal("ListOfInteger List<MovieId>;", engine.GetSchema("ListOfInteger")!.ToString());

        // A type of its own, holding what the shared Integer type would have held.
        Assert.Equal("MovieId {\n\tint value;\n}", engine.GetSchema("MovieId")!.ToString());
        Assert.Null(engine.GetSchema("Integer"));
    }

    [Fact]
    public void ASetsElementTypeCanBeNamed()
    {
        HollowWriteStateEngine engine = Map(typeof(NamedSetElements));

        // The derived hash key comes along as usual: it is worked out from the element's CLR type,
        // which naming the Hollow type does not change.
        Assert.Equal(
            "SetOfInteger Set<MovieId> @HashKey(value);", engine.GetSchema("SetOfInteger")!.ToString());
        Assert.NotNull(engine.GetSchema("MovieId"));
    }

    [Fact]
    public void AMapsKeyAndValueTypesCanBeNamed()
    {
        HollowWriteStateEngine engine = Map(typeof(NamedMapEntries));

        Assert.Equal(
            "MapOfStringToString Map<SubTypeKey,SubTypeValue> @HashKey(value);",
            engine.GetSchema("MapOfStringToString")!.ToString());

        Assert.Equal("SubTypeKey {\n\tstring value;\n}", engine.GetSchema("SubTypeKey")!.ToString());
        Assert.Equal("SubTypeValue {\n\tstring value;\n}", engine.GetSchema("SubTypeValue")!.ToString());
        Assert.Null(engine.GetSchema("String"));
    }

    [Fact]
    public void EitherHalfOfAMapCanBeNamedOnItsOwn()
    {
        HollowWriteStateEngine engine = Map(typeof(NamedMapKeyOnly));

        // The value keeps the shared String type, which is the whole reason both halves are optional.
        Assert.Equal(
            "MapOfStringToString Map<SubTypeKey,String> @HashKey(value);",
            engine.GetSchema("MapOfStringToString")!.ToString());

        Assert.NotNull(engine.GetSchema("String"));
    }

    [Fact]
    public void TheCollectionTypeIsRenamedSeparately()
    {
        HollowWriteStateEngine engine = Map(typeof(RenamedListAndElements));

        // The composition Java's documentation shows: one attribute names the list, the other names
        // what it holds.
        Assert.Equal("MyMovieIds List<MovieId>;", engine.GetSchema("MyMovieIds")!.ToString());
        Assert.Null(engine.GetSchema("ListOfInteger"));
    }

    [Fact]
    public void ANamedElementTypeStillReadsBackAsItsValues()
    {
        HollowWriteStateEngine writeEngine = new();
        HollowObjectMapper mapper = new(writeEngine);

        mapper.Add(new NamedListElements { MovieIds = [7, 11, 7] });

        HollowReadStateEngine readEngine = StateEngineRoundTripper.RoundTripSnapshot(writeEngine);

        Assert.Equal(
            "ListOfInteger List<MovieId>;", readEngine.GetTypeState("ListOfInteger")!.Schema.ToString());

        // Two records, not three: the repeated element deduplicates inside its own type just as it
        // would inside the shared one.
        Assert.Equal(2, readEngine.GetTypeState("MovieId")!.PopulatedOrdinals.Cardinality());
    }

    [Fact]
    public void TwoListsOfOneTypeMayNameTheirElementsDifferently()
    {
        HollowWriteStateEngine engine = Map(typeof(TwoDifferentlyNamedLists));

        Assert.Equal("MyMovieIds List<MovieId>;", engine.GetSchema("MyMovieIds")!.ToString());
        Assert.Equal("MyActorIds List<ActorId>;", engine.GetSchema("MyActorIds")!.ToString());
    }

    [Fact]
    public void TwoListsThatWouldShareATypeNameAndDisagreeAreRefused()
    {
        // Java keys its mappers by type name alone, so the second member here silently gets the
        // first's mapper and its annotation does nothing at all. Refusing says what went wrong.
        HollowMappingException failure =
            Assert.Throws<HollowMappingException>(() => Map(typeof(TwoListsOneName)));

        Assert.Contains("ListOfInteger", failure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(HollowCollectionTypeNameAttribute), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BothAttributesOnOneMemberAreRefused()
    {
        HollowMappingException failure =
            Assert.Throws<HollowMappingException>(() => Map(typeof(BothAttributes)));

        Assert.Contains("only one of them", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCollectionAttributeOnAMapIsRefused()
    {
        HollowMappingException failure =
            Assert.Throws<HollowMappingException>(() => Map(typeof(CollectionAttributeOnAMap)));

        Assert.Contains("rather than a list or a set", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMapAttributeOnAListIsRefused()
    {
        HollowMappingException failure =
            Assert.Throws<HollowMappingException>(() => Map(typeof(MapAttributeOnAList)));

        Assert.Contains("rather than a map", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EitherAttributeOnSomethingThatHoldsNoCollectionIsRefused()
    {
        Assert.Throws<HollowMappingException>(() => Map(typeof(CollectionAttributeOnAnInt)));
    }

    private static HollowWriteStateEngine Map(Type type)
    {
        HollowWriteStateEngine engine = new();
        new HollowObjectMapper(engine).InitializeTypeState(type);

        return engine;
    }

    private sealed class NamedListElements
    {
        [HollowCollectionTypeName("MovieId")]
        public List<int> MovieIds { get; init; } = [];
    }

    private sealed class NamedSetElements
    {
        [HollowCollectionTypeName("MovieId")]
        public HashSet<int> MovieIds { get; init; } = [];
    }

    private sealed class NamedMapEntries
    {
        [HollowMapTypeName(KeyTypeName = "SubTypeKey", ValueTypeName = "SubTypeValue")]
        public Dictionary<string, string> Attributes { get; init; } = [];
    }

    private sealed class NamedMapKeyOnly
    {
        [HollowMapTypeName(KeyTypeName = "SubTypeKey")]
        public Dictionary<string, string> Attributes { get; init; } = [];
    }

    private sealed class RenamedListAndElements
    {
        [HollowTypeName("MyMovieIds")]
        [HollowCollectionTypeName("MovieId")]
        public List<int> MovieIds { get; init; } = [];
    }

    private sealed class TwoDifferentlyNamedLists
    {
        [HollowTypeName("MyMovieIds")]
        [HollowCollectionTypeName("MovieId")]
        public List<int> MovieIds { get; init; } = [];

        [HollowTypeName("MyActorIds")]
        [HollowCollectionTypeName("ActorId")]
        public List<int> ActorIds { get; init; } = [];
    }

    private sealed class TwoListsOneName
    {
        public List<int> PlainIds { get; init; } = [];

        [HollowCollectionTypeName("MovieId")]
        public List<int> MovieIds { get; init; } = [];
    }

    private sealed class BothAttributes
    {
        [HollowCollectionTypeName("MovieId")]
        [HollowMapTypeName(KeyTypeName = "SubTypeKey")]
        public List<int> MovieIds { get; init; } = [];
    }

    private sealed class CollectionAttributeOnAMap
    {
        [HollowCollectionTypeName("MovieId")]
        public Dictionary<string, string> Attributes { get; init; } = [];
    }

    private sealed class MapAttributeOnAList
    {
        [HollowMapTypeName(KeyTypeName = "SubTypeKey")]
        public List<int> MovieIds { get; init; } = [];
    }

    private sealed class CollectionAttributeOnAnInt
    {
        [HollowCollectionTypeName("MovieId")]
        public int MovieId { get; init; }
    }
}
