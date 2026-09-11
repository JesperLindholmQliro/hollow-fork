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
using Hollow.Core.Read.Engine.Set;
using Hollow.Core.Read.Iterator;
using Hollow.Core.Schema;
using Hollow.Core.Write;

namespace Hollow.Tests.Core.Index;

/// <summary>
/// A set or map schema may declare a hash key, which makes the producer place each element in the
/// bucket a consumer probing by those key values will look in — rather than in the bucket its ordinal
/// hashes to. Producer and consumer have to agree exactly on that hash, so these tests look the
/// elements up through the read state rather than comparing hashes directly.
/// </summary>
public class HashKeyTests
{
    private sealed record Movie(int Id, string Title, double Rating);

    /// <summary>
    /// Builds a dataset of movies plus a set and a map of them, both keyed by the given hash key paths.
    /// </summary>
    private sealed class Model
    {
        internal Model(string[]? hashKeyPaths, int numShards = 1)
        {
            StringSchema = new HollowObjectSchema("String", 1);
            StringSchema.AddField("value", FieldType.String);

            MovieSchema = new HollowObjectSchema("Movie", 3);
            MovieSchema.AddField("id", FieldType.Int);
            MovieSchema.AddField("title", FieldType.Reference, "String");
            MovieSchema.AddField("rating", FieldType.Double);

            HollowSetSchema setSchema = new("MovieSet", "Movie", hashKeyPaths);
            HollowMapSchema mapSchema = new("MovieMap", "Movie", "String", hashKeyPaths);

            Engine = new HollowWriteStateEngine { RandomizedTag = 1 };
            Engine.AddTypeState(new HollowObjectTypeWriteState(StringSchema, numShards));
            Engine.AddTypeState(new HollowObjectTypeWriteState(MovieSchema, numShards));
            Engine.AddTypeState(new HollowSetTypeWriteState(setSchema, numShards));
            Engine.AddTypeState(new HollowMapTypeWriteState(mapSchema, numShards));
        }

        internal HollowObjectSchema StringSchema { get; }

        internal HollowObjectSchema MovieSchema { get; }

        internal HollowWriteStateEngine Engine { get; }

        internal int AddString(string value)
        {
            HollowObjectWriteRecord record = new(StringSchema);
            record.SetString("value", value);
            return Engine.Add("String", record);
        }

        internal int AddMovie(Movie movie)
        {
            HollowObjectWriteRecord record = new(MovieSchema);
            record.SetInt("id", movie.Id);
            record.SetReference("title", AddString(movie.Title));
            record.SetDouble("rating", movie.Rating);
            return Engine.Add("Movie", record);
        }

        /// <summary>Adds one set and one map, both holding every given movie.</summary>
        internal (int SetOrdinal, int MapOrdinal) AddCollections(IEnumerable<Movie> movies)
        {
            HollowSetWriteRecord set = new();
            HollowMapWriteRecord map = new();

            foreach (Movie movie in movies)
            {
                int ordinal = AddMovie(movie);
                set.AddElement(ordinal);
                map.AddEntry(ordinal, AddString($"value-of-{movie.Id}"));
            }

            return (Engine.Add("MovieSet", set), Engine.Add("MovieMap", map));
        }

        internal HollowReadStateEngine ReadSnapshot()
        {
            using MemoryStream stream = new();
            new HollowBlobWriter(Engine).WriteSnapshot(stream);
            stream.Position = 0;

            HollowReadStateEngine readEngine = new();
            new HollowBlobReader(readEngine).ReadSnapshot(stream);
            return readEngine;
        }

        internal void WriteDelta(HollowReadStateEngine consumer)
        {
            using MemoryStream stream = new();
            new HollowBlobWriter(Engine).WriteDelta(stream);
            stream.Position = 0;

            new HollowBlobReader(consumer).ApplyDelta(stream);
        }
    }

