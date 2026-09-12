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

using Hollow.Api.Custom;
using Hollow.Api.Objects;
using Hollow.Api.Objects.Delegate;
using Hollow.Api.Objects.Provider;
using Hollow.Core;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Engine;
using Hollow.Core.Schema;
using Hollow.Core.Types;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Tests.Api;

/// <summary>
/// A typed client over a Hollow dataset, of the shape a generator would emit.
/// </summary>
/// <remarks>
/// <para>
/// The classes below are written by hand in the shape generated code takes: a type API that resolves
/// each field's position once, a record wrapper with a property per field, a factory, and an API
/// holding it all together. Writing them by hand is what proves the runtime is fit for a generator to
/// target — if this is awkward to write, the generated code would be awkward too.
/// </para>
/// <para>
/// Note what C# changes. Java emits a <c>getYear()</c> and a <c>getYearBoxed()</c> per numeric field,
/// because only the boxed one can be null; here one <c>int?</c> property says both. Java's
/// <c>getTitle()</c> returns a <c>String</c> by collapsing the reference to the shared record; the
/// same shortcut is a property here.
/// </para>
/// </remarks>
public class TypedApiTests
{
    private sealed record Movie(int Id, string Title, int? Year, decimal? Budget);

    private static (HollowWriteStateEngine Write, HollowReadStateEngine Read, HollowObjectMapper Mapper)
        Populate(params Movie[] movies)
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
    public void ATypedApiReadsEveryFieldOfARecord()
    {
        (_, HollowReadStateEngine readEngine, _) = Populate(
            new Movie(1, "The Matrix", 1999, 63_000_000m));

        MovieApi api = new(readEngine);
        MovieRecord movie = api.AllMovies.Single();

        Assert.Equal(1, movie.Id);
        Assert.Equal("The Matrix", movie.Title);
        Assert.Equal(1999, movie.Year);
        Assert.Equal(63_000_000m, movie.Budget);
    }

    /// <summary>
    /// Java emits a primitive getter and a boxed one per numeric field, because only the boxed one can
    /// carry null. One nullable property covers both, and null actually reads as null.
    /// </summary>
    [Fact]
    public void ANullValueFieldReadsAsNullRatherThanASentinel()
    {
        (_, HollowReadStateEngine readEngine, _) = Populate(new Movie(1, "Untitled", null, null));

        MovieRecord movie = new MovieApi(readEngine).AllMovies.Single();

        Assert.Null(movie.Year);
        Assert.Null(movie.Budget);
        Assert.Equal("Untitled", movie.Title);
    }

    /// <summary>
    /// The typed API resolves each field's position once at construction, so a comparison against a
    /// string field never materialises the stored value.
    /// </summary>
    [Fact]
    public void AStringIsComparedWithoutBeingMaterialised()
    {
        (_, HollowReadStateEngine readEngine, _) = Populate(
            new Movie(1, "The Matrix", 1999, null), new Movie(2, "Rush", 2013, null));

        MovieApi api = new(readEngine);

        Assert.Single(api.AllMovies, movie => movie.IsTitleEqual("Rush"));
        Assert.DoesNotContain(api.AllMovies, movie => movie.IsTitleEqual("Nothing"));
    }

    /// <summary>
    /// A client compiled against a newer model reads an older dataset: the field it wants is simply
    /// not in the loaded schema, and reads of it fall through rather than failing.
    /// </summary>
    [Fact]
    public void AFieldTheDatasetDoesNotHaveReadsAsAbsent()
    {
        (_, HollowReadStateEngine readEngine, _) = Populate(new Movie(1, "The Matrix", 1999, null));

        // The type API asks for a field the schema does not declare.
        NewerMovieTypeApi typeApi = new(
            new HollowApi(readEngine),
            (IHollowObjectTypeDataAccess)readEngine.GetTypeState("Movie")!);

        Assert.Equal(1999, typeApi.GetYear(FirstOrdinal(readEngine, "Movie")));
        Assert.Null(typeApi.GetRuntimeMinutes(FirstOrdinal(readEngine, "Movie")));
    }

