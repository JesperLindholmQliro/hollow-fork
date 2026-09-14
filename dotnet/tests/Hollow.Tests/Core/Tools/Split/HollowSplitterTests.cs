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

using Hollow.Api.Objects.Generic;
using Hollow.Core;
using Hollow.Core.Index.Key;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Tools.Split;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Tests.Core.Tools.Split;

/// <summary>
/// Dividing one state into several. Java has no tests for this at all, so these are written from what
/// a shard has to be: complete on its own, holding every record its own roots reach, and holding
/// nothing twice.
/// </summary>
public class HollowSplitterTests
{
    /// <summary>A film, which is what the splitter divides.</summary>
    private sealed record Film(int Id, string Title, Studio Studio);

    /// <summary>A studio several films share, so a shard has to pull its own copy across.</summary>
    private sealed record Studio(string Name, string Country);

    /// <summary>A type nothing references, for the replicated case.</summary>
    private sealed record Setting(string Name, string Value);

    [Fact]
    public void EveryRecordLandsInExactlyOneShardAndTheShardsCoverTheInput()
    {
        HollowReadStateEngine input = Input(20);

        HollowSplitter splitter = new(new HollowSplitterOrdinalCopyDirector(4, "Film"), input);
        splitter.Split();

        Assert.Equal(4, splitter.NumberOfShards);

        List<int> found = [];

        for (int shard = 0; shard < splitter.NumberOfShards; shard++)
        {
            found.AddRange(FilmIds(splitter, shard));
        }

        // Twenty films, each in one shard and no shard holding one twice.
        Assert.Equal(20, found.Count);
        Assert.Equal(Enumerable.Range(0, 20), found.Order());
    }

    [Fact]
    public void AReferencedRecordFollowsEveryRootThatReachesIt()
    {
        HollowReadStateEngine input = Input(20);

        // Two studios across twenty films, so each is reached from several shards' roots.
        Assert.Equal(1, input.GetTypeState("Studio")!.MaxOrdinal);

        HollowSplitter splitter = new(new HollowSplitterOrdinalCopyDirector(4, "Film"), input);
        splitter.Split();

        for (int shard = 0; shard < splitter.NumberOfShards; shard++)
        {
            HollowReadStateEngine output = Shard(splitter, shard);

            // A shard has to stand on its own, so it carries a copy of whatever its films reach —
            // and only what they reach, renumbered into its own ordinal space.
            Assert.NotNull(output.GetTypeState("Studio"));
            Assert.NotEqual(HollowConstants.OrdinalNone, output.GetTypeState("Studio")!.MaxOrdinal);

            foreach (int ordinal in output.GetTypeState("Film")!.PopulatedOrdinals.EnumerateSetBits())
            {
                GenericHollowObject film = new(output, "Film", ordinal);

                // The reference resolves, and resolves to the right studio.
                Assert.Equal(
                    film.GetInt("Id") % 2 == 0 ? "A24" : "Pathe",
                    film.GetObject("Studio")!.GetObject("Name")!.GetString("value"));
            }
        }
    }

    [Fact]
    public void SplittingByKeyPutsTheSameRecordInTheSameShardEveryTime()
    {
        HollowReadStateEngine first = Input(20);

        // The same films, written in a different order, so every ordinal moves.
        HollowReadStateEngine second = Input(20, reverse: true);

        Dictionary<int, int> firstShards = ShardsByFilmId(first);
        Dictionary<int, int> secondShards = ShardsByFilmId(second);

        // A key-directed split is reproducible: the shard a film lands in is a property of the film,
        // not of where it happened to sit in the input.
        Assert.Equal(firstShards, secondShards);

        // And it is a real division rather than everything in one shard.
        Assert.True(firstShards.Values.Distinct().Count() > 1);
    }

    [Fact]
    public void AKeyWhoseHashIsNegativeStillLandsInOneShard()
    {
        HollowReadStateEngine input = Input(200);

        // Keyed on the title rather than the id: an int hashes to itself, so a small id could
        // never have hit the bug, whereas a string hashes through MurmurHash3 and half of them do.
        HollowSplitterPrimaryKeyCopyDirector director =
            new(input, 4, new PrimaryKey("Film", "Title"));

        HollowSplitter splitter = new(director, input);
        splitter.Split();

        List<int> found = [];

        for (int shard = 0; shard < splitter.NumberOfShards; shard++)
        {
            found.AddRange(FilmIds(splitter, shard));
        }

        // Java takes the key's hash modulo the shard count and treats a negative answer as "put this
        // in every shard" — so roughly half of all keys were replicated rather than placed. Over two
        // hundred films that would show up here as several hundred copies.
        Assert.Equal(200, found.Count);
        Assert.Equal(Enumerable.Range(0, 200), found.Order());

        // Some key in two hundred does hash negative, so this is actually exercising the fix.
        Assert.Contains(
            Enumerable.Range(0, 200),
            id => HollowSplitterPrimaryKeyCopyDirector.HashKey([$"Film {id}"]) < 0);
    }

