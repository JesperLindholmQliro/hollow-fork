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

using Hollow.Api.Objects;
using Hollow.Api.Objects.Generic;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Missing;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Tests.Api;

/// <summary>
/// Traversing a dataset with no generated code at all.
/// </summary>
/// <remarks>
/// This is the layer that makes the typed runtime useful before a generator exists: every reference is
/// followed by name and comes back as another generic record. It is also how the record wrappers, the
/// delegates and the missing-data fallback get exercised, since the generic records are the only
/// implementations of them until generated code arrives.
/// </remarks>
public class GenericRecordTests
{
    private sealed record Actor(string Name);

    private sealed record Movie(
        int Id,
        string Title,
        int Year,
        double Rating,
        decimal Budget,
        Actor Lead,
        List<Actor> Cast,
        HashSet<string> Tags,
        Dictionary<string, Actor> Roles);

    private static readonly Movie TheMatrix = new(
        1,
        "The Matrix",
        1999,
        8.7,
        63_000_000m,
        new Actor("Keanu Reeves"),
        [new Actor("Keanu Reeves"), new Actor("Laurence Fishburne")],
        ["sci-fi", "action"],
        new Dictionary<string, Actor>
        {
            ["Neo"] = new Actor("Keanu Reeves"),
            ["Morpheus"] = new Actor("Laurence Fishburne"),
        });

    private static HollowReadStateEngine Read(params Movie[] movies)
    {
        HollowWriteStateEngine writeEngine = new();
        HollowObjectMapper mapper = new(writeEngine);
        mapper.InitializeTypeState(typeof(Movie));

        foreach (Movie movie in movies)
        {
            mapper.Add(movie);
        }

        return StateEngineRoundTripper.RoundTripSnapshot(writeEngine);
    }

    /// <summary>The first populated record of a type, as a generic object.</summary>
    private static GenericHollowObject First(HollowReadStateEngine engine, string typeName) =>
        new(engine, typeName, engine.GetTypeState(typeName)!.PopulatedOrdinals.NextSetBit(0));

    [Fact]
    public void ScalarFieldsReadBackByName()
    {
        GenericHollowObject movie = First(Read(TheMatrix), "Movie");

        Assert.Equal(1, movie.GetInt("Id"));
        Assert.Equal(1999, movie.GetInt("Year"));
        Assert.Equal(8.7, movie.GetDouble("Rating"));

        // The object mapper stores a string as a reference to a shared String record, so the title is
        // one hop away rather than inline.
        Assert.Equal("The Matrix", movie.GetObject("Title")!.GetString("value"));
    }

    /// <summary>
    /// The decimal field type is this port's own; it has to read back through the generic layer like
    /// any other field. A non-nullable decimal is stored inline, as the mapper does for every
    /// non-nullable value scalar.
    /// </summary>
    [Fact]
    public void ADecimalFieldReadsBackThroughTheGenericLayer()
    {
        GenericHollowObject movie = First(Read(TheMatrix), "Movie");

        Assert.Equal(63_000_000m, movie.GetDecimal("Budget"));
    }

    [Fact]
    public void AReferenceIsFollowedByName()
    {
        GenericHollowObject movie = First(Read(TheMatrix), "Movie");

        GenericHollowObject lead = movie.GetObject("Lead")!;

        Assert.Equal("Keanu Reeves", lead.GetObject("Name")!.GetString("value"));
    }

    [Fact]
    public void AListReadsAsAReadOnlyList()
    {
        GenericHollowObject movie = First(Read(TheMatrix), "Movie");

        GenericHollowList cast = movie.GetList("Cast")!;

        Assert.Equal(2, cast.Count);

        // It is an IReadOnlyList, so LINQ works on it without a shim.
        Assert.Equal(
            ["Keanu Reeves", "Laurence Fishburne"],
            cast.Cast<GenericHollowObject>()
                .Select(actor => actor.GetObject("Name")!.GetString("value"))
                .Order(StringComparer.Ordinal));

        Assert.Equal(
            "Keanu Reeves", cast.GetObject(0)!.GetObject("Name")!.GetString("value"));
    }

