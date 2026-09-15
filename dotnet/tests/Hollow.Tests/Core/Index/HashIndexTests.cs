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

using Hollow.Core.Index;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Read.Iterator;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Tests.Core.Index;

/// <summary>
/// Indexes records by fields that are not a unique key.
/// </summary>
/// <remarks>
/// What separates this from <see cref="PrimaryKeyIndexTests"/> is that the paths may cross collections:
/// one record is reachable by as many keys as its collection has elements, and one key may reach many
/// records. The query also selects at a path of its own, so it can return the elements rather than the
/// records they belong to.
/// </remarks>
public class HashIndexTests
{
    private sealed class TypeB(string? b1)
    {
        public string? B1 { get; set; } = b1;
    }

    private sealed class TypeA(int a1, double a2, params TypeB[] ab)
    {
        public int A1 { get; set; } = a1;

        public double A2 { get; set; } = a2;

        public List<TypeB> Ab { get; set; } = [.. ab];
    }

    private sealed class TypeBytes(byte[]? data)
    {
        [HollowInline]
        public byte[]? Data { get; set; } = data;
    }

    private sealed class TypeTwoStrings(string? b1, string? b2)
    {
        public string? B1 { get; set; } = b1;

        public string? B2 { get; set; } = b2;
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

    private static HollowReadStateEngine Map(params object[] values)
    {
        HollowWriteStateEngine engine = new();
        HollowObjectMapper mapper = new(engine);

        foreach (object value in values)
        {
            mapper.Add(value);
        }

        return RoundTrip(engine);
    }

    private static void AssertMatches(HollowHashIndexResult? result, params int[] expected)
    {
        Assert.NotNull(result);
        Assert.Equal(expected.Order(), result.AsEnumerable().Order());
        Assert.Equal(expected.Length, result.Count);

        foreach (int ordinal in expected)
        {
            Assert.True(result.Contains(ordinal), $"result should contain ordinal {ordinal}");
        }
    }

    /// <summary>
    /// The canonical case: a record with several children is found by any of them, and a key shared by
    /// two records finds both.
    /// </summary>
    [Fact]
    public void RecordsAreFoundByAnyOfTheirChildren()
    {
        HollowReadStateEngine consumer = Map(
            new TypeA(1, 1.1d, new TypeB("one")),
            new TypeA(1, 1.1d, new TypeB("1")),
            new TypeA(2, 2.2d, new TypeB("two"), new TypeB("twenty"), new TypeB("two hundred")),
            new TypeA(3, 3.3d, new TypeB("three"), new TypeB("thirty"), new TypeB("three hundred")),
            new TypeA(4, 4.4d, new TypeB("four")),
            new TypeA(4, 4.5d, new TypeB("four"), new TypeB("forty")));

        HollowHashIndex index = new(consumer, "TypeA", "", "A1", "Ab.element.B1.value");

        Assert.Null(index.FindMatches(0, "notfound"));

        AssertMatches(index.FindMatches(1, "one"), 0);
        AssertMatches(index.FindMatches(1, "1"), 1);
        AssertMatches(index.FindMatches(2, "two"), 2);
        AssertMatches(index.FindMatches(2, "twenty"), 2);
        AssertMatches(index.FindMatches(2, "two hundred"), 2);
        AssertMatches(index.FindMatches(3, "three"), 3);
        AssertMatches(index.FindMatches(3, "thirty"), 3);
        AssertMatches(index.FindMatches(3, "three hundred"), 3);

        // Two records share (4, "four"); only one also has "forty".
        AssertMatches(index.FindMatches(4, "four"), 4, 5);
        AssertMatches(index.FindMatches(4, "forty"), 5);
    }

