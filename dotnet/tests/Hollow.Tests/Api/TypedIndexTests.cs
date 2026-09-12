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

using Hollow.Api.Client;
using Hollow.Api.Consumer;
using Hollow.Api.Consumer.Index;
using Hollow.Api.Custom;
using Hollow.Api.Objects;
using Hollow.Api.Objects.Delegate;
using Hollow.Api.Objects.Generic;
using Hollow.Core;
using Hollow.Core.Index;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Schema;
using Hollow.Core.Types;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Tests.Api;

/// <summary>
/// The typed façades over the two value indexes, against a consumer with a generated-shape API.
/// </summary>
/// <remarks>
/// <para>
/// What these add over <c>PrimaryKeyIndexTests</c> and <c>HashIndexTests</c> is the two ends: a query
/// is an object of the caller's own type rather than a loose <c>object[]</c>, and a match comes back as
/// a record wrapper rather than an ordinal. Both ends are bound to the schemas when the index is built,
/// so most of what is worth testing is what the binding refuses.
/// </para>
/// <para>
/// The model is declared twice, as Java's <c>DataModel</c> does: once as the CLR types the producer
/// maps, once as the record wrappers a generator would emit. They have to agree on type names, since
/// that is what the index binds through.
/// </para>
/// </remarks>
public class TypedIndexTests
{
    // ---- The producer's data model. ----

    private static class Producer
    {
        internal sealed record Actor(string Name);

        internal sealed record Studio(string Name);

        [HollowPrimaryKey("Id")]
        internal sealed record Movie(int Id, string Title, int Year, Studio Studio, List<Actor> Cast);

        [HollowPrimaryKey("Id", "Studio.Name", "Year")]
        internal sealed record Release(int Id, Studio Studio, int Year, string Country);
    }

    private static readonly Producer.Studio WarnerBros = new("Warner Bros.");
    private static readonly Producer.Studio Lionsgate = new("Lionsgate");

    private static readonly Producer.Movie TheMatrix = new(
        1, "The Matrix", 1999, WarnerBros,
        [new Producer.Actor("Keanu Reeves"), new Producer.Actor("Laurence Fishburne")]);

    private static readonly Producer.Movie JohnWick = new(
        2, "John Wick", 2014, Lionsgate, [new Producer.Actor("Keanu Reeves")]);

    private static readonly Producer.Movie Rush = new(
        3, "Rush", 2013, Lionsgate, [new Producer.Actor("Chris Hemsworth")]);

    /// <summary>Publishes one cycle and returns a consumer sitting on it, with its typed API.</summary>
    private static (HollowConsumer Consumer, InMemoryBlobStore Store, HollowObjectMapper Mapper,
        HollowWriteStateEngine Write) Publish(params Producer.Movie[] movies)
    {
        InMemoryBlobStore store = new();
        HollowWriteStateEngine writeEngine = new();
        HollowObjectMapper mapper = new(writeEngine);
        mapper.InitializeTypeState(typeof(Producer.Movie));
        mapper.InitializeTypeState(typeof(Producer.Release));

        foreach (Producer.Movie movie in movies)
        {
            mapper.Add(movie);
        }

        // One release, for the multi-field declared key.
        mapper.Add(new Producer.Release(1, WarnerBros, 1999, "US"));

        store.Publish(writeEngine, 1);

        HollowConsumer consumer = new HollowConsumerBuilder()
            .WithBlobRetriever(store)
            .WithApiFactory(new DelegateHollowApiFactory(access => new MovieApi(access)))
            .Build();

        consumer.TriggerRefreshTo(1);

        return (consumer, store, mapper, writeEngine);
    }

    // ---- The consumer's API, of the shape a generator would emit. ----

    /// <summary>
    /// A consumer built with an API factory hands that API out; it is what the typed indexes turn an
    /// ordinal into a record with.
    /// </summary>
    [Fact]
    public void TheConsumerHandsOutTheGeneratedApi()
    {
        (HollowConsumer consumer, _, _, _) = Publish(TheMatrix);

        MovieApi api = Assert.IsType<MovieApi>(consumer.Api);
        Assert.Equal("The Matrix", api.AllMovies.Single().Title);

        // A consumer built without one still has an API; it simply carries no type APIs.
        HollowConsumer plain = new HollowConsumerBuilder()
            .WithBlobRetriever(Publish(TheMatrix).Store)
            .Build();
        plain.TriggerRefreshTo(1);

        Assert.NotNull(plain.Api);
        Assert.Empty(plain.Api.TypeApis);
    }

