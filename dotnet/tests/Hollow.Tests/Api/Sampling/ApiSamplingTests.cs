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
using Hollow.Api.Sampling;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.List;
using Hollow.Core.Read.Filter;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Tests.Api.Sampling;

/// <summary>
/// Turning sampling on through a typed client rather than through the read state.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam an application actually has: it holds a generated API, not a state engine. The
/// API covers only the types its model declares, so the numbers it reports are the ones a caller can
/// act on — a field of a type the client never mentions is not its problem.
/// </para>
/// <para>
/// Java also carries a second sampler per object type, counting its boxed getters, because it emits
/// <c>getYear()</c> and <c>getYearBoxed()</c> separately and only the second allocates. Here one
/// <c>int?</c> accessor says both and <see cref="Nullable{T}"/> is a struct, so there is no boxing to
/// count and no second sampler.
/// </para>
/// </remarks>
public class ApiSamplingTests
{
    [Fact]
    public void ADirectorSetThroughTheApiReachesEveryTypeItCovers()
    {
        MovieApi api = Api();

        Assert.Null(api.SamplingDirector);

        EnabledSamplingDirector director = new();
        api.SetSamplingDirector(director);

        Assert.Same(director, api.SamplingDirector);

        api.ReadTitles();

        Assert.True(api.HasSampleResults);
        Assert.Equal(3, Count(api, "Movie.Title"));
        Assert.Equal(3, Count(api, "String.value"));
    }

    [Fact]
    public void TurningSamplingOnThroughTheApiTurnsItOnForTheApisTypesOnly()
    {
        MovieApi api = Api();

        api.SetSamplingDirector(new EnabledSamplingDirector());

        HollowReadStateEngine engine = (HollowReadStateEngine)api.DataAccess;

        Cast(engine).Size(0);
        api.ReadTitles();

        // The dataset has a list type; the client's model does not mention it, so the director never
        // reaches it and it stays silent even in the read state's own numbers.
        Assert.DoesNotContain(
            api.GetSampleResults(),
            result => result.Identifier.StartsWith("ListOfString", StringComparison.Ordinal));

        Assert.DoesNotContain(
            engine.GetSampleResults(),
            result => result.Identifier.StartsWith("ListOfString", StringComparison.Ordinal));
    }

    [Fact]
    public void TurningSamplingOnThroughTheReadStateCoversEveryType()
    {
        MovieApi api = Api();

        HollowReadStateEngine engine = (HollowReadStateEngine)api.DataAccess;

        // The same director set on the dataset reaches the types no client declares.
        engine.SetSamplingDirector(new EnabledSamplingDirector());

        Cast(engine).Size(0);

        Assert.Equal(1, Count(engine, "ListOfString.Count"));

        // And the API, which covers only its own types, still does not report it.
        Assert.DoesNotContain(
            api.GetSampleResults(),
            result => result.Identifier.StartsWith("ListOfString", StringComparison.Ordinal));
    }

    [Fact]
    public void ResultsFromTheApiComeHottestFirst()
    {
        MovieApi api = Api();

        api.SetSamplingDirector(new EnabledSamplingDirector());

        api.ReadTitles();
        api.ReadYears();
        api.ReadYears();

        Assert.Equal("Movie.Year", api.GetSampleResults()[0].Identifier);
        Assert.Equal(6, api.GetSampleResults()[0].SampleCount);
    }

    [Fact]
    public void ADirectorThroughTheApiCanBeAimedAtOneField()
    {
        MovieApi api = Api();

        api.SetFieldSpecificSamplingDirector(
            TypeFilter.Include(
                ["Movie"],
                new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
                {
                    ["Movie"] = new HashSet<string>(StringComparer.Ordinal) { "Year" },
                }),
            new EnabledSamplingDirector());

        api.ReadTitles();
        api.ReadYears();

        Assert.Equal(3, Count(api, "Movie.Year"));
        Assert.Equal(0, Count(api, "Movie.Title"));
    }

