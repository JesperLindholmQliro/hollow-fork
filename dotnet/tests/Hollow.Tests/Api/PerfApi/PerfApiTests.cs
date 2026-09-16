/*
 *  Copyright 2021 Netflix, Inc.
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

using Hollow.Api.PerfApi;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Engine;
using Hollow.Core.Schema;
using Hollow.Core.Write;

namespace Hollow.Tests.Api.PerfApi;

/// <summary>
/// Exercises the performance API: reads that hand back a reference rather than an object.
/// </summary>
/// <remarks>
/// Netflix Hollow has no tests for <c>api.perfapi</c> at all — the package is exercised only through
/// the generated APIs its code generator emits, which this port does not have. These are written
/// against the runtime directly.
/// </remarks>
public class PerfApiTests
{
    [Fact]
    public void AReferenceCarriesATypeAndAnOrdinal()
    {
        HollowRef reference = HollowRef.Create(3, 42);

        Assert.Equal(3, reference.Type);
        Assert.Equal(42, reference.Ordinal);
        Assert.False(reference.IsNull);
        Assert.True(reference.IsOfType(3));
        Assert.False(reference.IsOfType(4));
    }

    [Fact]
    public void ANullReferenceIsNullWhicheverWayItWasMade()
    {
        Assert.True(HollowRef.Null.IsNull);

        // The case Java's toRefWithTypeMasked gets wrong: OR-ing -1 into a masked type overwrites the
        // type bits and yields a reference that is neither null nor of the type it claims.
        Assert.True(HollowRef.Create(HollowRef.ToTypeMasked(2), -1).IsNull);
    }

    [Fact]
    public void AReferenceOfTheWrongTypeIsRefused()
    {
        Catalogue catalogue = new();

        HollowRef movie = catalogue.Movies.RefForOrdinal(0);

        // Reading a Movie reference as a ListOfMovie would otherwise read a different record and
        // answer with nonsense, which is the whole reason a reference carries its type.
        ArgumentException failure = Assert.Throws<ArgumentException>(() => catalogue.Films.Size(movie));

        Assert.Contains("Movie", failure.Message, StringComparison.Ordinal);
        Assert.Contains("ListOfMovie", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANullReferenceIsRefusedAsANullArgument()
    {
        Catalogue catalogue = new();

        // Java throws NullPointerException here, which claims the fault is in the callee.
        Assert.Throws<ArgumentNullException>(() => catalogue.Movies.GetId(HollowRef.Null));
    }

    [Fact]
    public void AnObjectTypeReadsItsFields()
    {
        Catalogue catalogue = new();

        HollowRef movie = catalogue.Movies.RefForOrdinal(0);

        Assert.Equal(1, catalogue.Movies.GetId(movie));

        // A reference field hands back a reference into the type the schema says it points at.
        Assert.Equal("Heat", catalogue.Strings.GetValue(catalogue.Movies.GetTitle(movie)));
    }

    [Fact]
    public void AListReadsItsElements()
    {
        Catalogue catalogue = new();

        HollowRef films = catalogue.Films.RefForOrdinal(0);

        Assert.Equal(3, catalogue.Films.Size(films));
        Assert.Equal(1, catalogue.Movies.GetId(catalogue.Films.Get(films, 0)));

        Assert.Equal(
            ["Heat", "Ronin", "Collateral"],
            catalogue.Films.Elements(films).Select(catalogue.TitleOf));
    }

    [Fact]
    public void ASetFindsAnElementByItsHashKey()
    {
        Catalogue catalogue = new();

        HollowRef set = catalogue.Genres.RefForOrdinal(0);

        Assert.Equal(2, catalogue.Genres.Size(set));
        Assert.Equal("Crime", catalogue.Strings.GetValue(catalogue.Genres.FindElement(set, "Crime")));

        // Nothing there is the null reference, not a reference to ordinal -1.
        Assert.True(catalogue.Genres.FindElement(set, "Musical").IsNull);
    }

    [Fact]
    public void AMapFindsAKeyAndAValue()
    {
        Catalogue catalogue = new();

        HollowRef ratings = catalogue.Ratings.RefForOrdinal(0);

        Assert.Equal(2, catalogue.Ratings.Size(ratings));
        Assert.Equal("imdb", catalogue.Strings.GetValue(catalogue.Ratings.FindKey(ratings, "imdb")));
        Assert.Equal(1, catalogue.Movies.GetId(catalogue.Ratings.FindValue(ratings, "imdb")));

        Assert.True(catalogue.Ratings.FindValue(ratings, "nowhere").IsNull);

        Assert.Equal(
            ["imdb", "rt"],
            catalogue.Ratings.Entries(ratings)
                .Select(entry => catalogue.Strings.GetValue(entry.Key))
                .Order());
    }

    [Fact]
    public void ACollectionCanBeReadAsAnOrdinaryOne()
    {
        Catalogue catalogue = new();

        IReadOnlyList<string?> films = catalogue.Films.BackedList(
            catalogue.Films.RefForOrdinal(0), catalogue.TitleOf);

        Assert.Equal(3, films.Count);
        Assert.Equal("Ronin", films[1]);
        Assert.Equal(["Heat", "Ronin", "Collateral"], films);

        IReadOnlySet<string?> genres = catalogue.Genres.BackedSet(
            catalogue.Genres.RefForOrdinal(0), catalogue.Strings.GetValue, genre => genre);

        Assert.Equal(2, genres.Count);
        Assert.Contains("Crime", genres);
        Assert.DoesNotContain("Musical", genres);

        IReadOnlyDictionary<string, string?> ratings = catalogue.Ratings.BackedMap(
            catalogue.Ratings.RefForOrdinal(0),
            reference => catalogue.Strings.GetValue(reference)!,
            catalogue.TitleOf,
            key => key);

        Assert.Equal(2, ratings.Count);
        Assert.Equal("Heat", ratings["imdb"]);
        Assert.False(ratings.ContainsKey("nowhere"));
        Assert.Throws<KeyNotFoundException>(() => ratings["nowhere"]);
    }

    [Fact]
    public void ABackedSetWithoutAHashKeyStillAnswersContains()
    {
        Catalogue catalogue = new();

        // No extractor, so there is no way to probe the record's hash table. Java throws here; this
        // scans, which is slower but is the right answer rather than a refusal.
        IReadOnlySet<string?> genres =
            catalogue.Genres.BackedSet(catalogue.Genres.RefForOrdinal(0), catalogue.Strings.GetValue);

        Assert.Contains("Crime", genres);
        Assert.DoesNotContain("Musical", genres);
    }

    [Fact]
    public void ATypeTheDatasetDoesNotHaveIsNotAnError()
    {
        Catalogue catalogue = new();

        HollowListTypePerfApi missing = new(catalogue.DataAccess, "ListOfNothing", catalogue.Api);

        // A consumer built against a newer model keeps working against an older dataset: the type is
        // absent, its reads go to a missing-data access, and nothing throws at construction.
        Assert.True(missing.IsMissingType);
        Assert.Equal(0, missing.Size(missing.RefForOrdinal(0)));
    }

    [Fact]
    public void TheCacheBuildsAWrapperPerRecord()
    {
        Catalogue catalogue = new();

        HollowPerfApiCache<Wrapper> cache = new(catalogue.Movies, reference => new Wrapper(reference));

        Assert.Equal(3, cache.Count);

        Wrapper first = cache.Get(catalogue.Movies.RefForOrdinal(0))!;

        // The same object each time, which is what a cache is for.
        Assert.Same(first, cache.Get(catalogue.Movies.RefForOrdinal(0)));
        Assert.Equal(catalogue.Movies.RefForOrdinal(0), first.Reference);
    }

    [Fact]
    public void TheCacheCarriesOverWhatDidNotChange()
    {
        Catalogue catalogue = new();

        HollowPerfApiCache<Wrapper> before = new(catalogue.Movies, reference => new Wrapper(reference));

        Wrapper keptBefore = before.Get(catalogue.Movies.RefForOrdinal(0))!;

        // A second cycle that keeps the first two films, drops the third and adds a fourth.
        catalogue.SecondCycle();

        int built = 0;
        HollowPerfApiCache<Wrapper> after = new(
            catalogue.Movies,
            reference =>
            {
                built++;

                return new Wrapper(reference);
            },
            before);

        // A record whose ordinal is still populated has not changed — Hollow gives a changed record a
        // new ordinal — so its wrapper is the one built before the transition.
        Assert.Same(keptBefore, after.Get(catalogue.Movies.RefForOrdinal(0)));

        // Only what actually moved was rebuilt, which is the whole point of passing the old cache in.
        Assert.Equal(1, built);
    }

    [Fact]
    public void ARecordTheTransitionRemovedIsStillReadable()
    {
        Catalogue catalogue = new();

        HollowPerfApiCache<Wrapper> before = new(catalogue.Movies, reference => new Wrapper(reference));

        HollowRef dropped = catalogue.Movies.RefForOrdinal(2);
        Wrapper wasThere = before.Get(dropped)!;

        catalogue.SecondCycle();

        Assert.False(catalogue.Movies.TypeAccess.TypeState.PopulatedOrdinals.Get(2));

        HollowPerfApiCache<Wrapper> after =
            new(catalogue.Movies, reference => new Wrapper(reference), before);

        // Deliberate, and the reason the array is sized to cover the previous populated set too: a
        // caller working out what a transition did has to read what went away as well as what
        // arrived, and the storage behind it has not been reused yet.
        Assert.Same(wasThere, after.Get(dropped));
        Assert.Equal("Collateral", catalogue.TitleOf(dropped));
    }

    [Fact]
    public void ARecordRemovedTwoTransitionsAgoIsNot()
    {
        Catalogue catalogue = new();

        HollowPerfApiCache<Wrapper> cache = new(catalogue.Movies, reference => new Wrapper(reference));

        HollowRef dropped = catalogue.Movies.RefForOrdinal(2);

        catalogue.SecondCycle();
        cache = new HollowPerfApiCache<Wrapper>(
            catalogue.Movies, reference => new Wrapper(reference), cache);

        catalogue.ThirdCycle();
        cache = new HollowPerfApiCache<Wrapper>(
            catalogue.Movies, reference => new Wrapper(reference), cache);

        // It survives one transition, not two. By now the slot is a hole like any other.
        Assert.Null(cache.Get(dropped));
    }

    /// <summary>A wrapper of the shape a generated performance API emits.</summary>
    private sealed class Wrapper(HollowRef reference) : HollowRefObject(reference);

    /// <summary>The per-type API a generator would emit for an object type, written by hand.</summary>
    private sealed class MovieTypePerfApi(IHollowDataAccess dataAccess, HollowPerformanceApi api)
        : HollowObjectTypePerfApi(dataAccess, "Movie", api, "Id", "Title")
    {
        internal int GetId(HollowRef reference) =>
            TypeAccess.ReadInt(Ordinal(reference), FieldIndexes[0]);

        internal HollowRef GetTitle(HollowRef reference) =>
            HollowRef.Create(
                ReferenceMaskedTypeIdentifiers[1],
                TypeAccess.ReadOrdinal(Ordinal(reference), FieldIndexes[1]));
    }

    private sealed class StringTypePerfApi(IHollowDataAccess dataAccess, HollowPerformanceApi api)
        : HollowObjectTypePerfApi(dataAccess, "String", api, "value")
    {
        internal string? GetValue(HollowRef reference) =>
            TypeAccess.ReadString(Ordinal(reference), FieldIndexes[0]);
    }

    /// <summary>A film catalogue, and a performance API over it.</summary>
    private sealed class Catalogue
    {
        private readonly HollowWriteStateEngine _writeEngine = new();
        private readonly HollowReadStateEngine _readEngine = new();
        private readonly HollowObjectSchema _movie;
        private readonly HollowObjectSchema _string;

        internal Catalogue()
        {
            _string = new HollowObjectSchema("String", 1, "value");
            _string.AddField("value", FieldType.String);

            _movie = new HollowObjectSchema("Movie", 2, "Id");
            _movie.AddField("Id", FieldType.Int);
            _movie.AddField("Title", FieldType.Reference, "String");

            _writeEngine.AddTypeState(new HollowObjectTypeWriteState(_string));
            _writeEngine.AddTypeState(new HollowObjectTypeWriteState(_movie));
            _writeEngine.AddTypeState(
                new HollowListTypeWriteState(new HollowListSchema("ListOfMovie", "Movie")));
            _writeEngine.AddTypeState(
                new HollowSetTypeWriteState(new HollowSetSchema("SetOfString", "String", "value")));
            _writeEngine.AddTypeState(
                new HollowMapTypeWriteState(
                    new HollowMapSchema("MapOfStringToMovie", "String", "Movie", "value")));

            HollowListWriteRecord films = new();
            HollowSetWriteRecord genres = new();
            HollowMapWriteRecord ratings = new();

            foreach (string title in new[] { "Heat", "Ronin", "Collateral" })
            {
                films.AddElement(AddMovie(films.Count + 1, title));
            }

            genres.AddElement(AddString("Crime"));
            genres.AddElement(AddString("Drama"));

            ratings.AddEntry(AddString("imdb"), 0);
            ratings.AddEntry(AddString("rt"), 0);

            _writeEngine.Add("ListOfMovie", films);
            _writeEngine.Add("SetOfString", genres);
            _writeEngine.Add("MapOfStringToMovie", ratings);

            RoundTrip();
            Rebuild();
        }

        internal HollowPerformanceApi Api { get; private set; } = null!;

        internal IHollowDataAccess DataAccess => _readEngine;

        internal MovieTypePerfApi Movies { get; private set; } = null!;

        internal StringTypePerfApi Strings { get; private set; } = null!;

        internal HollowListTypePerfApi Films { get; private set; } = null!;

        internal HollowSetTypePerfApi Genres { get; private set; } = null!;

        internal HollowMapTypePerfApi Ratings { get; private set; } = null!;

        /// <summary>The title of the film a reference points at.</summary>
        internal string? TitleOf(HollowRef movie) => Strings.GetValue(Movies.GetTitle(movie));

        /// <summary>
        /// Keeps the first two films, drops the third, adds a fourth.
        /// </summary>
        /// <remarks>
        /// Ordinals 0 and 1 stay populated across the transition, ordinal 2 goes, and a new one
        /// arrives — which is exactly the shape the cache's carry-over is written for.
        /// </remarks>
        internal void SecondCycle() => Cycle((1, "Heat"), (2, "Ronin"), (4, "Ali"));

        /// <summary>Another cycle changing nothing, to age the previous one out.</summary>
        internal void ThirdCycle() => Cycle((1, "Heat"), (2, "Ronin"), (4, "Ali"));

        private void Cycle(params (int Id, string Title)[] films)
        {
            _writeEngine.PrepareForNextCycle();

            HollowListWriteRecord list = new();

            foreach ((int id, string title) in films)
            {
                list.AddElement(AddMovie(id, title));
            }

            _writeEngine.Add("ListOfMovie", list);

            RoundTrip(delta: true);

            // Each type API holds a data access, and a delta replaces the storage under it, so they
            // are rebuilt. A generated API is rebuilt per transition for the same reason.
            Rebuild();
        }

        private void Rebuild()
        {
            Api = new HollowPerformanceApi(_readEngine);
            Movies = new MovieTypePerfApi(_readEngine, Api);
            Strings = new StringTypePerfApi(_readEngine, Api);
            Films = new HollowListTypePerfApi(_readEngine, "ListOfMovie", Api);
            Genres = new HollowSetTypePerfApi(_readEngine, "SetOfString", Api);
            Ratings = new HollowMapTypePerfApi(_readEngine, "MapOfStringToMovie", Api);
        }

        private int AddMovie(int id, string title)
        {
            HollowObjectWriteRecord movie = new(_movie);
            movie.SetInt("Id", id);
            movie.SetReference("Title", AddString(title));

            return _writeEngine.Add("Movie", movie);
        }

        private int AddString(string value)
        {
            HollowObjectWriteRecord record = new(_string);
            record.SetString("value", value);

            return _writeEngine.Add("String", record);
        }

        private void RoundTrip(bool delta = false)
        {
            _writeEngine.PrepareForWrite();

            using MemoryStream stream = new();

            if (delta)
            {
                new HollowBlobWriter(_writeEngine).WriteDelta(stream);
                stream.Position = 0;
                new HollowBlobReader(_readEngine).ApplyDelta(stream);
            }
            else
            {
                new HollowBlobWriter(_writeEngine).WriteSnapshot(stream);
                stream.Position = 0;
                new HollowBlobReader(_readEngine).ReadSnapshot(stream);
            }
        }
    }
}
