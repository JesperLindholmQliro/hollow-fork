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

using Hollow.Api.Codegen;
using Hollow.Api.Consumer.Data;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Engine;
using Hollow.Core.Schema;
using Hollow.Core.Types.Accessor;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Tests.Api.Consumer.Data;

/// <summary>
/// Exercises the data accessors: what a transition did to a type's records, as records.
/// </summary>
/// <remarks>
/// Java's tests for these live in <c>api.consumer.data</c> — <c>HollowDataAccessorTest</c> and one per
/// scalar type over a shared <c>AbstractPrimitiveTypeDataAccessorTest</c>. The cases are the same ones;
/// what they run against is this port's <c>RecordChangeSet</c>, which does the matching underneath.
/// </remarks>
public class DataAccessorTests
{
    [Fact]
    public void ASnapshotHasNothingToCompareAgainst()
    {
        Producer producer = new();
        HollowReadStateEngine readEngine = producer.PublishSnapshot(Film(1, "Heat"), Film(2, "Ronin"));

        MovieAccessor accessor = new(readEngine);

        // Everything looks added after a snapshot, which is why a caller asks this before believing
        // the rest of it.
        Assert.False(accessor.HasPriorState);
        Assert.Equal(2, accessor.AllRecords.Count);
    }

    [Fact]
    public void ADeltaSaysWhatArrivedAndWhatWentAway()
    {
        Producer producer = new();
        HollowReadStateEngine readEngine = producer.PublishSnapshot(Film(1, "Heat"), Film(2, "Ronin"));

        producer.PublishDelta(readEngine, Film(1, "Heat"), Film(3, "Collateral"));

        MovieAccessor accessor = new(readEngine);

        Assert.True(accessor.HasPriorState);
        Assert.Equal(2, accessor.AllRecords.Count);

        Assert.Equal([3], accessor.AddedRecords);
        Assert.Equal([2], accessor.RemovedRecords);
        Assert.Empty(accessor.UpdatedRecords);
    }

    [Fact]
    public void AReplacedRecordIsOneUpdateRatherThanAnAdditionAndARemoval()
    {
        Producer producer = new();
        HollowReadStateEngine readEngine = producer.PublishSnapshot(Film(1, "Heat"), Film(2, "Ronin"));

        producer.PublishDelta(readEngine, Film(1, "Heat"), Film(2, "Ronin (1998)"));

        MovieAccessor accessor = new(readEngine);

        UpdatedRecord<int> updated = Assert.Single(accessor.UpdatedRecords);

        // The key is what makes it one change rather than two: the same film under a new title.
        Assert.Equal(2, updated.Before);
        Assert.Equal(2, updated.After);

        Assert.Empty(accessor.AddedRecords);
        Assert.Empty(accessor.RemovedRecords);
    }

    [Fact]
    public void ARemovedRecordIsStillReadableThroughTheAccessor()
    {
        Producer producer = new();
        HollowReadStateEngine readEngine = producer.PublishSnapshot(Film(1, "Heat"), Film(2, "Ronin"));

        producer.PublishDelta(readEngine, Film(1, "Heat"));

        MovieAccessor accessor = new(readEngine);

        // Its ordinal is no longer populated, but the storage behind it has not been reused. That is
        // the point of the accessor: a record that went away can be looked at before the next
        // transition takes the space back.
        Assert.Equal([2], accessor.RemovedRecords);
    }

    [Fact]
    public void AChangeIsWorkedOutOnceAndOnDemand()
    {
        Producer producer = new();
        HollowReadStateEngine readEngine = producer.PublishSnapshot(Film(1, "Heat"));

        MovieAccessor accessor = new(readEngine);

        Assert.False(accessor.IsDataChangeComputed);

        // AllRecords is answerable from the populated ordinals alone, and does not force it.
        Assert.Single(accessor.AllRecords);
        Assert.False(accessor.IsDataChangeComputed);

        accessor.ComputeDataChange();
        Assert.True(accessor.IsDataChangeComputed);

        // Twice is once.
        accessor.ComputeDataChange();
        Assert.True(accessor.IsDataChangeComputed);
    }

    [Fact]
    public void AnotherKeyCanBeGivenInsteadOfTheDeclaredOne()
    {
        Producer producer = new();
        HollowReadStateEngine readEngine = producer.PublishSnapshot(Film(1, "Heat"), Film(2, "Ronin"));

        producer.PublishDelta(readEngine, Film(1, "Heat"), Film(3, "Ronin"));

        // Matched on the title rather than on the id, so the renumbered record reads as the same film
        // under a new number rather than as one arrival and one departure.
        MovieAccessor byTitle = new(readEngine, "Title.value");

        Assert.Equal("Title.value", Assert.Single(byTitle.PrimaryKey.FieldPaths));
        Assert.Single(byTitle.UpdatedRecords);
        Assert.Empty(byTitle.AddedRecords);
        Assert.Empty(byTitle.RemovedRecords);
    }

    // ---- The scalar types ----