    [Fact]
    public void ResettingThroughTheApiClearsEveryTypeItCovers()
    {
        MovieApi api = Api();

        api.SetSamplingDirector(new EnabledSamplingDirector());
        api.ReadTitles();

        Assert.True(api.HasSampleResults);

        api.ResetSampling();

        Assert.False(api.HasSampleResults);
        Assert.Empty(api.GetSampleResults());
    }

    [Fact]
    public void TheUpdateThreadIsExcludedThroughTheApiToo()
    {
        MovieApi api = Api();

        api.SetSamplingDirector(new EnabledSamplingDirector());
        api.SetSamplerUpdateThread(Thread.CurrentThread);

        api.ReadTitles();

        Assert.False(api.HasSampleResults);
    }

    private static HollowListTypeReadState Cast(HollowReadStateEngine engine) =>
        (HollowListTypeReadState)engine.GetTypeState("ListOfString")!;

    private static long Count(HollowReadStateEngine engine, string identifier) =>
        engine.GetSampleResults()
            .Single(result => string.Equals(result.Identifier, identifier, StringComparison.Ordinal))
            .SampleCount;

    private static long Count(MovieApi api, string identifier) =>
        api.GetSampleResults()
            .Single(result => string.Equals(result.Identifier, identifier, StringComparison.Ordinal))
            .SampleCount;

    private static MovieApi Api()
    {
        HollowWriteStateEngine engine = new();
        HollowObjectMapper mapper = new(engine);

        mapper.Add(new Movie { Id = 1, Title = "Heat", Year = 1995, Cast = ["Pacino"] });
        mapper.Add(new Movie { Id = 2, Title = "Ronin", Year = 1998, Cast = ["De Niro"] });
        mapper.Add(new Movie { Id = 3, Title = "Collateral", Year = 2004, Cast = ["Cruise"] });

        return new MovieApi(StateEngineRoundTripper.RoundTripSnapshot(engine));
    }

    [HollowPrimaryKey("Id")]
    public sealed class Movie
    {
        public required int Id { get; init; }

        public required string Title { get; init; }

        public required int Year { get; init; }

        public required List<string> Cast { get; init; }
    }

    /// <summary>
    /// The smallest thing a generated API is: two object types and a way to read them.
    /// </summary>
    private sealed class MovieApi : HollowApi
    {
        private readonly HollowReadStateEngine _engine;

        internal MovieApi(HollowReadStateEngine engine)
            : base(engine)
        {
            _engine = engine;

            Movies = new MovieTypeApi(this, (IHollowObjectTypeDataAccess)engine.GetTypeState("Movie")!);
            Strings = new StringTypeApi(this, (IHollowObjectTypeDataAccess)engine.GetTypeState("String")!);

            AddTypeApi(Movies);
            AddTypeApi(Strings);
        }

        internal MovieTypeApi Movies { get; }

        internal StringTypeApi Strings { get; }

        internal void ReadTitles()
        {
            foreach (int ordinal in _engine.GetTypeState("Movie")!.PopulatedOrdinals.EnumerateSetBits())
            {
                Strings.GetValue(Movies.GetTitleOrdinal(ordinal));
            }
        }

        internal void ReadYears()
        {
            foreach (int ordinal in _engine.GetTypeState("Movie")!.PopulatedOrdinals.EnumerateSetBits())
            {
                Movies.GetYear(ordinal);
            }
        }
    }

    private sealed class MovieTypeApi(HollowApi api, IHollowObjectTypeDataAccess typeDataAccess)
        : HollowObjectTypeApi(api, typeDataAccess, ["Id", "Title", "Year"])
    {
        internal int GetTitleOrdinal(int ordinal) => ReadOrdinalField(ordinal, 1);

        internal int GetYear(int ordinal) => ReadIntField(ordinal, 2);
    }

    private sealed class StringTypeApi(HollowApi api, IHollowObjectTypeDataAccess typeDataAccess)
        : HollowObjectTypeApi(api, typeDataAccess, ["value"])
    {
        internal string? GetValue(int ordinal) => ReadStringField(ordinal, 0);
    }
}