    [Fact]
    public void AReplicatedTypeGoesIntoEveryShard()
    {
        HollowReadStateEngine input = Input(20, withSettings: true);

        HollowSplitterPrimaryKeyCopyDirector director = new(input, 4, new PrimaryKey("Film", "Id"));
        director.AddReplicatedTypes("Setting");

        HollowSplitter splitter = new(director, input);
        splitter.Split();

        int expected = input.GetTypeState("Setting")!.PopulatedOrdinals.Cardinality();

        Assert.NotEqual(0, expected);

        for (int shard = 0; shard < splitter.NumberOfShards; shard++)
        {
            HollowReadStateEngine output = Shard(splitter, shard);

            Assert.Equal(expected, output.GetTypeState("Setting")!.PopulatedOrdinals.Cardinality());
        }
    }

    [Fact]
    public void ATopLevelTypeTheInputDoesNotHaveIsRefused()
    {
        HollowReadStateEngine input = Input(4);

        HollowSplitter splitter = new(new HollowSplitterOrdinalCopyDirector(2, "Nonexistent"), input);

        // Java logs a warning and carries on, which quietly produces shards missing a type the caller
        // asked to split by.
        Assert.Throws<ArgumentException>(splitter.Split);
    }

    [Fact]
    public void SplittingTwiceRollsTheShardsOnToTheirNextCycle()
    {
        HollowReadStateEngine input = Input(8);

        HollowSplitter splitter = new(new HollowSplitterOrdinalCopyDirector(2, "Film"), input);

        splitter.Split();
        int[] firstPass = [.. FilmIds(splitter, 0)];

        // A second split of the same input is the same answer, rather than each shard holding
        // everything twice.
        splitter.Split();

        Assert.Equal(firstPass, FilmIds(splitter, 0));
    }

    [Fact]
    public void ASplitNeedsAtLeastOneShard() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new HollowSplitter(new HollowSplitterOrdinalCopyDirector(0, "Film"), Input(1)));

    /// <summary>Which shard each film id lands in, under a four-way key-directed split.</summary>
    private static Dictionary<int, int> ShardsByFilmId(HollowReadStateEngine input)
    {
        HollowSplitter splitter = new(
            new HollowSplitterPrimaryKeyCopyDirector(input, 4, new PrimaryKey("Film", "Id")), input);

        splitter.Split();

        Dictionary<int, int> shardsById = [];

        for (int shard = 0; shard < splitter.NumberOfShards; shard++)
        {
            foreach (int id in FilmIds(splitter, shard))
            {
                shardsById[id] = shard;
            }
        }

        return shardsById;
    }

    private static IEnumerable<int> FilmIds(HollowSplitter splitter, int shard)
    {
        HollowReadStateEngine output = Shard(splitter, shard);

        if (output.GetTypeState("Film") is not { } films)
        {
            yield break;
        }

        int position = ((HollowObjectTypeReadState)films).Schema.GetPosition("Id");

        foreach (int ordinal in films.PopulatedOrdinals.EnumerateSetBits())
        {
            yield return ((HollowObjectTypeReadState)films).ReadInt(ordinal, position);
        }
    }

    private static HollowReadStateEngine Shard(HollowSplitter splitter, int shard) =>
        StateEngineRoundTripper.RoundTripSnapshot(splitter.GetOutputShardStateEngine(shard));

    private static HollowReadStateEngine Input(int filmCount, bool reverse = false, bool withSettings = false)
    {
        HollowWriteStateEngine writeEngine = new();
        HollowObjectMapper mapper = new(writeEngine);

        mapper.InitializeTypeState(typeof(Film));

        Studio a24 = new("A24", "US");
        Studio pathe = new("Pathe", "FR");

        IEnumerable<int> ids = Enumerable.Range(0, filmCount);

        foreach (int id in reverse ? ids.Reverse() : ids)
        {
            mapper.Add(new Film(id, $"Film {id}", id % 2 == 0 ? a24 : pathe));
        }

        if (withSettings)
        {
            mapper.Add(new Setting("region", "eu"));
            mapper.Add(new Setting("tier", "premium"));
        }
        else
        {
            // Declared regardless, so that every shard has the same data model either way.
            mapper.InitializeTypeState(typeof(Setting));
        }

        return StateEngineRoundTripper.RoundTripSnapshot(writeEngine);
    }
}