    [Fact]
    public void TheStringAccessorSaysWhichStringsArrivedAndWhichWentAway()
    {
        Producer producer = new();
        HollowReadStateEngine readEngine = producer.PublishSnapshot(Film(1, "Heat"), Film(2, "Ronin"));

        producer.PublishDelta(readEngine, Film(1, "Heat"), Film(2, "Collateral"));

        StringDataAccessor strings = new(readEngine);

        Assert.Equal(["Collateral"], strings.AddedRecords);
        Assert.Equal(["Ronin"], strings.RemovedRecords);

        // A scalar's key is its own value, so a string never reads as replaced: a different value is a
        // different record.
        Assert.Empty(strings.UpdatedRecords);
    }

    [Fact]
    public void EveryScalarTypeHasAnAccessor()
    {
        Producer producer = new();
        HollowReadStateEngine readEngine = producer.PublishSnapshot(
            new Scalars
            {
                Text = "one",
                Number = 1,
                Big = 2L,
                Small = 3f,
                Wide = 4d,
                Flag = true,
                Money = 5.5m,
            });

        // Nullable, so each maps as a reference to its wrapper type rather than being inlined.
        Assert.Equal(["one"], new StringDataAccessor(readEngine).AllRecords);
        Assert.Equal([1], new IntegerDataAccessor(readEngine).AllRecords);
        Assert.Equal([2L], new LongDataAccessor(readEngine).AllRecords);
        Assert.Equal([3f], new FloatDataAccessor(readEngine).AllRecords);
        Assert.Equal([4d], new DoubleDataAccessor(readEngine).AllRecords);
        Assert.Equal([true], new BooleanDataAccessor(readEngine).AllRecords);
        Assert.Equal([5.5m], new DecimalDataAccessor(readEngine).AllRecords);
    }

    [Fact]
    public void AScalarTypeTheDatasetDoesNotHoldIsRefused()
    {
        Producer producer = new();
        HollowReadStateEngine readEngine = producer.PublishSnapshot(Film(1, "Heat"));

        // A film catalogue holds strings, and nothing else loose: its id is inlined.
        Assert.Throws<ArgumentException>(() => new IntegerDataAccessor(readEngine));
    }

    // ---- What the generator emits ----

    [Fact]
    public void AKeyedTypeGetsAGeneratedAccessorAndAKeylessOneDoesNot()
    {
        IReadOnlyDictionary<string, string> files = Generate();

        Assert.Contains("MovieDataAccessor.cs", files.Keys);
        Assert.DoesNotContain("ScalarsDataAccessor.cs", files.Keys);

        string source = files["MovieDataAccessor.cs"];

        Assert.Contains("public sealed class MovieDataAccessor : HollowDataAccessor<Movie>", source, StringComparison.Ordinal);
        Assert.Contains("public const string TypeName = \"Movie\";", source, StringComparison.Ordinal);

        // All it adds to the base class is a read through the generated API.
        Assert.Contains("public override Movie GetRecord(int ordinal) =>", source, StringComparison.Ordinal);
        Assert.Contains("_api.GetMovie(ordinal)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGeneratedAccessorCanBeTurnedOff()
    {
        Assert.DoesNotContain("MovieDataAccessor.cs", Generate(accessors: false).Keys);
    }

    private static IReadOnlyDictionary<string, string> Generate(bool accessors = true) =>
        new HollowCodeGenerator(
                new HollowCodeGeneratorOptions
                {
                    Namespace = "Hollow.Tests.Generated.Accessors",
                    ApiClassName = "AccessorsApi",
                    GenerateDataAccessors = accessors,
                })
            .Generate(typeof(Movie), typeof(Scalars));

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

    public sealed class Scalars
    {
        public required string Text { get; init; }

        public required int? Number { get; init; }

        public required long? Big { get; init; }

        public required float? Small { get; init; }

        public required double? Wide { get; init; }

        public required bool? Flag { get; init; }

        public required decimal? Money { get; init; }
    }

    /// <summary>
    /// The smallest thing a subclass has to be: a type name and a read.
    /// </summary>
    /// <remarks>
    /// Reads the id straight out of the data access rather than through a generated client, so the
    /// test says what the base class does and nothing about the code generator.
    /// </remarks>
    private sealed class MovieAccessor : HollowDataAccessor<int>
    {
        private readonly IHollowObjectTypeDataAccess _movies;
        private readonly int _idField;

        internal MovieAccessor(HollowReadStateEngine stateEngine)
            : base(stateEngine, "Movie") => (_movies, _idField) = Access(stateEngine);

        internal MovieAccessor(HollowReadStateEngine stateEngine, params string[] fieldPaths)
            : base(stateEngine, "Movie", fieldPaths) => (_movies, _idField) = Access(stateEngine);

        public override int GetRecord(int ordinal) => _movies.ReadInt(ordinal, _idField);

        private static (IHollowObjectTypeDataAccess Movies, int IdField) Access(
            HollowReadStateEngine stateEngine)
        {
            IHollowObjectTypeDataAccess movies =
                (IHollowObjectTypeDataAccess)stateEngine.GetTypeDataAccess("Movie")!;

            return (movies, ((HollowObjectSchema)movies.Schema).GetPosition("Id"));
        }
    }
}
