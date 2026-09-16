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

using Hollow.Api.Consumer.Data;
using Hollow.Api.Objects.Generic;
using Hollow.Core.Index.Key;
using Hollow.Core.Read.Engine;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Tests.Api.Consumer.Data;

/// <summary>
/// What a transition changed, read without a generated API.
/// </summary>
/// <remarks>
/// Ported from <c>GenericHollowRecordDataAccessorTest</c>. The change computation itself is covered by
/// <see cref="DataAccessorTests"/>; what is worth asserting here is that the records come back readable
/// by field name, which is the only thing this subclass adds.
/// </remarks>
public class GenericHollowRecordDataAccessorTests
{
    [Fact]
    public void EveryRecordIsReadableByFieldName()
    {
        Producer producer = new();
        HollowReadStateEngine engine = producer.PublishSnapshot(Film(1, "Heat"), Film(2, "Ronin"));

        GenericHollowRecordDataAccessor accessor = new(engine, "Movie");

        Assert.Equal(
            ["Heat", "Ronin"],
            accessor.AllRecords.Select(Title).Order());
    }

    [Fact]
    public void AdditionsAndRemovalsAreReported()
    {
        Producer producer = new();
        HollowReadStateEngine engine = producer.PublishSnapshot(Film(1, "Heat"), Film(2, "Ronin"));

        producer.PublishDelta(engine, Film(1, "Heat"), Film(3, "Collateral"));

        GenericHollowRecordDataAccessor accessor = new(engine, "Movie");

        Assert.Equal(["Collateral"], accessor.AddedRecords.Select(Title));
        Assert.Equal(["Ronin"], accessor.RemovedRecords.Select(Title));
    }

    [Fact]
    public void AReplacementIsReportedAsOneUpdate()
    {
        Producer producer = new();
        HollowReadStateEngine engine = producer.PublishSnapshot(Film(1, "Heat"), Film(2, "Ronin"));

        producer.PublishDelta(engine, Film(1, "Heat"), Film(2, "Ronin (1998)"));

        GenericHollowRecordDataAccessor accessor = new(engine, "Movie");

        // A key is what turns a removal and an addition back into one record that changed.
        UpdatedRecord<GenericHollowObject> updated = Assert.Single(accessor.UpdatedRecords);

        Assert.Equal("Ronin", Title(updated.Before));
        Assert.Equal("Ronin (1998)", Title(updated.After));
        Assert.Empty(accessor.AddedRecords);
        Assert.Empty(accessor.RemovedRecords);
    }

    [Fact]
    public void AKeyGivenAtTheCallSiteOverridesTheDeclaredOne()
    {
        Producer producer = new();
        HollowReadStateEngine engine = producer.PublishSnapshot(Film(1, "Heat"), Film(2, "Ronin"));

        producer.PublishDelta(engine, Film(1, "Heat"), Film(3, "Ronin"));

        // Keyed on the title instead, the film that changed identifier is the same film renumbered.
        GenericHollowRecordDataAccessor accessor = new(engine, "Movie", "Title.value");

        UpdatedRecord<GenericHollowObject> updated = Assert.Single(accessor.UpdatedRecords);

        Assert.Equal(2, updated.Before.GetInt("Id"));
        Assert.Equal(3, updated.After.GetInt("Id"));
    }

    [Fact]
    public void AKeyCanBeGivenAsAPrimaryKey()
    {
        Producer producer = new();
        HollowReadStateEngine engine = producer.PublishSnapshot(Film(1, "Heat"));

        GenericHollowRecordDataAccessor accessor =
            new(engine, "Movie", new PrimaryKey("Movie", "Title.value"));

        Assert.Equal(["Title.value"], accessor.PrimaryKey.FieldPaths);
    }

    [Fact]
    public void ATypeThatIsNotAnObjectTypeIsRefused()
    {
        Producer producer = new();
        HollowReadStateEngine engine = producer.PublishSnapshot(new Billing { Cast = ["Pacino"] });

        // The change computation is defined over object records, because matching needs a key.
        GenericHollowRecordDataAccessor accessor = new(engine, "ListOfString");

        Assert.Throws<InvalidOperationException>(() => accessor.GetRecord(0));
    }

    [Fact]
    public void ATypeTheDatasetDoesNotHaveIsRefused()
    {
        Producer producer = new();
        HollowReadStateEngine engine = producer.PublishSnapshot(Film(1, "Heat"));

        GenericHollowRecordDataAccessor accessor = new(engine, "Actor");

        Assert.Throws<InvalidOperationException>(() => accessor.GetRecord(0));
    }

    private static string? Title(GenericHollowObject movie) => movie.GetObject("Title")?.GetString("value");

    private static Movie Film(int id, string title) => new() { Id = id, Title = title };

    /// <summary>A producer and its mapper, kept across cycles the way a delta chain needs.</summary>
    private sealed class Producer
    {
        private readonly HollowWriteStateEngine _writeEngine = new();
        private readonly HollowObjectMapper _mapper;

        internal Producer() => _mapper = new HollowObjectMapper(_writeEngine);

        internal HollowReadStateEngine PublishSnapshot(params object[] records)
        {
            Add(records);

            return StateEngineRoundTripper.RoundTripSnapshot(_writeEngine);
        }

        internal void PublishDelta(HollowReadStateEngine readEngine, params object[] records)
        {
            Add(records);

            StateEngineRoundTripper.RoundTripDelta(_writeEngine, readEngine);
        }

        private void Add(object[] records)
        {
            foreach (object record in records)
            {
                _mapper.Add(record);
            }
        }
    }

    [HollowPrimaryKey("Id")]
    public sealed class Movie
    {
        public required int Id { get; init; }

        public required string Title { get; init; }
    }

    public sealed class Billing
    {
        public required List<string> Cast { get; init; }
    }
}
