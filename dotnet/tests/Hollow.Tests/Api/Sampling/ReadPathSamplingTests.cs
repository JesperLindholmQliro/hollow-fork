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

using Hollow.Api.Sampling;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.List;
using Hollow.Core.Read.Engine.Map;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Read.Engine.Set;
using Hollow.Core.Read.Filter;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Tests.Api.Sampling;

/// <summary>
/// Sampling as a consumer sees it: read a real dataset and ask what was read.
/// </summary>
/// <remarks>
/// The framework is covered by <see cref="SamplingTests"/>. What matters here is that the counters are
/// wired to the reads that actually happen — a sampler counting the wrong accessor, or none, reports a
/// field as cold that an application reads constantly, which is worse than not sampling at all.
/// </remarks>
public class ReadPathSamplingTests
{
    [Fact]
    public void NothingIsCountedUntilSamplingIsTurnedOn()
    {
        HollowReadStateEngine engine = Dataset();

        ReadTitles(engine);

        Assert.False(engine.HasSampleResults);
        Assert.Empty(engine.GetSampleResults());
    }

    [Fact]
    public void AFieldReadThroughTheReadStateIsCounted()
    {
        HollowReadStateEngine engine = Dataset();

        engine.SetSamplingDirector(new EnabledSamplingDirector());

        ReadTitles(engine);

        Assert.True(engine.HasSampleResults);

        // Three films, so three reads of the reference and three of the string behind it.
        Assert.Equal(3, Count(engine, "Movie.Title"));
        Assert.Equal(3, Count(engine, "String.value"));
    }

    [Fact]
    public void AFieldNothingReadsIsReportedAsCold()
    {
        HollowReadStateEngine engine = Dataset();

        engine.SetSamplingDirector(new EnabledSamplingDirector());

        ReadTitles(engine);

        // Which is the whole point: Year costs bytes in every record and nothing has asked for it.
        Assert.Equal(0, Count(engine, "Movie.Year"));
    }

    [Fact]
    public void OnlyTypesThatWereReadAreReported()
    {
        HollowReadStateEngine engine = Dataset();

        engine.SetSamplingDirector(new EnabledSamplingDirector());

        ReadTitles(engine);

        Assert.DoesNotContain(
            engine.GetSampleResults(),
            result => result.Identifier.StartsWith("ListOfString", StringComparison.Ordinal));
    }

    [Fact]
    public void TheHottestFieldComesFirst()
    {
        HollowReadStateEngine engine = Dataset();

        engine.SetSamplingDirector(new EnabledSamplingDirector());

        HollowObjectTypeReadState movies = Movies(engine);
        int year = movies.Schema.GetPosition("Year");

        foreach (int ordinal in movies.PopulatedOrdinals.EnumerateSetBits())
        {
            for (int i = 0; i < 10; i++)
            {
                movies.ReadInt(ordinal, year);
            }
        }

        ReadTitles(engine);

        Assert.Equal("Movie.Year", engine.GetSampleResults()[0].Identifier);
        Assert.Equal(30, engine.GetSampleResults()[0].SampleCount);
    }

    [Fact]
    public void ADirectorCanBeAimedAtOneFieldOfOneType()
    {
        HollowReadStateEngine engine = Dataset();

        engine.SetFieldSpecificSamplingDirector(
            TypeFilter.Include(
                ["Movie"],
                new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
                {
                    ["Movie"] = new HashSet<string>(StringComparer.Ordinal) { "Title" },
                }),
            new EnabledSamplingDirector());

        ReadTitles(engine);

        Assert.Equal(3, Count(engine, "Movie.Title"));

        // The string type was read just as often and was never asked about.
        Assert.False(engine.GetTypeState("String")!.Sampler.HasSampleResults);
    }

    [Fact]
    public void ResettingClearsEveryTypeAtOnce()
    {
        HollowReadStateEngine engine = Dataset();

        engine.SetSamplingDirector(new EnabledSamplingDirector());

        ReadTitles(engine);

        Assert.True(engine.HasSampleResults);

        engine.ResetSampling();

        Assert.False(engine.HasSampleResults);
    }

    [Fact]
    public void TheThreeThingsOneCanDoWithAListAreCountedSeparately()
    {
        HollowReadStateEngine engine = Dataset();

        engine.SetSamplingDirector(new EnabledSamplingDirector());

        HollowListTypeReadState cast = (HollowListTypeReadState)engine.GetTypeState("ListOfString")!;

        cast.Size(0);
        cast.GetElementOrdinal(0, 0);

        // Obtaining the sequence is the read that counts, as obtaining the iterator is in Java.
        _ = cast.ElementOrdinals(0);

        Assert.Equal(1, Count(engine, "ListOfString.Count"));
        Assert.Equal(1, Count(engine, "ListOfString.Get()"));
        Assert.Equal(1, Count(engine, "ListOfString.Enumerate()"));
    }