    /// <summary>
    /// Selecting at a path returns the records that path reaches, not the ones the match started from.
    /// </summary>
    [Fact]
    public void TheSelectPathDecidesWhatIsReturned()
    {
        HollowReadStateEngine consumer = Map(
            new TypeA(1, 1.1d, new TypeB("one"), new TypeB("uno")),
            new TypeA(2, 2.2d, new TypeB("two")));

        HollowHashIndex index = new(consumer, "TypeA", "Ab.element", "A1");

        HollowObjectTypeReadState typeBs =
            Assert.IsType<HollowObjectTypeReadState>(consumer.GetTypeState("TypeB"));
        HollowObjectTypeReadState strings =
            Assert.IsType<HollowObjectTypeReadState>(consumer.GetTypeState("String"));

        int b1 = typeBs.Schema.GetPosition("B1");
        int value = strings.Schema.GetPosition("value");

        HollowHashIndexResult? result = index.FindMatches(1);
        Assert.NotNull(result);

        Assert.Equal(
            ["one", "uno"],
            result.AsEnumerable()
                .Select(o => strings.ReadString(typeBs.ReadOrdinal(o, b1), value))
                .Order());

        HollowHashIndexResult? second = index.FindMatches(2);
        Assert.NotNull(second);
        Assert.Equal(1, second.Count);
    }

    /// <summary>
    /// A null value along a match path is not a key anything can be found by, but it must not stop the
    /// rest of the records being indexed.
    /// </summary>
    [Fact]
    public void NullValuesAreNotIndexedButDoNotBreakTheIndex()
    {
        HollowReadStateEngine consumer = Map(new TypeB(null), new TypeB("onez:"));

        HollowHashIndex index = new(consumer, "TypeB", "", "B1.value");

        Assert.Null(index.FindMatches("one:"));
        AssertMatches(index.FindMatches("onez:"), 1);
    }

    [Fact]
    public void TwoMatchFieldsWithNullsBehave()
    {
        HollowReadStateEngine consumer = Map(
            new TypeTwoStrings(null, "onez:"),
            new TypeTwoStrings("onez:", "onez:"),
            new TypeTwoStrings(null, null));

        HollowHashIndex index = new(consumer, "TypeTwoStrings", "", "B1.value", "B2.value");

        AssertMatches(index.FindMatches("onez:", "onez:"), 1);
        Assert.Null(index.FindMatches("onez:", "other"));
    }

    [Fact]
    public void BytesFieldsCanBeMatchedOn()
    {
        byte[] data = [0x88, 0, 0, 0];

        HollowReadStateEngine consumer = Map(new TypeBytes(null), new TypeBytes(data));
        HollowHashIndex index = new(consumer, "TypeBytes", "", "Data");

        Assert.Null(index.FindMatches((object)new byte[] { 1 }));
        AssertMatches(index.FindMatches(data), 1);
    }

    [Fact]
    public void NumericFieldsCanBeMatchedOn()
    {
        HollowReadStateEngine consumer = Map(
            new TypeA(1, 1.5d, new TypeB("a")),
            new TypeA(2, 2.5d, new TypeB("b")));

        HollowHashIndex byDouble = new(consumer, "TypeA", "", "A2");

        AssertMatches(byDouble.FindMatches(1.5d), 0);
        AssertMatches(byDouble.FindMatches(2.5d), 1);
        Assert.Null(byDouble.FindMatches(3.5d));
    }

