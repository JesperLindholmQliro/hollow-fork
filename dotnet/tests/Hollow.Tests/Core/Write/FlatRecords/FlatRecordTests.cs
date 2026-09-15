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

using Hollow.Core.Schema;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;
using Hollow.Core.Write.ObjectMapper.FlatRecords;
using Hollow.Core.Write.ObjectMapper.FlatRecords.Traversal;

namespace Hollow.Tests.Core.Write.FlatRecords;

/// <summary>
/// Exercises the flat record format: one record and everything it references, serialised on its own.
/// </summary>
/// <remarks>
/// Ported from <c>FlatRecordWriterTests</c> and <c>FlatRecordReaderTests</c>, which build their
/// records field by field as these do. Java's tests supply a <c>FakeHollowSchemaIdentifierMapper</c>;
/// this port has a real one, so these use it.
/// </remarks>
public class FlatRecordTests
{
    [Fact]
    public void ARecordAndWhatItReferencesGoInAndComeBackOut()
    {
        Catalogue catalogue = new();
        FlatRecord record = catalogue.Flatten(1, "Heat");

        FlatRecordTraversalObjectNode movie = FlatRecordTraversal.From(record);

        Assert.Equal("Movie", movie.Schema.Name);
        Assert.Equal(1, movie.GetInt("Id"));

        // The title is a reference to a String record inside this same flat record.
        FlatRecordTraversalObjectNode title = movie.GetFieldNode<FlatRecordTraversalObjectNode>("Title")!;
        Assert.Equal("Heat", title.GetString("value"));
    }

    [Fact]
    public void TheKeyIsCarriedSoAReceiverNeedNoModelClass()
    {
        Catalogue catalogue = new();
        FlatRecord record = catalogue.Flatten(7, "Ronin");

        // Read out of the record's tail, where the writer noted where each key field's value sits.
        Assert.NotNull(record.RecordPrimaryKey);
        Assert.Equal("Movie", record.RecordPrimaryKey.Type);
        Assert.Equal([7], record.RecordPrimaryKey.Key);
    }

    [Fact]
    public void AKeyThatStepsThroughAReferenceIsFound()
    {
        Catalogue catalogue = new(keyOnTitle: true);
        FlatRecord record = catalogue.Flatten(1, "Heat");

        // The key is Title.value, so finding it means following a reference to another record of the
        // flat record and reading a field of that.
        Assert.Equal(["Heat"], record.RecordPrimaryKey!.Key);
    }

    [Fact]
    public void TwoIdenticalRecordsAreWrittenOnce()
    {
        Catalogue catalogue = new();

        // Both titles are the same string, so the two Movie records should share one String record.
        FlatRecord record = catalogue.FlattenTwo(1, "Heat", 2, "Heat");
        FlatRecordOrdinalReader reader = new(record);

        // One String, two Movies, and the list that holds them.
        Assert.Equal(4, reader.OrdinalCount);
        Assert.Equal(
            1, Enumerable.Range(0, reader.OrdinalCount).Count(i => reader.ReadSchema(i).Name == "String"));
    }

    [Fact]
    public void ACollectionReadsBackAsItsElements()
    {
        Catalogue catalogue = new();
        FlatRecord record = catalogue.FlattenTwo(1, "Heat", 2, "Ronin");

        FlatRecordTraversalListNode films = (FlatRecordTraversalListNode)FlatRecordTraversal.Node(
            new FlatRecordOrdinalReader(record), new FlatRecordOrdinalReader(record).OrdinalCount - 1);

        Assert.Equal(2, films.Count);
        Assert.Equal(
            ["Heat", "Ronin"],
            films.Select(film =>
                ((FlatRecordTraversalObjectNode)film!)
                    .GetFieldNode<FlatRecordTraversalObjectNode>("Title")!
                    .GetString("value")));
    }

    [Fact]
    public void EveryFieldTypeSurvivesTheRoundTrip()
    {
        HollowWriteStateEngine engine = new();
        HollowObjectSchema schema = new("Everything", 8);
        schema.AddField("i", FieldType.Int);
        schema.AddField("l", FieldType.Long);
        schema.AddField("f", FieldType.Float);
        schema.AddField("d", FieldType.Double);
        schema.AddField("b", FieldType.Boolean);
        schema.AddField("s", FieldType.String);
        schema.AddField("y", FieldType.Bytes);
        schema.AddField("m", FieldType.Decimal);

        engine.AddTypeState(new HollowObjectTypeWriteState(schema));

        HollowObjectWriteRecord record = new(schema);
        record.SetInt("i", -42);
        record.SetLong("l", long.MaxValue / 3);
        record.SetFloat("f", 1.5f);
        record.SetDouble("d", -2.25d);
        record.SetBoolean("b", true);
        record.SetString("s", "a string with a ሾ in it");
        record.SetBytes("y", [1, 2, 3, 250]);
        record.SetDecimal("m", 12.3456m);

        FlatRecordWriter writer = new(engine, new HollowDatasetSchemaIdentifierMapper(engine));
        writer.Write(schema, record);

        FlatRecordTraversalObjectNode node = FlatRecordTraversal.From(writer.GenerateFlatRecord());

        Assert.Equal(-42, node.GetInt("i"));
        Assert.Equal(long.MaxValue / 3, node.GetLong("l"));
        Assert.Equal(1.5f, node.GetFloat("f"));
        Assert.Equal(-2.25d, node.GetDouble("d"));
        Assert.True(node.GetBoolean("b"));
        Assert.Equal("a string with a ሾ in it", node.GetString("s"));
        Assert.Equal<byte[]>([1, 2, 3, 250], node.GetBytes("y")!);

        // Decimal is this port's own field type, which Java's flat records have no case for.
        Assert.Equal(12.3456m, node.GetDecimal("m"));
    }