    /// <summary>
    /// A cached provider hands out one wrapper per record. Across a delta, a record that did not
    /// change keeps the wrapper it already had.
    /// </summary>
    [Fact]
    public void ACachedProviderReusesWrappersAcrossADelta()
    {
        (HollowWriteStateEngine writeEngine, HollowReadStateEngine readEngine, HollowObjectMapper mapper) =
            Populate(new Movie(1, "The Matrix", 1999, null), new Movie(2, "Rush", 2013, null));

        MovieApi api = new(readEngine);

        HollowObjectCacheProvider<MovieRecord> cache = new(
            (IHollowObjectTypeDataAccess)readEngine.GetTypeState("Movie")!,
            api.MovieTypeApi,
            new MovieFactory(api));

        int unchangedOrdinal = FirstOrdinal(readEngine, "Movie");
        MovieRecord before = cache.GetHollowObject(unchangedOrdinal)!;

        // A second read of the same ordinal is the same object, which is the point of caching.
        Assert.Same(before, cache.GetHollowObject(unchangedOrdinal));

        mapper.Add(new Movie(1, "The Matrix", 1999, null));
        mapper.Add(new Movie(2, "Rush", 2013, null));
        mapper.Add(new Movie(3, "Rocky", 1976, null));
        StateEngineRoundTripper.RoundTripDelta(writeEngine, readEngine);

        MovieApi nextApi = new(readEngine);
        HollowObjectCacheProvider<MovieRecord> nextCache = new(
            (IHollowObjectTypeDataAccess)readEngine.GetTypeState("Movie")!,
            nextApi.MovieTypeApi,
            new MovieFactory(nextApi),
            cache);

        // The record that did not change carried its wrapper over rather than being rebuilt.
        Assert.Same(before, nextCache.GetHollowObject(unchangedOrdinal));

        // And the new record is there, read through the new API.
        Assert.Equal(3, nextApi.AllMovies.Count());

        cache.Detach();
        nextCache.Detach();
    }

    [Fact]
    public void ADetachedCacheRefusesToBeRead()
    {
        (_, HollowReadStateEngine readEngine, _) = Populate(new Movie(1, "The Matrix", 1999, null));

        MovieApi api = new(readEngine);
        HollowObjectCacheProvider<MovieRecord> cache = new(
            (IHollowObjectTypeDataAccess)readEngine.GetTypeState("Movie")!,
            api.MovieTypeApi,
            new MovieFactory(api));

        cache.Detach();

        Assert.Throws<InvalidOperationException>(() => cache.GetHollowObject(0));
    }

    [Fact]
    public void AFactoryProviderBuildsAFreshWrapperEachTime()
    {
        (_, HollowReadStateEngine readEngine, _) = Populate(new Movie(1, "The Matrix", 1999, null));

        MovieApi api = new(readEngine);
        HollowObjectFactoryProvider<MovieRecord> provider = new(
            (IHollowObjectTypeDataAccess)readEngine.GetTypeState("Movie")!,
            api.MovieTypeApi,
            new MovieFactory(api));

        int ordinal = FirstOrdinal(readEngine, "Movie");

        MovieRecord first = provider.GetHollowObject(ordinal)!;
        MovieRecord second = provider.GetHollowObject(ordinal)!;

        Assert.NotSame(first, second);

        // Different objects, same record: a handle is its type and ordinal.
        Assert.Equal(first, second);
        Assert.Equal("The Matrix", second.Title);
    }

