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

using Hollow.Api.Consumer;
using Hollow.Api.Consumer.Fs;
using Hollow.Api.Producer;
using Hollow.Api.Producer.Fs;
using Hollow.Core.Read.Engine;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Tests.Api;

/// <summary>
/// A producer publishing optional parts, and consumers choosing whether to fetch them.
/// </summary>
/// <remarks>
/// Through a real blob store on disk, because the part file names are the contract between the two
/// sides — a producer writing <c>snapshot_reviews-5</c> and a consumer looking for anything else is a
/// failure the format tests cannot see.
/// </remarks>
public class OptionalBlobPartPipelineTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "hollow-parts-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void AConsumerThatWantsThePartGetsTheWholeDataset()
    {
        Publish();

        HollowConsumer consumer = Consumer("reviews");
        consumer.TriggerRefresh();

        HollowReadStateEngine engine = consumer.StateEngine!;

        Assert.Equal(2, engine.GetTypeState("Movie")!.PopulatedOrdinals.Cardinality());
        Assert.Equal(2, engine.GetTypeState("Review")!.PopulatedOrdinals.Cardinality());
    }

    [Fact]
    public void AConsumerThatDoesNotWantThePartSimplyDoesNotHaveThoseTypes()
    {
        Publish();

        HollowConsumer consumer = Consumer();
        consumer.TriggerRefresh();

        HollowReadStateEngine engine = consumer.StateEngine!;

        Assert.Equal(2, engine.GetTypeState("Movie")!.PopulatedOrdinals.Cardinality());

        // Which is the point of the whole feature: this consumer never fetched the bytes.
        Assert.Null(engine.GetTypeState("Review"));
    }

    [Fact]
    public void ThePartFilesAreNamedWhereAConsumerLooksForThem()
    {
        long version = Publish();

        Assert.True(File.Exists(Path.Combine(_directory, $"snapshot-{version}")));
        Assert.True(File.Exists(Path.Combine(_directory, $"snapshot_reviews-{version}")));
    }

    [Fact]
    public void ADeltaAndItsPartCarryTheConsumerForward()
    {
        Producer producer = new(_directory);

        producer.Cycle(state =>
        {
            state.Add(new Movie(1, "Heat"));
            state.Add(new Review(1, "great"));
        });

        HollowConsumer consumer = Consumer("reviews");
        consumer.TriggerRefresh();

        producer.Cycle(state =>
        {
            state.Add(new Movie(1, "Heat"));
            state.Add(new Movie(2, "Ronin"));
            state.Add(new Review(1, "great"));
            state.Add(new Review(2, "fine"));
        });

        consumer.TriggerRefresh();

        Assert.Equal(2, consumer.StateEngine!.GetTypeState("Movie")!.PopulatedOrdinals.Cardinality());
        Assert.Equal(2, consumer.StateEngine.GetTypeState("Review")!.PopulatedOrdinals.Cardinality());
    }

    [Fact]
    public void ARetrieverNamingNoPartsReportsNone() =>
        Assert.Null(new HollowFilesystemBlobRetriever(Prepared()).ConfiguredOptionalBlobParts);

    [Fact]
    public void ARetrieverNamingPartsReportsThem() =>
        Assert.Equal(
            ["reviews"],
            new HollowFilesystemBlobRetriever(Prepared(), "reviews").ConfiguredOptionalBlobParts!);

    /// <inheritdoc />
    public void Dispose()
    {
        GC.SuppressFinalize(this);

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string Prepared()
    {
        Directory.CreateDirectory(_directory);

        return _directory;
    }

    private long Publish()
    {
        Producer producer = new(_directory);

        return producer.Cycle(state =>
        {
            state.Add(new Movie(1, "Heat"));
            state.Add(new Movie(2, "Ronin"));
            state.Add(new Review(1, "great"));
            state.Add(new Review(2, "fine"));
        });
    }

    private HollowConsumer Consumer(params string[] parts) =>
        new HollowConsumerBuilder()
            .WithBlobRetriever(new HollowFilesystemBlobRetriever(Prepared(), parts))
            .Build();

    /// <summary>A producer whose reviews live in a part of their own.</summary>
    private sealed class Producer
    {
        private readonly HollowProducer _producer;

        internal Producer(string directory)
        {
            Directory.CreateDirectory(directory);

            OptionalBlobPartConfig config = new();
            config.AddTypesToPart("reviews", "Review");

            _producer = new HollowProducerBuilder()
                .WithOptionalPartConfig(config)
                .WithBlobStager(new HollowFilesystemBlobStager(
                    Path.Combine(directory, "staging"), optionalPartConfig: config))
                .WithPublisher(new HollowFilesystemPublisher(directory))
                .WithAnnouncer(new HollowFilesystemAnnouncer(directory))
                .Build();

            _producer.InitializeDataModel(typeof(Movie), typeof(Review));
        }

        internal long Cycle(Populator populate) => _producer.RunCycle(populate);
    }

    [HollowPrimaryKey("Id")]
    private sealed record Movie(int Id, string Title);

    [HollowPrimaryKey("Id")]
    private sealed record Review(int Id, string Text);
}