    [Fact]
    public void ASetReadsAsAReadOnlyCollection()
    {
        GenericHollowObject movie = First(Read(TheMatrix), "Movie");

        GenericHollowSet tags = movie.GetSet("Tags")!;

        Assert.Equal(2, tags.Count);
        Assert.Equal(
            ["action", "sci-fi"],
            tags.Cast<GenericHollowObject>()
                .Select(tag => tag.GetString("value"))
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AMapReadsAsAReadOnlyCollectionOfPairs()
    {
        GenericHollowObject movie = First(Read(TheMatrix), "Movie");

        GenericHollowMap roles = movie.GetMap("Roles")!;

        Assert.Equal(2, roles.Count);

        Dictionary<string, string> byRole = roles.ToDictionary(
            entry => ((GenericHollowObject)entry.Key).GetString("value")!,
            entry => ((GenericHollowObject)entry.Value).GetObject("Name")!.GetString("value")!,
            StringComparer.Ordinal);

        Assert.Equal("Keanu Reeves", byRole["Neo"]);
        Assert.Equal("Laurence Fishburne", byRole["Morpheus"]);
    }

    /// <summary>
    /// A set is laid out by element ordinal unless the type declares a hash key, so a membership test
    /// given a record handle can probe the hash table rather than walking every element.
    /// </summary>
    [Fact]
    public void ASetFindsAnElementItWasGivenAHandleTo()
    {
        HollowReadStateEngine engine = Read(TheMatrix);
        GenericHollowObject movie = First(engine, "Movie");

        GenericHollowSet tags = movie.GetSet("Tags")!;
        IHollowRecord anElement = tags.First();

        Assert.True(tags.Contains(anElement));

        // A record of the right type at an ordinal the set does not hold is not a member.
        GenericHollowObject notAMember = new(engine, "String", 99);
        Assert.False(tags.Contains(notAMember));
    }

    [Fact]
    public void AMapLooksUpAValueByAKeyHandle()
    {
        GenericHollowObject movie = First(Read(TheMatrix), "Movie");

        GenericHollowMap roles = movie.GetMap("Roles")!;
        IHollowRecord aKey = roles.Keys.First();

        Assert.True(roles.ContainsKey(aKey));
        Assert.NotNull(roles[aKey]);

        // The value it maps to is the one iteration pairs with that key.
        KeyValuePair<IHollowRecord, IHollowRecord> expected = roles.First(entry => entry.Key.Equals(aKey));
        Assert.Equal(expected.Value.Ordinal, roles[aKey]!.Ordinal);
    }

    [Fact]
    public void AListReportsWhereAnElementIs()
    {
        GenericHollowObject movie = First(Read(TheMatrix), "Movie");

        GenericHollowList cast = movie.GetList("Cast")!;
        IHollowRecord second = cast[1];

        Assert.Equal(1, cast.IndexOf(second));
        Assert.Equal(1, cast.LastIndexOf(second));
        Assert.True(cast.Contains(second));
        Assert.Equal(-1, cast.IndexOf("not a record"));
    }

    /// <summary>
    /// Two handles to the same record of the same type are equal, so a record can be a dictionary key
    /// without anything being materialised.
    /// </summary>
    [Fact]
    public void TwoHandlesToTheSameRecordAreEqual()
    {
        HollowReadStateEngine engine = Read(TheMatrix);

        GenericHollowObject first = First(engine, "Movie");
        GenericHollowObject second = First(engine, "Movie");

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());

        // The same ordinal in a different type is a different record.
        GenericHollowObject otherType = new(engine, "Actor", first.Ordinal);
        Assert.NotEqual<object>(first, otherType);
    }

    /// <summary>
    /// A client reading a dataset written against an older model asks for fields that are not there.
    /// Rather than failing, those reads fall through to the missing-data handler.
    /// </summary>
    [Fact]
    public void AFieldTheDatasetDoesNotHaveReadsAsAbsent()
    {
        GenericHollowObject movie = First(Read(TheMatrix), "Movie");

        Assert.True(movie.IsNull("NoSuchField"));
        Assert.Equal(int.MinValue, movie.GetInt("NoSuchField"));
        Assert.Equal(long.MinValue, movie.GetLong("NoSuchField"));
        Assert.Equal(double.NaN, movie.GetDouble("NoSuchField"));
        Assert.Equal(float.NaN, movie.GetFloat("NoSuchField"));
        Assert.Null(movie.GetString("NoSuchField"));
        Assert.Null(movie.GetDecimal("NoSuchField"));
        Assert.Null(movie.GetBytes("NoSuchField"));
        Assert.False(movie.GetBoolean("NoSuchField"));
        Assert.Null(movie.GetObject("NoSuchField"));
        Assert.True(movie.IsStringFieldEqual("NoSuchField", null));
        Assert.False(movie.IsStringFieldEqual("NoSuchField", "anything"));
    }

    /// <summary>
    /// A caller can replace the fallback, which is how a missing field becomes a loud failure rather
    /// than a silent null.
    /// </summary>
    [Fact]
    public void TheMissingDataFallbackCanBeReplaced()
    {
        HollowReadStateEngine engine = Read(TheMatrix);
        engine.MissingDataHandler = new StrictMissingDataHandler();

        GenericHollowObject movie = First(engine, "Movie");

        Assert.Equal(1999, movie.GetInt("Year"));
        Assert.Throws<InvalidOperationException>(() => movie.GetInt("NoSuchField"));
    }

    [Fact]
    public void EveryRecordOfEveryKindDescribesItself()
    {
        HollowReadStateEngine engine = Read(TheMatrix);
        GenericHollowObject movie = First(engine, "Movie");

        Assert.StartsWith("Hollow Object: Movie", movie.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("Hollow List: ", movie.GetList("Cast")!.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("Hollow Set: ", movie.GetSet("Tags")!.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("Hollow Map: ", movie.GetMap("Roles")!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AskingForTheWrongKindOfRecordIsRefused()
    {
        HollowReadStateEngine engine = Read(TheMatrix);

        Assert.Throws<ArgumentException>(() => new GenericHollowObject(engine, "NoSuchType", 0));
        Assert.Throws<ArgumentException>(() => new GenericHollowList(engine, "Movie", 0));
        Assert.Throws<ArgumentException>(() => new GenericHollowSet(engine, "Movie", 0));
        Assert.Throws<ArgumentException>(() => new GenericHollowMap(engine, "Movie", 0));
    }

    /// <summary>Turns a missing field into a failure rather than a null.</summary>
    private sealed class StrictMissingDataHandler : DefaultMissingDataHandler
    {
        public override int HandleInt(string type, int ordinal, string field) =>
            throw new InvalidOperationException($"{type} has no field named {field}");
    }
}