    /// <summary>
    /// An index that matches on nothing would give every record the same zero-width key, and the tables
    /// use a bit of the key to tell an occupied bucket from an empty one. Java builds such an index and
    /// then finds only some of the records; this port refuses it.
    /// </summary>
    [Fact]
    public void AnIndexWithNoMatchFieldsIsRejected()
    {
        HollowReadStateEngine consumer = Map(new TypeB("one"), new TypeB("two"));

        ArgumentException e = Assert.Throws<ArgumentException>(
            () => new HollowHashIndex(consumer, "TypeB", ""));

        Assert.Contains("at least one field", e.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Where two match paths each cross a collection, a record is reachable by every pairing of their
    /// values — the combinations the traversal multiplies out.
    /// </summary>
    [Fact]
    public void TwoPathsThroughCollectionsProduceEveryCombination()
    {
        HollowReadStateEngine consumer = Map(
            new Pairing { Left = ["a", "b"], Right = ["x", "y"] },
            new Pairing { Left = ["a"], Right = ["z"] });

        HollowHashIndex index = new(
            consumer, "Pairing", "", "Left.element.value", "Right.element.value");

        AssertMatches(index.FindMatches("a", "x"), 0);
        AssertMatches(index.FindMatches("a", "y"), 0);
        AssertMatches(index.FindMatches("b", "x"), 0);
        AssertMatches(index.FindMatches("b", "y"), 0);
        AssertMatches(index.FindMatches("a", "z"), 1);

        // A pairing that exists in neither record.
        Assert.Null(index.FindMatches("b", "z"));
    }

    private sealed class Pairing
    {
        public List<string> Left { get; set; } = [];

        public List<string> Right { get; set; } = [];
    }

    /// <summary>
    /// A path may traverse a map's keys or values.
    /// </summary>
    [Fact]
    public void MapsCanBeTraversed()
    {
        HollowReadStateEngine consumer = Map(
            new Catalogue { Ratings = new Dictionary<string, int> { ["Persona"] = 9, ["Sweden"] = 7 } },
            new Catalogue { Ratings = new Dictionary<string, int> { ["Persona"] = 8 } });

        HollowHashIndex byKey = new(consumer, "Catalogue", "", "Ratings.key.value");
        AssertMatches(byKey.FindMatches("Persona"), 0, 1);
        AssertMatches(byKey.FindMatches("Sweden"), 0);

        HollowHashIndex byValue = new(consumer, "Catalogue", "", "Ratings.value.value");
        AssertMatches(byValue.FindMatches(9), 0);
        AssertMatches(byValue.FindMatches(8), 1);

        // A key and a value of the same entry pair up; a key of one entry and a value of another do not.
        HollowHashIndex byEntry = new(
            consumer, "Catalogue", "", "Ratings.key.value", "Ratings.value.value");
        AssertMatches(byEntry.FindMatches("Persona", 9), 0);
        AssertMatches(byEntry.FindMatches("Sweden", 7), 0);
        Assert.Null(byEntry.FindMatches("Persona", 7));
    }

    private sealed class Catalogue
    {
        public Dictionary<string, int> Ratings { get; set; } = [];
    }

    /// <summary>
    /// Enough records that the intermediate table outgrows its initial guess and has to be rehashed.
    /// </summary>
    [Fact]
    public void TheIndexGrowsPastItsInitialSizeGuess()
    {
        // Every record has ten children, so there are ten times as many distinct keys as records --
        // far more than the one-per-record the builder starts by assuming.
        TypeA[] records =
        [
            .. Enumerable.Range(0, 200).Select(
                i => new TypeA(
                    i,
                    i,
                    [.. Enumerable.Range(0, 10).Select(j => new TypeB($"child-{i}-{j}"))])),
        ];

        HollowReadStateEngine consumer = Map([.. records.Cast<object>()]);
        HollowHashIndex index = new(consumer, "TypeA", "", "Ab.element.B1.value");

        for (int i = 0; i < 200; i++)
        {
            for (int j = 0; j < 10; j++)
            {
                HollowHashIndexResult? result = index.FindMatches($"child-{i}-{j}");
                Assert.NotNull(result);
                Assert.Equal(1, result.Count);
            }
        }

        Assert.Null(index.FindMatches("child-200-0"));
        Assert.True(index.ApproxHeapFootprintInBytes > 0);
    }

    /// <summary>
    /// One key reaching many records is the other half of what this index is for.
    /// </summary>
    [Fact]
    public void OneKeyCanReachManyRecords()
    {
        object[] records =
            [.. Enumerable.Range(0, 50).Select(i => new TypeA(i % 5, i, new TypeB($"child-{i}")))];

        HollowReadStateEngine consumer = Map(records);
        HollowHashIndex index = new(consumer, "TypeA", "", "A1");

        for (int key = 0; key < 5; key++)
        {
            HollowHashIndexResult? result = index.FindMatches(key);
            Assert.NotNull(result);
            Assert.Equal(10, result.Count);
        }
    }

    [Fact]
    public void TheIndexTracksADelta()
    {
        HollowWriteStateEngine engine = new() { RandomizedTag = 1 };
        HollowObjectMapper mapper = new(engine);

        mapper.Add(new TypeA(1, 1.1d, new TypeB("one")));
        mapper.Add(new TypeA(2, 2.2d, new TypeB("two")));

        HollowReadStateEngine consumer = RoundTrip(engine);

        HollowHashIndex index = new(consumer, "TypeA", "", "Ab.element.B1.value");
        index.ListenForDeltaUpdates();

        AssertMatches(index.FindMatches("one"), 0);

        engine.PrepareForNextCycle();
        engine.RandomizedTag = 2;
        mapper.Add(new TypeA(1, 1.1d, new TypeB("one")));
        mapper.Add(new TypeA(3, 3.3d, new TypeB("three")));

        using (MemoryStream delta = new())
        {
            new HollowBlobWriter(engine).WriteDelta(delta);
            delta.Position = 0;
            new HollowBlobReader(consumer).ApplyDelta(delta);
        }

        AssertMatches(index.FindMatches("one"), 0);
        Assert.Null(index.FindMatches("two"));

        HollowHashIndexResult? three = index.FindMatches("three");
        Assert.NotNull(three);
        Assert.Equal(1, three.Count);

        index.DetachFromDeltaUpdates();
    }

    [Fact]
    public void AnIndexOverAnAbsentTypeIsNotInitialized()
    {
        HollowReadStateEngine consumer = Map(new TypeB("one"));

        HollowHashIndex index = new(consumer, "NoSuchType", "", "B1.value");

        Assert.False(index.IsInitialized);
        Assert.Equal(0, index.ApproxHeapFootprintInBytes);
        Assert.Throws<InvalidOperationException>(() => index.FindMatches("one"));
    }

    [Fact]
    public void QueryingWithTheWrongNumberOfValuesIsRejected()
    {
        HollowReadStateEngine consumer = Map(new TypeB("one"));
        HollowHashIndex index = new(consumer, "TypeB", "", "B1.value");

        Assert.Throws<ArgumentException>(() => index.FindMatches("one", "two"));
    }

    /// <summary>
    /// A null cannot be a key, because a null field is not indexed; saying so beats returning an empty
    /// result that looks like "no such record".
    /// </summary>
    [Fact]
    public void QueryingByNullIsRejected()
    {
        HollowReadStateEngine consumer = Map(new TypeB("one"));
        HollowHashIndex index = new(consumer, "TypeB", "", "B1.value");

        Assert.Throws<ArgumentException>(() => index.FindMatches([null]));
    }

    [Fact]
    public void AnIndexDescribesItself()
    {
        HollowReadStateEngine consumer = Map(new TypeB("one"));
        HollowHashIndex index = new(consumer, "TypeB", "", "B1.value");

        Assert.Equal("TypeB", index.Type);
        Assert.Equal(string.Empty, index.SelectField);
        Assert.Equal(["B1.value"], index.MatchFields);
        Assert.Contains("TypeB", index.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Repeated elements select the same record twice, which the index has to collapse rather than
    /// store twice or count twice.
    /// </summary>
    [Fact]
    public void RepeatedSelectionsAreDeduplicated()
    {
        HollowReadStateEngine consumer = Map(
            new TypeA(1, 1.1d, new TypeB("dup"), new TypeB("dup"), new TypeB("other")));

        // "dup" reaches the one TypeA record through two different elements.
        HollowHashIndex index = new(consumer, "TypeA", "", "Ab.element.B1.value");

        HollowHashIndexResult? result = index.FindMatches("dup");
        Assert.NotNull(result);
        Assert.Equal(1, result.Count);
        Assert.Equal([0], result.AsEnumerable());
    }

    /// <summary>
    /// Iterating a result and probing it have to agree, since they read the same table two ways.
    /// </summary>
    [Fact]
    public void IterationAndContainmentAgree()
    {
        object[] records =
            [.. Enumerable.Range(0, 40).Select(i => new TypeA(i % 3, i, new TypeB($"child-{i}")))];

        HollowReadStateEngine consumer = Map(records);
        HollowHashIndex index = new(consumer, "TypeA", "", "A1");

        HollowHashIndexResult? result = index.FindMatches(0);
        Assert.NotNull(result);

        int[] iterated = [.. result.AsEnumerable()];
        Assert.Equal(result.Count, iterated.Length);
        Assert.Equal(iterated.Length, iterated.Distinct().Count());

        foreach (int ordinal in iterated)
        {
            Assert.True(result.Contains(ordinal));
        }

        // An ordinal no record has, to prove Contains is not answering yes to everything.
        Assert.False(result.Contains(int.MaxValue - 1));
    }
}
