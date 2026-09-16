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

using Hollow.Api.Consumer;
using Hollow.Api.TestData;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Schema;

namespace Hollow.Tests.Api.TestData;

/// <summary>
/// Describes a dataset as records and hands it to a consumer.
/// </summary>
/// <remarks>
/// Netflix Hollow has no tests for <c>api.testdata</c>; the package is exercised only through the
/// classes its test-data generator emits, which this port does not have. These are written against
/// the runtime, which this port made usable without a generator.
/// </remarks>
public class HollowTestDatasetTests
{
    [Fact]
    public void ADatasetDescribedAsRecordsBecomesAStateEngine()
    {
        HollowTestDataset dataset = new();

        dataset.Add(Movie(1, "Heat"));
        dataset.Add(Movie(2, "Ronin"));

        HollowObjectTypeReadState movies = Movies(dataset.BuildSnapshot());

        Assert.Equal([1, 2], Ids(movies).Order());
    }

    [Fact]
    public void AReferencedRecordIsAddedWithTheRecordThatReachesIt()
    {
        HollowTestDataset dataset = new();

        dataset.Add(Movie(1, "Heat"));

        HollowReadStateEngine engine = dataset.BuildSnapshot();

        // The String type was never declared; the tree's shape declared it.
        Assert.NotNull(engine.GetTypeState("String"));
        Assert.Equal("Heat", TitleOf(engine, Movies(engine), 1));
    }

    [Fact]
    public void ATreeCanBeWrittenAsOneExpression()
    {
        HollowTestObject<object?> movie = new(null, MovieSchema);

        HollowTestObject<HollowTestObject<object?>> title = new(movie, StringSchema);
        title.With("value", "Collateral");

        movie.With("Id", 3).With("Title", title);

        // Up walks back to the parent, and UpTop to the root, which is what lets a generated builder
        // read as one chained expression.
        Assert.Same(movie, title.Up());
        Assert.Same(movie, title.UpTop<HollowTestObject<object?>>());
    }

    [Fact]
    public void EveryFieldTypeCanBeDescribed()
    {
        HollowObjectSchema schema = new("Every", 8);
        schema.AddField("i", FieldType.Int);
        schema.AddField("l", FieldType.Long);
        schema.AddField("f", FieldType.Float);
        schema.AddField("d", FieldType.Double);
        schema.AddField("b", FieldType.Boolean);
        schema.AddField("s", FieldType.String);
        schema.AddField("y", FieldType.Bytes);
        schema.AddField("m", FieldType.Decimal);

        HollowTestObject<object?> record = new(null, schema);
        record.With("i", 1).With("l", 2L).With("f", 1.5f).With("d", 2.25d)
            .With("b", true).With("s", "text").With("y", new byte[] { 1, 2 }).With("m", 3.75m);

        HollowTestDataset dataset = new();
        dataset.Add(record);

        HollowObjectTypeReadState every =
            (HollowObjectTypeReadState)dataset.BuildSnapshot().GetTypeState("Every")!;

        // Java's chain has no case for a double, so a model with one cannot be described at all.
        Assert.Equal(2.25d, every.ReadDouble(0, every.Schema.GetPosition("d")));

        // And none for a decimal, which is this port's own field type.
        Assert.Equal(3.75m, every.ReadDecimal(0, every.Schema.GetPosition("m")));

        Assert.Equal(1, every.ReadInt(0, every.Schema.GetPosition("i")));
        Assert.Equal("text", every.ReadString(0, every.Schema.GetPosition("s")));
    }

    [Fact]
    public void AFieldSetToNothingIsNull()
    {
        HollowTestObject<object?> movie = new(null, MovieSchema);
        movie.With("Id", 1).With("Title", null);

        HollowTestDataset dataset = new();
        dataset.Add(movie);

        HollowObjectTypeReadState movies = Movies(dataset.BuildSnapshot());

        Assert.Equal(-1, movies.ReadOrdinal(0, movies.Schema.GetPosition("Title")));
    }

    [Fact]
    public void AConsumerFollowsTheDatasetAcrossACycle()
    {
        HollowTestDataset dataset = new();

        dataset.Add(Movie(1, "Heat"));
        dataset.Add(Movie(2, "Ronin"));

        HollowConsumer consumer = new HollowConsumerBuilder()
            .WithBlobRetriever(dataset.BlobRetriever)
            .Build();

        dataset.BuildSnapshot(consumer);

        Assert.Equal([1, 2], Ids(Movies(consumer.StateEngine!)).Order());

        // A second cycle holding only one of the two films, which is a removal as well as a keep.
        dataset.Add(Movie(1, "Heat"));
        dataset.BuildDelta(consumer);

        Assert.Equal([1], Ids(Movies(consumer.StateEngine!)).Order());
    }

    [Fact]
    public void ACycleHoldsOnlyWhatWasAddedForIt()
    {
        HollowTestDataset dataset = new();

        dataset.Add(Movie(1, "Heat"));

        HollowReadStateEngine engine = dataset.BuildSnapshot();
        Assert.Equal([1], Ids(Movies(engine)).Order());

        // Java re-adds every record on every cycle, so the first film would still be here and a
        // delta could never remove anything.
        dataset.Add(Movie(2, "Ronin"));
        dataset.BuildDelta(engine);

        Assert.Equal([2], Ids(Movies(engine)).Order());
    }

    private static HollowObjectSchema MovieSchema
    {
        get
        {
            HollowObjectSchema schema = new("Movie", 2, "Id");
            schema.AddField("Id", FieldType.Int);
            schema.AddField("Title", FieldType.Reference, "String");

            return schema;
        }
    }

    private static HollowObjectSchema StringSchema
    {
        get
        {
            HollowObjectSchema schema = new("String", 1, "value");
            schema.AddField("value", FieldType.String);

            return schema;
        }
    }

    private static HollowTestObject<object?> Movie(int id, string title)
    {
        HollowTestObject<object?> movie = new(null, MovieSchema);

        HollowTestObject<object?> titleRecord = new(movie, StringSchema);
        titleRecord.With("value", title);

        return movie.With("Id", id).With("Title", titleRecord);
    }

    private static HollowObjectTypeReadState Movies(HollowReadStateEngine engine) =>
        (HollowObjectTypeReadState)engine.GetTypeState("Movie")!;

    private static IEnumerable<int> Ids(HollowObjectTypeReadState movies)
    {
        int field = movies.Schema.GetPosition("Id");

        return [.. movies.PopulatedOrdinals.EnumerateSetBits()
            .Select(ordinal => movies.ReadInt(ordinal, field))];
    }

    private static string? TitleOf(
        HollowReadStateEngine engine, HollowObjectTypeReadState movies, int id)
    {
        int idField = movies.Schema.GetPosition("Id");
        int titleField = movies.Schema.GetPosition("Title");

        HollowObjectTypeReadState strings = (HollowObjectTypeReadState)engine.GetTypeState("String")!;

        foreach (int ordinal in movies.PopulatedOrdinals.EnumerateSetBits())
        {
            if (movies.ReadInt(ordinal, idField) == id)
            {
                int title = movies.ReadOrdinal(ordinal, titleField);

                return title == -1 ? null : strings.ReadString(title, 0);
            }
        }

        return null;
    }
}