    /// <summary>
    /// A string field is stored as a reference to a shared record, so the shared types need typed
    /// wrappers of their own.
    /// </summary>
    [Fact]
    public void TheBuiltInScalarWrappersReadTheirValue()
    {
        (_, HollowReadStateEngine readEngine, _) = Populate(
            new Movie(1, "The Matrix", 1999, 63_000_000m));

        HString title = (HString)HollowScalarTypes.Instantiate(
            (IHollowObjectTypeDataAccess)readEngine.GetTypeState("String")!,
            FirstOrdinal(readEngine, "String"));

        Assert.Equal("The Matrix", title.Value);
        Assert.True(title.IsValueEqual("The Matrix"));
        Assert.False(title.IsValueEqual("Rush"));
        Assert.Equal("The Matrix", title.ToString());

        // A nullable decimal is stored as a shared Decimal record, which is this port's own type.
        HDecimal budget = (HDecimal)HollowScalarTypes.Instantiate(
            (IHollowObjectTypeDataAccess)readEngine.GetTypeState("Decimal")!,
            FirstOrdinal(readEngine, "Decimal"));

        Assert.Equal(63_000_000m, budget.Value);
    }

    [Fact]
    public void AScalarWrapperIsRefusedForATypeThatIsNotOne()
    {
        (_, HollowReadStateEngine readEngine, _) = Populate(new Movie(1, "The Matrix", 1999, null));

        Assert.Throws<ArgumentException>(
            () => HollowScalarTypes.Instantiate(
                (IHollowObjectTypeDataAccess)readEngine.GetTypeState("Movie")!, 0));
    }

    private static int FirstOrdinal(HollowReadStateEngine engine, string typeName) =>
        engine.GetTypeState(typeName)!.PopulatedOrdinals.NextSetBit(0);

    // ---- Below is the shape a generator would emit. ----

    /// <summary>The typed view over the dataset, holding one type API per type.</summary>
    private sealed class MovieApi : HollowApi
    {
        internal MovieApi(HollowReadStateEngine readEngine)
            : base(readEngine)
        {
            MovieTypeApi = new MovieTypeApi(
                this, (IHollowObjectTypeDataAccess)readEngine.GetTypeState("Movie")!);
            StringTypeApi = new StringScalarTypeApi(
                this, (IHollowObjectTypeDataAccess)readEngine.GetTypeState("String")!);

            AddTypeApi(MovieTypeApi);
            AddTypeApi(StringTypeApi);
        }

        internal MovieTypeApi MovieTypeApi { get; }

        internal StringScalarTypeApi StringTypeApi { get; }

        /// <summary>Every movie in the dataset.</summary>
        internal IEnumerable<MovieRecord> AllMovies =>
            MovieTypeApi.TypeDataAccess.TypeState.PopulatedOrdinals
                .EnumerateSetBits()
                .Select(GetMovie);

        internal MovieRecord GetMovie(int ordinal) =>
            new(new MovieDelegate(MovieTypeApi), ordinal);
    }

    /// <summary>Reads a movie's fields by the positions resolved at construction.</summary>
    private sealed class MovieTypeApi(HollowApi api, IHollowObjectTypeDataAccess typeDataAccess)
        : HollowObjectTypeApi(api, typeDataAccess, ["Id", "Title", "Year", "Budget"])
    {
        private const int IdField = 0;
        private const int TitleField = 1;
        private const int YearField = 2;
        private const int BudgetField = 3;

        internal int GetId(int ordinal) => ReadIntField(ordinal, IdField);

        internal int GetTitleOrdinal(int ordinal) => ReadOrdinalField(ordinal, TitleField);

        // A nullable int is stored as a reference to a shared Integer record.
        internal int GetYearOrdinal(int ordinal) => ReadOrdinalField(ordinal, YearField);

        internal int GetBudgetOrdinal(int ordinal) => ReadOrdinalField(ordinal, BudgetField);

        internal bool IsTitleEqual(int ordinal, string? testValue)
        {
            int titleOrdinal = GetTitleOrdinal(ordinal);

            return titleOrdinal == HollowConstants.OrdinalNone
                ? testValue is null
                : ((MovieApi)Api).StringTypeApi.IsValueEqual(titleOrdinal, testValue);
        }
    }