    [Fact]
    public void ANullFieldReadsBackAsNull()
    {
        HollowWriteStateEngine engine = new();
        HollowObjectSchema schema = new("Sparse", 3);
        schema.AddField("i", FieldType.Int);
        schema.AddField("s", FieldType.String);
        schema.AddField("m", FieldType.Decimal);

        engine.AddTypeState(new HollowObjectTypeWriteState(schema));

        // Nothing set at all, so every field is null.
        FlatRecordWriter writer = new(engine, new HollowDatasetSchemaIdentifierMapper(engine));
        writer.Write(schema, new HollowObjectWriteRecord(schema));

        FlatRecordTraversalObjectNode node = FlatRecordTraversal.From(writer.GenerateFlatRecord());

        Assert.True(node.IsFieldNull("i"));
        Assert.True(node.IsFieldNull("s"));
        Assert.True(node.IsFieldNull("m"));

        Assert.Null(node.GetInt("i"));
        Assert.Null(node.GetString("s"));
        Assert.Null(node.GetDecimal("m"));

        // A field the record does not have counts as null too.
        Assert.True(node.IsFieldNull("nothing"));
    }

    [Fact]
    public void TheSequentialReaderWalksTheSameBytes()
    {
        Catalogue catalogue = new();
        FlatRecord record = catalogue.Flatten(3, "Collateral");

        FlatRecordReader reader = new(record);

        // The String record comes first: a record can only reference one already written.
        Assert.Equal("String", reader.ReadSchema().Name);
        Assert.Equal("Collateral", reader.ReadString());

        Assert.True(reader.HasMore);
        Assert.Equal("Movie", reader.ReadSchema().Name);
        Assert.Equal(3, reader.ReadInt());
        Assert.Equal(0, reader.ReadOrdinal());

        Assert.False(reader.HasMore);
    }

    [Fact]
    public void WritingNothingIsRefused()
    {
        HollowWriteStateEngine engine = new();
        FlatRecordWriter writer = new(engine, new HollowDatasetSchemaIdentifierMapper(engine));

        Assert.Throws<InvalidOperationException>(writer.GenerateFlatRecord);
    }

    [Fact]
    public void AResetWriterStartsAgain()
    {
        Catalogue catalogue = new();

        Assert.Equal(1, FlatRecordTraversal.From(catalogue.Flatten(1, "Heat")).GetInt("Id"));

        // The same writer, having been reset: the second record has to be a record of its own rather
        // than the first one with more on the end.
        Assert.Equal(2, FlatRecordTraversal.From(catalogue.Flatten(2, "Ronin")).GetInt("Id"));
    }

    /// <summary>
    /// A film catalogue, and the writer that flattens records of it.
    /// </summary>
    /// <remarks>
    /// The records are built field by field rather than through the object mapper, which has no
    /// flattening of its own yet.
    /// </remarks>
    private sealed class Catalogue
    {
        private readonly HollowWriteStateEngine _engine = new();
        private readonly FlatRecordWriter _writer;
        private readonly HollowObjectSchema _movie;
        private readonly HollowObjectSchema _string;
        private readonly HollowListSchema _films;

        internal Catalogue(bool keyOnTitle = false)
        {
            _string = new HollowObjectSchema("String", 1);
            _string.AddField("value", FieldType.String);

            _movie = new HollowObjectSchema("Movie", 2, keyOnTitle ? "Title.value" : "Id");
            _movie.AddField("Id", FieldType.Int);
            _movie.AddField("Title", FieldType.Reference, "String");

            _films = new HollowListSchema("ListOfMovie", "Movie");

            _engine.AddTypeState(new HollowObjectTypeWriteState(_string));
            _engine.AddTypeState(new HollowObjectTypeWriteState(_movie));
            _engine.AddTypeState(new HollowListTypeWriteState(_films));

            _writer = new FlatRecordWriter(_engine, new HollowDatasetSchemaIdentifierMapper(_engine));
        }

        internal FlatRecord Flatten(int id, string title)
        {
            _writer.Reset();
            WriteMovie(id, title);

            return _writer.GenerateFlatRecord();
        }

        internal FlatRecord FlattenTwo(int firstId, string firstTitle, int secondId, string secondTitle)
        {
            _writer.Reset();

            HollowListWriteRecord films = new();
            films.AddElement(WriteMovie(firstId, firstTitle));
            films.AddElement(WriteMovie(secondId, secondTitle));

            _writer.Write(_films, films);

            return _writer.GenerateFlatRecord();
        }

        private int WriteMovie(int id, string title)
        {
            HollowObjectWriteRecord titleRecord = new(_string);
            titleRecord.SetString("value", title);

            HollowObjectWriteRecord movieRecord = new(_movie);
            movieRecord.SetInt("Id", id);
            movieRecord.SetReference("Title", _writer.Write(_string, titleRecord));

            return _writer.Write(_movie, movieRecord);
        }
    }
}