    /// <summary>
    /// Ids deliberately differ from the ordinals the records land on: a key hashed by ordinal instead of
    /// by key value would otherwise agree by accident.
    /// </summary>
    private static Movie[] Movies(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new Movie((i * 7) + 13, $"movie-{i}", i / 4d))];

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void ElementsAreFoundByASingleFieldHashKey(int numShards)
    {
        Model model = new(["id"], numShards);
        Movie[] movies = Movies(60);
        (int setOrdinal, int mapOrdinal) = model.AddCollections(movies);

        HollowReadStateEngine consumer = model.ReadSnapshot();
        HollowSetTypeReadState sets = Assert.IsType<HollowSetTypeReadState>(consumer.GetTypeState("MovieSet"));
        HollowMapTypeReadState maps = Assert.IsType<HollowMapTypeReadState>(consumer.GetTypeState("MovieMap"));

        Dictionary<int, int> entries = ReadEntries(maps, mapOrdinal);

        foreach (Movie movie in movies)
        {
            int elementOrdinal = sets.FindElement(setOrdinal, movie.Id);
            Assert.NotEqual(HollowConstants.OrdinalNone, elementOrdinal);
            Assert.Contains(elementOrdinal, sets.OrdinalIterator(setOrdinal).AsEnumerable());

            // The map's key ordinals index the same Movie records the set holds.
            Assert.Equal(elementOrdinal, maps.FindKey(mapOrdinal, movie.Id));
            Assert.Equal(entries[elementOrdinal], maps.FindValue(mapOrdinal, movie.Id));
        }

        Assert.Equal(HollowConstants.OrdinalNone, sets.FindElement(setOrdinal, -999));
        Assert.Equal(HollowConstants.OrdinalNone, maps.FindKey(mapOrdinal, -999));
        Assert.Equal(-1L, maps.FindEntry(mapOrdinal, -999));
    }

    /// <summary>
    /// A key field reached through a reference is the case the producer and consumer are most likely to
    /// disagree on, because each reads it from a different representation of the record.
    /// </summary>
    [Fact]
    public void AHashKeyCanTraverseAReference()
    {
        Model model = new(["title.value"]);
        Movie[] movies = Movies(40);
        (int setOrdinal, int mapOrdinal) = model.AddCollections(movies);

        HollowReadStateEngine consumer = model.ReadSnapshot();
        HollowSetTypeReadState sets = Assert.IsType<HollowSetTypeReadState>(consumer.GetTypeState("MovieSet"));
        HollowMapTypeReadState maps = Assert.IsType<HollowMapTypeReadState>(consumer.GetTypeState("MovieMap"));

        foreach (Movie movie in movies)
        {
            int elementOrdinal = sets.FindElement(setOrdinal, movie.Title);
            Assert.NotEqual(HollowConstants.OrdinalNone, elementOrdinal);
            Assert.Equal(elementOrdinal, maps.FindKey(mapOrdinal, movie.Title));
        }

        Assert.Equal(HollowConstants.OrdinalNone, sets.FindElement(setOrdinal, "no-such-movie"));
    }

    [Fact]
    public void AHashKeyCanSpanSeveralFields()
    {
        Model model = new(["id", "title.value", "rating"]);
        Movie[] movies = Movies(40);
        (int setOrdinal, _) = model.AddCollections(movies);

        HollowSetTypeReadState sets =
            Assert.IsType<HollowSetTypeReadState>(model.ReadSnapshot().GetTypeState("MovieSet"));

        foreach (Movie movie in movies)
        {
            Assert.NotEqual(
                HollowConstants.OrdinalNone,
                sets.FindElement(setOrdinal, movie.Id, movie.Title, movie.Rating));
        }

        // Every field has to match, not just some.
        Assert.Equal(
            HollowConstants.OrdinalNone, sets.FindElement(setOrdinal, movies[0].Id, movies[1].Title, movies[0].Rating));

        // And a key of the wrong length is a miss rather than an error.
        Assert.Equal(HollowConstants.OrdinalNone, sets.FindElement(setOrdinal, movies[0].Id));
    }

    /// <summary>
    /// The bucket a key hashes to depends on the record's key fields, not its ordinal, so a delta that
    /// moves records to different ordinals must leave lookups working.
    /// </summary>
    [Fact]
    public void HashKeyLookupsSurviveADelta()
    {
        Model model = new(["id"]);
        Movie[] first = Movies(30);
        model.AddCollections(first);

        HollowReadStateEngine consumer = model.ReadSnapshot();

        model.Engine.PrepareForNextCycle();
        model.Engine.RandomizedTag = 2;

        // Drop some records, which frees ordinals the new ones then reuse.
        Movie[] second = [.. first.Where((_, index) => index % 3 != 0), .. Movies(45).Skip(30)];
        (int setOrdinal, int mapOrdinal) = model.AddCollections(second);

        model.WriteDelta(consumer);

        HollowSetTypeReadState sets = Assert.IsType<HollowSetTypeReadState>(consumer.GetTypeState("MovieSet"));
        HollowMapTypeReadState maps = Assert.IsType<HollowMapTypeReadState>(consumer.GetTypeState("MovieMap"));

        foreach (Movie movie in second)
        {
            Assert.NotEqual(HollowConstants.OrdinalNone, sets.FindElement(setOrdinal, movie.Id));
            Assert.NotEqual(HollowConstants.OrdinalNone, maps.FindKey(mapOrdinal, movie.Id));
        }

        foreach (Movie movie in first.Except(second))
        {
            Assert.Equal(HollowConstants.OrdinalNone, sets.FindElement(setOrdinal, movie.Id));
        }
    }

    /// <summary>
    /// Without a declared key there is nothing to look elements up by, and the lookups say so rather
    /// than returning something arbitrary.
    /// </summary>
    [Fact]
    public void WithoutADeclaredKeyLookupsFindNothing()
    {
        Model model = new(hashKeyPaths: null);
        (int setOrdinal, int mapOrdinal) = model.AddCollections(Movies(10));

        HollowReadStateEngine consumer = model.ReadSnapshot();
        HollowSetTypeReadState sets = Assert.IsType<HollowSetTypeReadState>(consumer.GetTypeState("MovieSet"));
        HollowMapTypeReadState maps = Assert.IsType<HollowMapTypeReadState>(consumer.GetTypeState("MovieMap"));

        Assert.Null(sets.KeyDeriver);
        Assert.Null(maps.KeyDeriver);

        Assert.Equal(HollowConstants.OrdinalNone, sets.FindElement(setOrdinal, 0));
        Assert.Equal(HollowConstants.OrdinalNone, maps.FindKey(mapOrdinal, 0));

        // Ordinal-based access is unaffected.
        Assert.Equal(10, sets.Size(setOrdinal));
        Assert.Equal(10, maps.Size(mapOrdinal));
    }

    /// <summary>
    /// Declaring a hash key must not change what the records contain, only where in the table each one
    /// is placed.
    /// </summary>
    [Fact]
    public void AHashKeyDoesNotChangeTheRecordsThemselves()
    {
        Movie[] movies = Movies(25);

        Model keyed = new(["id"]);
        (int keyedSet, int keyedMap) = keyed.AddCollections(movies);

        Model unkeyed = new(hashKeyPaths: null);
        (int unkeyedSet, int unkeyedMap) = unkeyed.AddCollections(movies);

        HollowReadStateEngine keyedConsumer = keyed.ReadSnapshot();
        HollowReadStateEngine unkeyedConsumer = unkeyed.ReadSnapshot();

        HollowSetTypeReadState keyedSets =
            Assert.IsType<HollowSetTypeReadState>(keyedConsumer.GetTypeState("MovieSet"));
        HollowSetTypeReadState unkeyedSets =
            Assert.IsType<HollowSetTypeReadState>(unkeyedConsumer.GetTypeState("MovieSet"));

        Assert.Equal(
            unkeyedSets.OrdinalIterator(unkeyedSet).AsEnumerable().Order(),
            keyedSets.OrdinalIterator(keyedSet).AsEnumerable().Order());

        HollowMapTypeReadState keyedMaps =
            Assert.IsType<HollowMapTypeReadState>(keyedConsumer.GetTypeState("MovieMap"));
        HollowMapTypeReadState unkeyedMaps =
            Assert.IsType<HollowMapTypeReadState>(unkeyedConsumer.GetTypeState("MovieMap"));

        Assert.Equal(unkeyedMaps.Size(unkeyedMap), keyedMaps.Size(keyedMap));
        Assert.Equal(ReadEntries(unkeyedMaps, unkeyedMap), ReadEntries(keyedMaps, keyedMap));
    }

    /// <summary>
    /// The buckets of a keyed collection are laid out by key value rather than by element ordinal, so
    /// the ordinal-based lookups no longer find anything. This is inherent to the format, not a
    /// limitation of the port: a consumer of a keyed collection has to look elements up by key.
    /// </summary>
    [Fact]
    public void ADeclaredKeyReplacesOrdinalBasedLookup()
    {
        Model model = new(["id"]);
        Movie[] movies = Movies(40);
        (int setOrdinal, int mapOrdinal) = model.AddCollections(movies);

        HollowReadStateEngine consumer = model.ReadSnapshot();
        HollowSetTypeReadState sets = Assert.IsType<HollowSetTypeReadState>(consumer.GetTypeState("MovieSet"));
        HollowMapTypeReadState maps = Assert.IsType<HollowMapTypeReadState>(consumer.GetTypeState("MovieMap"));

        // Iteration still yields every element, because it walks the buckets rather than probing them.
        Assert.Equal(movies.Length, sets.OrdinalIterator(setOrdinal).AsEnumerable().Count());
        Assert.Equal(movies.Length, ReadEntries(maps, mapOrdinal).Count);

        // Probing by ordinal, however, looks in the bucket the ordinal hashes to, which is not where a
        // keyed collection put it. An individual probe may still land on its element by chance, so the
        // claim is that ordinal-based lookup is no longer reliable, not that it never succeeds.
        int[] elementOrdinals = [.. sets.OrdinalIterator(setOrdinal).AsEnumerable()];

        Assert.NotEqual(
            elementOrdinals.Length, elementOrdinals.Count(o => sets.Contains(setOrdinal, o)));
        Assert.NotEqual(
            elementOrdinals.Length,
            elementOrdinals.Count(o => maps.Get(mapOrdinal, o) != HollowConstants.OrdinalNone));
    }

    /// <summary>Reads a map whole, by iteration rather than by probing.</summary>
    private static Dictionary<int, int> ReadEntries(HollowMapTypeReadState maps, int ordinal)
    {
        Dictionary<int, int> entries = [];
        IHollowMapEntryOrdinalIterator iterator = maps.OrdinalIterator(ordinal);

        while (iterator.Next())
        {
            entries[iterator.Key] = iterator.Value;
        }

        return entries;
    }

    /// <summary>
    /// The schema carries the key, so it has to survive being written into the blob and read back.
    /// </summary>
    [Fact]
    public void TheDeclaredKeyRoundTripsThroughTheBlob()
    {
        Model model = new(["id", "title.value"]);
        model.AddCollections(Movies(5));

        HollowReadStateEngine consumer = model.ReadSnapshot();

        HollowSetSchema setSchema =
            Assert.IsType<HollowSetTypeReadState>(consumer.GetTypeState("MovieSet")).Schema;
        Assert.Equal(new PrimaryKey("Movie", "id", "title.value"), setSchema.HashKey);

        HollowMapSchema mapSchema =
            Assert.IsType<HollowMapTypeReadState>(consumer.GetTypeState("MovieMap")).Schema;
        Assert.Equal(new PrimaryKey("Movie", "id", "title.value"), mapSchema.HashKey);
    }
}