    /// <summary>Reads the shared String type's single field.</summary>
    private sealed class StringScalarTypeApi(HollowApi api, IHollowObjectTypeDataAccess typeDataAccess)
        : HollowScalarTypeApi<string?>(api, typeDataAccess)
    {
        public override string? GetValue(int ordinal) => ReadStringField(ordinal, ValueFieldPosition);

        internal bool IsValueEqual(int ordinal, string? testValue) =>
            IsStringFieldEqual(ordinal, ValueFieldPosition, testValue);
    }

    /// <summary>
    /// A type API asking for a field the loaded dataset does not declare, standing in for a client
    /// compiled against a newer model.
    /// </summary>
    private sealed class NewerMovieTypeApi(HollowApi api, IHollowObjectTypeDataAccess typeDataAccess)
        : HollowObjectTypeApi(api, typeDataAccess, ["Year", "RuntimeMinutes"])
    {
        internal int? GetYear(int ordinal)
        {
            int yearOrdinal = ReadOrdinalField(ordinal, 0);

            return yearOrdinal == HollowConstants.OrdinalNone
                ? null
                : ((IHollowObjectTypeDataAccess)Api.DataAccess.GetTypeDataAccess("Integer")!)
                    .ReadInt(yearOrdinal, 0);
        }

        internal int? GetRuntimeMinutes(int ordinal)
        {
            int value = ReadIntField(ordinal, 1);

            return value == int.MinValue ? null : value;
        }
    }

    /// <summary>Where a movie record reads its data from.</summary>
    private sealed class MovieDelegate(MovieTypeApi typeApi) : HollowObjectAbstractDelegate
    {
        public override HollowObjectSchema Schema => typeApi.Schema;

        public override IHollowObjectTypeDataAccess TypeDataAccess => typeApi.TypeDataAccess;

        public override HollowObjectTypeApi TypeApi => typeApi;

        internal MovieTypeApi MovieTypeApi => typeApi;
    }

    /// <summary>A movie, with a property per field.</summary>
    private sealed class MovieRecord(MovieDelegate movieDelegate, int ordinal)
        : HollowObject(movieDelegate, ordinal)
    {
        internal int Id => MovieTypeApi.GetId(Ordinal);

        internal string? Title => ReadSharedString(MovieTypeApi.GetTitleOrdinal(Ordinal));

        internal int? Year => ReadSharedInt(MovieTypeApi.GetYearOrdinal(Ordinal));

        internal decimal? Budget => ReadSharedDecimal(MovieTypeApi.GetBudgetOrdinal(Ordinal));

        private MovieTypeApi MovieTypeApi => movieDelegate.MovieTypeApi;

        internal bool IsTitleEqual(string? testValue) => MovieTypeApi.IsTitleEqual(Ordinal, testValue);

        private string? ReadSharedString(int referencedOrdinal) =>
            referencedOrdinal == HollowConstants.OrdinalNone
                ? null
                : SharedTypeAccess("String").ReadString(referencedOrdinal, 0);

        private int? ReadSharedInt(int referencedOrdinal) =>
            referencedOrdinal == HollowConstants.OrdinalNone
                ? null
                : SharedTypeAccess("Integer").ReadInt(referencedOrdinal, 0);

        private decimal? ReadSharedDecimal(int referencedOrdinal) =>
            referencedOrdinal == HollowConstants.OrdinalNone
                ? null
                : SharedTypeAccess("Decimal").ReadDecimal(referencedOrdinal, 0);

        private IHollowObjectTypeDataAccess SharedTypeAccess(string typeName) =>
            (IHollowObjectTypeDataAccess)TypeDataAccess.DataAccess.GetTypeDataAccess(typeName)!;
    }

    /// <summary>Builds movie wrappers, for a provider to hand out.</summary>
    private sealed class MovieFactory(MovieApi api) : HollowFactory<MovieRecord>
    {
        public override MovieRecord NewHollowObject(
            IHollowTypeDataAccess typeDataAccess, HollowTypeApi? typeApi, int ordinal) =>
            api.GetMovie(ordinal);
    }
}