    /// <summary>
    /// The reflective factory is the form Java has: hand it the generated class and it picks the
    /// constructor the generator emitted.
    /// </summary>
    [Fact]
    public void AGeneratedApiCanBeWiredUpByItsType()
    {
        (_, InMemoryBlobStore store, _, _) = Publish(TheMatrix);

        HollowConsumer consumer = new HollowConsumerBuilder()
            .WithBlobRetriever(store)
            .WithApiFactory(new GeneratedHollowApiFactory<MovieApi>())
            .Build();

        consumer.TriggerRefreshTo(1);

        Assert.Equal("The Matrix", Assert.IsType<MovieApi>(consumer.Api).AllMovies.Single().Title);
    }

    // ---- UniqueKeyIndex ----

    [Fact]
    public void FindsTheUniqueRecordForASingleFieldKey()
    {
        (HollowConsumer consumer, _, _, _) = Publish(TheMatrix, JohnWick, Rush);

        UniqueKeyIndex<Movie, int> byId = UniqueKeyIndex.From<Movie>(consumer).UsingPath<int>("Id");

        Assert.Equal("John Wick", byId.FindMatch(2)?.Title);
        Assert.Equal("Rush", byId.FindMatch(3)?.Title);
        Assert.Null(byId.FindMatch(99));
    }

    /// <summary>
    /// Binding to the declared primary key is what ties the index to the uniqueness the producer
    /// actually enforces, rather than to a key that merely happens to be unique today.
    /// </summary>
    [Fact]
    public void BindsToTheKeyTheTypeDeclares()
    {
        (HollowConsumer consumer, _, _, _) = Publish(TheMatrix, JohnWick);

        UniqueKeyIndex<Movie, int> byId = UniqueKeyIndex.From<Movie>(consumer)
            .BindToPrimaryKey()
            .UsingPath<int>("Id");

        Assert.Equal("The Matrix", byId.FindMatch(1)?.Title);
    }

    /// <summary>
    /// A key object's members are put in the order the declared key lists them, so the key type may
    /// declare them in any order it likes.
    /// </summary>
    [Fact]
    public void AKeyObjectIsReorderedToMatchTheDeclaredKey()
    {
        (HollowConsumer consumer, _, _, _) = Publish(TheMatrix);

        UniqueKeyIndex<Release, DeclaredOrderKey> declared = UniqueKeyIndex.From<Release>(consumer)
            .BindToPrimaryKey()
            .UsingBean<DeclaredOrderKey>();

        UniqueKeyIndex<Release, ReverseOrderKey> reversed = UniqueKeyIndex.From<Release>(consumer)
            .BindToPrimaryKey()
            .UsingBean<ReverseOrderKey>();

        Assert.NotNull(declared.FindMatch(new DeclaredOrderKey(1, "Warner Bros.", 1999)));
        Assert.NotNull(reversed.FindMatch(new ReverseOrderKey(1999, "Warner Bros.", 1)));

        // And a key that names the right paths still has to name the right values.
        Assert.Null(declared.FindMatch(new DeclaredOrderKey(1, "Lionsgate", 1999)));
    }

    [Fact]
    public void RefusesAKeyThatIsNotTheDeclaredOne()
    {
        (HollowConsumer consumer, _, _, _) = Publish(TheMatrix);

        UniqueKeyIndexBuilder<Release> builder = UniqueKeyIndex.From<Release>(consumer).BindToPrimaryKey();

        // One of the declared key's paths is missing from the key type.
        Assert.Throws<ArgumentException>(() => builder.UsingBean<PartialKey>());

        // A path the declared key does not contain.
        Assert.Throws<ArgumentException>(() => builder.UsingBean<WrongPathKey>());
    }