    [Fact]
    public void ASetAndAMapCountTheirOwnOperations()
    {
        HollowReadStateEngine engine = Dataset();

        engine.SetSamplingDirector(new EnabledSamplingDirector());

        HollowSetTypeReadState genres = (HollowSetTypeReadState)engine.GetTypeState("SetOfString")!;
        HollowMapTypeReadState ratings =
            (HollowMapTypeReadState)engine.GetTypeState("MapOfStringToInteger")!;

        genres.Size(0);
        _ = genres.ElementOrdinals(0);

        ratings.Size(0);
        _ = ratings.Entries(0);

        Assert.Equal(1, Count(engine, "SetOfString.Count"));
        Assert.Equal(1, Count(engine, "SetOfString.Enumerate()"));
        Assert.Equal(1, Count(engine, "MapOfStringToInteger.Count"));
        Assert.Equal(1, Count(engine, "MapOfStringToInteger.Enumerate()"));
    }

    [Fact]
    public void TheDatasetsOwnReadsAreLeftOut()
    {
        HollowReadStateEngine engine = Dataset();

        engine.SetSamplingDirector(new EnabledSamplingDirector());

        // A refresh reads records to build indexes and checksums; those are not the application's.
        using (HollowSamplingScope.EnterUpdate())
        {
            ReadTitles(engine);
        }

        Assert.False(engine.HasSampleResults);
    }

    [Fact]
    public async Task TheExclusionSurvivesAnAwaitAndAFanOut()
    {
        HollowReadStateEngine engine = Dataset();

        engine.SetSamplingDirector(new EnabledSamplingDirector());

        bool heldAfterAwait;
        bool heldOnEveryWorker = true;

        using (HollowSamplingScope.EnterUpdate())
        {
            await Task.Yield();
            heldAfterAwait = HollowSamplingScope.IsUpdate;

            Parallel.For(0, 4, _ =>
            {
                if (!HollowSamplingScope.IsUpdate)
                {
                    heldOnEveryWorker = false;
                }

                ReadTitles(engine);
            });
        }

        // Java registers a Thread, which would have matched neither: the continuation resumes
        // elsewhere, and the workers never ran on the registering thread at all.
        Assert.True(heldAfterAwait);
        Assert.True(heldOnEveryWorker);
        Assert.False(engine.HasSampleResults);
    }

    [Fact]
    public void TheApplicationIsCountedAgainOnceTheScopeCloses()
    {
        HollowReadStateEngine engine = Dataset();

        engine.SetSamplingDirector(new EnabledSamplingDirector());

        using (HollowSamplingScope.EnterUpdate())
        {
            ReadTitles(engine);
        }

        Assert.False(engine.HasSampleResults);

        ReadTitles(engine);

        Assert.True(engine.HasSampleResults);
    }

    private static long Count(HollowReadStateEngine engine, string identifier) =>
        engine.GetSampleResults()
            .Single(result => string.Equals(result.Identifier, identifier, StringComparison.Ordinal))
            .SampleCount;

    private static HollowObjectTypeReadState Movies(HollowReadStateEngine engine) =>
        (HollowObjectTypeReadState)engine.GetTypeState("Movie")!;

    /// <summary>Reads every film's title, and the string each one points at.</summary>
    private static void ReadTitles(HollowReadStateEngine engine)
    {
        HollowObjectTypeReadState movies = Movies(engine);
        HollowObjectTypeReadState strings = (HollowObjectTypeReadState)engine.GetTypeState("String")!;

        int title = movies.Schema.GetPosition("Title");

        foreach (int ordinal in movies.PopulatedOrdinals.EnumerateSetBits())
        {
            strings.ReadString(movies.ReadOrdinal(ordinal, title), 0);
        }
    }

    private static HollowReadStateEngine Dataset()
    {
        HollowWriteStateEngine engine = new();
        HollowObjectMapper mapper = new(engine);

        mapper.Add(Film(1, "Heat", 1995, "Pacino", "Crime", 8));
        mapper.Add(Film(2, "Ronin", 1998, "De Niro", "Thriller", 7));
        mapper.Add(Film(3, "Collateral", 2004, "Cruise", "Thriller", 8));

        return StateEngineRoundTripper.RoundTripSnapshot(engine);
    }

    private static Movie Film(int id, string title, int year, string actor, string genre, int rating) =>
        new()
        {
            Id = id,
            Title = title,
            Year = year,
            Cast = [actor],
            Genres = [genre],
            Ratings = new Dictionary<string, int> { ["imdb"] = rating },
        };

    [HollowPrimaryKey("Id")]
    public sealed class Movie
    {
        public required int Id { get; init; }

        public required string Title { get; init; }

        public required int Year { get; init; }

        public required List<string> Cast { get; init; }

        public required HashSet<string> Genres { get; init; }

        public required Dictionary<string, int> Ratings { get; init; }
    }
}