    [Fact]
    public void RefusesABindingItCannotHonour()
    {
        (HollowConsumer consumer, _, _, _) = Publish(TheMatrix);

        // A key member whose type cannot match the field its path resolves to.
        Assert.Throws<ArgumentException>(
            () => UniqueKeyIndex.From<Movie>(consumer).UsingPath<long>("Id"));

        // A path that is not in the schema at all.
        Assert.Throws<FieldPathException>(
            () => UniqueKeyIndex.From<Movie>(consumer).UsingPath<int>("NoSuchField"));

        // An empty match path.
        Assert.Throws<ArgumentException>(
            () => UniqueKeyIndex.From<Movie>(consumer).UsingPath<int>(string.Empty));

        // A type that declares no primary key cannot be bound to one.
        Assert.Throws<InvalidOperationException>(
            () => UniqueKeyIndex.From<HString>(consumer, "String").BindToPrimaryKey());
    }

    /// <summary>
    /// A key member may be a record handle rather than a value, in which case the index matches on its
    /// ordinal — which is how a match result from one index becomes the key to another.
    /// </summary>
    [Fact]
    public void MatchesOnARecordHandle()
    {
        (HollowConsumer consumer, _, _, _) = Publish(TheMatrix, JohnWick);

        MovieApi api = (MovieApi)consumer.Api!;

        // The path stops at the reference rather than expanding through it.
        UniqueKeyIndex<Movie, HString> byTitleRecord = UniqueKeyIndex.From<Movie>(consumer)
            .UsingPath<HString>("Title!");

        HString title = api.AllMovies.Single(movie => movie.Title == "John Wick").TitleRecord!;

        Assert.Equal("John Wick", byTitleRecord.FindMatch(title)?.Title);
    }

    /// <summary>
    /// An index registered with the consumer follows the data. Carried over from Java's
    /// <c>UniqueKeyUpdatesTest</c>, which runs the same scenario twice — once following deltas, once
    /// forcing a snapshot — because those are different code paths in the index.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARegisteredIndexFollowsTheData(bool forceDoubleSnapshot)
    {
        (HollowConsumer consumer, InMemoryBlobStore store, HollowObjectMapper mapper,
            HollowWriteStateEngine writeEngine) = Publish(TheMatrix);

        UniqueKeyIndex<Movie, int> byId = UniqueKeyIndex.From<Movie>(consumer).UsingPath<int>("Id");
        consumer.AddRefreshListener(byId);

        Assert.NotNull(byId.FindMatch(1));
        Assert.Null(byId.FindMatch(2));

        mapper.Add(TheMatrix);
        mapper.Add(JohnWick);
        store.Publish(writeEngine, 2);

        if (forceDoubleSnapshot)
        {
            consumer.ForceDoubleSnapshotNextUpdate();
        }

        consumer.TriggerRefreshTo(2);

        Assert.NotNull(byId.FindMatch(1));
        Assert.Equal("John Wick", byId.FindMatch(2)?.Title);

        // Removed from the consumer, it stops following: the third movie never reaches it.
        consumer.RemoveRefreshListener(byId);

        mapper.Add(TheMatrix);
        mapper.Add(JohnWick);
        mapper.Add(Rush);
        store.Publish(writeEngine, 3);

        if (forceDoubleSnapshot)
        {
            consumer.ForceDoubleSnapshotNextUpdate();
        }

        consumer.TriggerRefreshTo(3);

        Assert.Null(byId.FindMatch(3));
    }

    [Fact]
    public void AnIndexRefusesToBeAddedToADifferentConsumer()
    {
        (HollowConsumer consumer, InMemoryBlobStore store, _, _) = Publish(TheMatrix);

        UniqueKeyIndex<Movie, int> byId = UniqueKeyIndex.From<Movie>(consumer).UsingPath<int>("Id");

        HollowConsumer other = new HollowConsumerBuilder()
            .WithBlobRetriever(store)
            .WithApiFactory(new DelegateHollowApiFactory(access => new MovieApi(access)))
            .Build();
        other.TriggerRefreshTo(1);

        Assert.Throws<InvalidOperationException>(() => other.AddRefreshListener(byId));
    }

    // ---- HashIndex ----

    [Fact]
    public void FindsEveryRecordMatchingANonUniqueQuery()
    {
        (HollowConsumer consumer, _, _, _) = Publish(TheMatrix, JohnWick, Rush);

        HashIndex<Movie, string> byStudio = HashIndex.From<Movie>(consumer)
            .UsingPath<string>("Studio.Name.value");

        Assert.Equal(
            ["John Wick", "Rush"],
            byStudio.FindMatches("Lionsgate").Select(movie => movie.Title).Order(StringComparer.Ordinal));

        Assert.Single(byStudio.FindMatches("Warner Bros."));
        Assert.Empty(byStudio.FindMatches("Paramount"));
    }

    /// <summary>
    /// A path crossing a collection is what distinguishes this index from the unique-key one: one movie
    /// has several actors, and one actor is in several movies.
    /// </summary>
    [Fact]
    public void MatchesThroughACollection()
    {
        (HollowConsumer consumer, _, _, _) = Publish(TheMatrix, JohnWick, Rush);

        HashIndex<Movie, string> byActor = HashIndex.From<Movie>(consumer)
            .UsingPath<string>("Cast.element.Name.value");

        Assert.Equal(
            ["John Wick", "The Matrix"],
            byActor.FindMatches("Keanu Reeves").Select(movie => movie.Title).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A select path returns the records the path names rather than the roots that matched — here every
    /// cast member of the movies a given studio made.
    /// </summary>
    [Fact]
    public void SelectsTheRecordsAPathNames()
    {
        (HollowConsumer consumer, _, _, _) = Publish(TheMatrix, JohnWick, Rush);

        HashIndexSelect<Movie, Actor, string> castByStudio = HashIndex.From<Movie>(consumer)
            .SelectField<Actor>("Cast.element")
            .UsingPath<string>("Studio.Name.value");

        Assert.Equal(
            ["Chris Hemsworth", "Keanu Reeves"],
            castByStudio.FindMatches("Lionsgate").Select(actor => actor.Name).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A generic record needs no accessor on the API, which is what makes the index usable before a
    /// generator exists.
    /// </summary>
    [Fact]
    public void SelectsAsAGenericRecord()
    {
        (HollowConsumer consumer, _, _, _) = Publish(TheMatrix, JohnWick);

        HashIndexSelect<Movie, GenericHollowObject, int> byYear = HashIndex.From<Movie>(consumer)
            .SelectField<GenericHollowObject>("Title")
            .UsingPath<int>("Year");

        Assert.Equal("The Matrix", byYear.FindMatches(1999).Single().GetString("value"));
    }

    [Fact]
    public void RefusesASelectPathThatDoesNotNameARecord()
    {
        (HollowConsumer consumer, _, _, _) = Publish(TheMatrix);

        // "Year" is an int field, not a reference, so there is no record to select.
        Assert.Throws<ArgumentException>(
            () => HashIndex.From<Movie>(consumer)
                .SelectField<GenericHollowObject>("Year")
                .UsingPath<int>("Year"));

        // A select type with no accessor on the API and no generic form.
        Assert.Throws<ArgumentException>(
            () => HashIndex.From<Movie>(consumer)
                .SelectField<Studio>("Studio")
                .UsingPath<int>("Year"));
    }

    /// <summary>
    /// The same following behaviour as the unique-key index, over the index that has to rebuild its
    /// whole hash state on a snapshot.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARegisteredHashIndexFollowsTheData(bool forceDoubleSnapshot)
    {
        (HollowConsumer consumer, InMemoryBlobStore store, HollowObjectMapper mapper,
            HollowWriteStateEngine writeEngine) = Publish(TheMatrix, JohnWick);

        HashIndex<Movie, string> byActor = HashIndex.From<Movie>(consumer)
            .UsingPath<string>("Cast.element.Name.value");
        consumer.AddRefreshListener(byActor);

        Assert.Equal(2, byActor.FindMatches("Keanu Reeves").Count());
        Assert.Empty(byActor.FindMatches("Chris Hemsworth"));

        mapper.Add(TheMatrix);
        mapper.Add(JohnWick);
        mapper.Add(Rush);
        store.Publish(writeEngine, 2);

        if (forceDoubleSnapshot)
        {
            consumer.ForceDoubleSnapshotNextUpdate();
        }

        consumer.TriggerRefreshTo(2);

        Assert.Equal("Rush", byActor.FindMatches("Chris Hemsworth").Single().Title);
        Assert.Equal(2, byActor.FindMatches("Keanu Reeves").Count());

        consumer.RemoveRefreshListener(byActor);
    }

    [Fact]
    public void DescribesItself()
    {
        (HollowConsumer consumer, _, _, _) = Publish(TheMatrix);

        Assert.Equal(
            "UniqueKeyIndex(Movie: Id)",
            UniqueKeyIndex.From<Movie>(consumer).UsingPath<int>("Id").ToString());

        Assert.Equal(
            "HashIndex(Movie: select (root) matching Year)",
            HashIndex.From<Movie>(consumer).UsingPath<int>("Year").ToString());
    }

    // ---- Key types, as a caller would declare them. ----

    private sealed record DeclaredOrderKey(
        [property: FieldPath("Id")] int Id,
        [property: FieldPath("Studio.Name", Order = 1)] string Studio,
        [property: FieldPath("Year", Order = 2)] int Year);

    private sealed record ReverseOrderKey(
        [property: FieldPath("Year", Order = 2)] int Year,
        [property: FieldPath("Studio.Name", Order = 1)] string Studio,
        [property: FieldPath("Id")] int Id);

    /// <summary>Names only part of the declared key.</summary>
    private sealed record PartialKey(
        [property: FieldPath("Id")] int Id,
        [property: FieldPath("Studio.Name", Order = 1)] string Studio);

    /// <summary>Names a real path that the declared key does not contain.</summary>
    private sealed record WrongPathKey(
        [property: FieldPath("Id")] int Id,
        [property: FieldPath("Studio.Name", Order = 1)] string Studio,
        [property: FieldPath("Country", Order = 2)] string Country);

    // ---- Below is the shape a generator would emit. ----

    /// <summary>The typed view over the dataset, with one accessor per type.</summary>
    internal sealed class MovieApi : HollowApi
    {
        public MovieApi(IHollowDataAccess dataAccess)
            : base(dataAccess)
        {
            MovieTypeApi = new MovieTypeApi(this, ObjectAccess(dataAccess, "Movie"));
            ActorTypeApi = new ActorTypeApi(this, ObjectAccess(dataAccess, "Actor"));
            ReleaseTypeApi = new ReleaseTypeApi(this, ObjectAccess(dataAccess, "Release"));
            StringTypeApi = new StringTypeApi(this, ObjectAccess(dataAccess, "String"));

            AddTypeApi(MovieTypeApi);
            AddTypeApi(ActorTypeApi);
            AddTypeApi(ReleaseTypeApi);
            AddTypeApi(StringTypeApi);
        }

        internal MovieTypeApi MovieTypeApi { get; }

        internal ActorTypeApi ActorTypeApi { get; }

        internal ReleaseTypeApi ReleaseTypeApi { get; }

        internal StringTypeApi StringTypeApi { get; }

        internal IEnumerable<Movie> AllMovies =>
            MovieTypeApi.TypeDataAccess.TypeState.PopulatedOrdinals.EnumerateSetBits().Select(GetMovie);

        /// <summary>The accessor a select path resolving to <c>Movie</c> is read through.</summary>
        public Movie GetMovie(int ordinal) => new(new MovieDelegate(MovieTypeApi), ordinal);

        /// <summary>The accessor a select path resolving to <c>Actor</c> is read through.</summary>
        public Actor GetActor(int ordinal) => new(new ActorDelegate(ActorTypeApi), ordinal);

        /// <summary>The accessor a select path resolving to <c>String</c> is read through.</summary>
        public HString GetHString(int ordinal) => new(new StringDelegate(StringTypeApi), ordinal);

        /// <summary>The accessor a select path resolving to <c>Release</c> is read through.</summary>
        public Release GetRelease(int ordinal) => new(new ReleaseDelegate(ReleaseTypeApi), ordinal);

        private static IHollowObjectTypeDataAccess ObjectAccess(IHollowDataAccess dataAccess, string typeName) =>
            (IHollowObjectTypeDataAccess)dataAccess.GetTypeDataAccess(typeName)!;
    }

    internal sealed class MovieTypeApi(HollowApi api, IHollowObjectTypeDataAccess typeDataAccess)
        : HollowObjectTypeApi(api, typeDataAccess, ["Id", "Title", "Year", "Studio", "Cast"])
    {
        internal int GetId(int ordinal) => ReadIntField(ordinal, 0);

        internal int GetTitleOrdinal(int ordinal) => ReadOrdinalField(ordinal, 1);

        internal int GetYear(int ordinal) => ReadIntField(ordinal, 2);
    }

    internal sealed class ActorTypeApi(HollowApi api, IHollowObjectTypeDataAccess typeDataAccess)
        : HollowObjectTypeApi(api, typeDataAccess, ["Name"])
    {
        internal int GetNameOrdinal(int ordinal) => ReadOrdinalField(ordinal, 0);
    }

    internal sealed class ReleaseTypeApi(HollowApi api, IHollowObjectTypeDataAccess typeDataAccess)
        : HollowObjectTypeApi(api, typeDataAccess, ["Id", "Studio", "Year", "Country"])
    {
        internal int GetId(int ordinal) => ReadIntField(ordinal, 0);
    }

    internal sealed class StringTypeApi(HollowApi api, IHollowObjectTypeDataAccess typeDataAccess)
        : HollowScalarTypeApi<string?>(api, typeDataAccess)
    {
        public override string? GetValue(int ordinal) => ReadStringField(ordinal, ValueFieldPosition);
    }

    internal sealed class MovieDelegate(MovieTypeApi typeApi) : HollowObjectAbstractDelegate
    {
        public override HollowObjectSchema Schema => typeApi.Schema;

        public override IHollowObjectTypeDataAccess TypeDataAccess => typeApi.TypeDataAccess;

        public override HollowObjectTypeApi TypeApi => typeApi;

        internal MovieTypeApi MovieTypeApi => typeApi;
    }

    internal sealed class ActorDelegate(ActorTypeApi typeApi) : HollowObjectAbstractDelegate
    {
        public override HollowObjectSchema Schema => typeApi.Schema;

        public override IHollowObjectTypeDataAccess TypeDataAccess => typeApi.TypeDataAccess;

        public override HollowObjectTypeApi TypeApi => typeApi;

        internal ActorTypeApi ActorTypeApi => typeApi;
    }

    /// <summary>A movie record, named for the type it reads so that the index can bind to it.</summary>
    internal sealed class Movie(MovieDelegate movieDelegate, int ordinal)
        : HollowObject(movieDelegate, ordinal)
    {
        internal int Id => movieDelegate.MovieTypeApi.GetId(Ordinal);

        internal int Year => movieDelegate.MovieTypeApi.GetYear(Ordinal);

        internal string? Title => TitleRecord?.Value;

        /// <summary>The shared <c>String</c> record the title is stored as, rather than its text.</summary>
        internal HString? TitleRecord
        {
            get
            {
                int titleOrdinal = movieDelegate.MovieTypeApi.GetTitleOrdinal(Ordinal);

                return titleOrdinal == HollowConstants.OrdinalNone
                    ? null
                    : ((MovieApi)movieDelegate.MovieTypeApi.Api).GetHString(titleOrdinal);
            }
        }
    }

    internal sealed class Actor(ActorDelegate actorDelegate, int ordinal)
        : HollowObject(actorDelegate, ordinal)
    {
        internal string? Name
        {
            get
            {
                int nameOrdinal = actorDelegate.ActorTypeApi.GetNameOrdinal(Ordinal);

                return nameOrdinal == HollowConstants.OrdinalNone
                    ? null
                    : ((MovieApi)actorDelegate.ActorTypeApi.Api).GetHString(nameOrdinal).Value;
            }
        }
    }

    internal sealed class ReleaseDelegate(ReleaseTypeApi typeApi) : HollowObjectAbstractDelegate
    {
        public override HollowObjectSchema Schema => typeApi.Schema;

        public override IHollowObjectTypeDataAccess TypeDataAccess => typeApi.TypeDataAccess;

        public override HollowObjectTypeApi TypeApi => typeApi;

        internal ReleaseTypeApi ReleaseTypeApi => typeApi;
    }

    internal sealed class StringDelegate(StringTypeApi typeApi) : HollowObjectAbstractDelegate
    {
        public override HollowObjectSchema Schema => typeApi.Schema;

        public override IHollowObjectTypeDataAccess TypeDataAccess => typeApi.TypeDataAccess;

        public override HollowObjectTypeApi TypeApi => typeApi;
    }

    /// <summary>A release, indexed by the three-field key its type declares.</summary>
    internal sealed class Release(ReleaseDelegate releaseDelegate, int ordinal)
        : HollowObject(releaseDelegate, ordinal)
    {
        internal int Id => releaseDelegate.ReleaseTypeApi.GetId(Ordinal);
    }

    /// <summary>A record wrapper the API has no accessor for, to check that the binding notices.</summary>
    internal sealed class Studio(MovieDelegate movieDelegate, int ordinal)
        : HollowObject(movieDelegate, ordinal)
    {
    }
}
