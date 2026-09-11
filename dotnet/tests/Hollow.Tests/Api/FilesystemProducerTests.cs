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

using System.IO.Compression;
using Hollow.Api.Consumer;
using Hollow.Api.Consumer.Fs;
using Hollow.Api.Producer;
using Hollow.Api.Producer.Fs;
using Hollow.Core;
using Hollow.Core.Index;
using Hollow.Core.Index.Key;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Write.ObjectMapper;
using Hollow.Api.Producer.Listener;
using ProducerBlob = Hollow.Api.Producer.Blob;
using ReadStateEngine = Hollow.Core.Read.Engine.HollowReadStateEngine;

namespace Hollow.Tests.Api;

/// <summary>
/// A producer and a consumer sharing a directory, which is the whole loop end to end.
/// </summary>
/// <remarks>
/// Everything else is tested against in-memory stand-ins. This is the test that would catch a producer
/// and a consumer that each work but do not agree — a file name, a staging step, an announcement
/// format.
/// </remarks>
public class FilesystemProducerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hollow-filesystem-producer-").FullName;

    private string BlobStore => Path.Combine(_root, "blobs");

    private string Staging => Path.Combine(_root, "staging");

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [HollowPrimaryKey("Id")]
    public sealed record Movie(int Id, string Title, int Year);

    private HollowProducer Producer(IBlobCompressor? compressor = null)
    {
        HollowProducer producer = new HollowProducerBuilder()
            .WithBlobStager(new HollowFilesystemBlobStager(Staging, compressor))
            .WithPublisher(new HollowFilesystemPublisher(BlobStore))
            .WithAnnouncer(new HollowFilesystemAnnouncer(BlobStore))
            .Build();

        producer.InitializeDataModel(typeof(Movie));

        return producer;
    }

    private static Populator Movies(params Movie[] movies) =>
        state =>
        {
            foreach (Movie movie in movies)
            {
                state.Add(movie);
            }
        };

    private static string Title(HollowConsumer consumer, int ordinal)
    {
        HollowObjectTypeReadState movies =
            Assert.IsType<HollowObjectTypeReadState>(consumer.StateEngine!.GetTypeState("Movie"));
        HollowObjectTypeReadState strings =
            Assert.IsType<HollowObjectTypeReadState>(consumer.StateEngine.GetTypeState("String"));

        int titleOrdinal = movies.ReadOrdinal(ordinal, movies.Schema.GetPosition("Title"));

        return strings.ReadString(titleOrdinal, strings.Schema.GetPosition("value"))!;
    }

    [Fact]
    public void AProducerAndAConsumerSharingADirectoryStayInStep()
    {
        HollowProducer producer = Producer();

        long first = producer.RunCycle(Movies(new Movie(1, "one", 2001), new Movie(2, "two", 2002)));

        using HollowFilesystemAnnouncementWatcher watcher = new(BlobStore, TimeSpan.Zero);
        using HollowConsumer consumer = new HollowConsumerBuilder()
            .WithLocalBlobStore(BlobStore)
            .WithAnnouncementWatcher(watcher)
            .Build();

        consumer.TriggerRefresh();

        Assert.Equal(first, consumer.CurrentVersionId);
        Assert.Equal("one", Title(consumer, 0));
        Assert.Equal("two", Title(consumer, 1));

        ReadStateEngine afterFirst = consumer.StateEngine!;

        long second = producer.RunCycle(
            Movies(new Movie(1, "one", 2001), new Movie(2, "two", 2002), new Movie(3, "three", 2003)));

        watcher.Poll();
        SpinWait.SpinUntil(() => consumer.CurrentVersionId == second, TimeSpan.FromSeconds(10));

        Assert.Equal(second, consumer.CurrentVersionId);

        // The consumer followed the producer's delta, so its state engine — and anything indexed over
        // it — survived.
        Assert.Same(afterFirst, consumer.StateEngine);
        Assert.Equal("three", Title(consumer, 2));
    }

    /// <summary>
    /// An index built over the consumer's state keeps working across a delta, which is what the whole
    /// arrangement is for.
    /// </summary>
    [Fact]
    public void AnIndexOverTheConsumerSurvivesTheProducersNextCycle()
    {
        HollowProducer producer = Producer();
        producer.RunCycle(Movies(new Movie(1, "one", 2001), new Movie(2, "two", 2002)));

        using HollowConsumer consumer = new HollowConsumerBuilder().WithLocalBlobStore(BlobStore).Build();
        consumer.TriggerRefresh();

        using HollowPrimaryKeyIndex index = new(consumer.StateEngine!, new PrimaryKey("Movie", "Id"));

        // An index only follows deltas if it is told to; otherwise it is a snapshot of the state it was
        // built over.
        index.ListenForDeltaUpdates();

        Assert.Equal(0, index.GetMatchingOrdinal(1));
        Assert.Equal(1, index.GetMatchingOrdinal(2));

        long second = producer.RunCycle(
            Movies(new Movie(1, "one", 2001), new Movie(2, "two", 2002), new Movie(3, "three", 2003)));

        consumer.TriggerRefresh();
        Assert.Equal(second, consumer.CurrentVersionId);

        // The index is listening to the state engine's ordinal changes, so the records it already knew
        // about are still where it left them.
        Assert.Equal(0, index.GetMatchingOrdinal(1));
        Assert.Equal(1, index.GetMatchingOrdinal(2));
        Assert.Equal(2, index.GetMatchingOrdinal(3));
    }

    [Fact]
    public void ARestartedProducerContinuesTheChainOnDisk()
    {
        HollowProducer first = Producer();
        long firstVersion = first.RunCycle(Movies(new Movie(1, "one", 2001)));

        using HollowConsumer consumer = new HollowConsumerBuilder().WithLocalBlobStore(BlobStore).Build();
        consumer.TriggerRefreshTo(firstVersion);
        ReadStateEngine stateEngine = consumer.StateEngine!;

        // A new producer process, pointed at the same directory.
        HollowProducer restarted = Producer();
        HollowFilesystemBlobRetriever retriever = new(BlobStore);

        using HollowFilesystemAnnouncementWatcher watcher = new(BlobStore, TimeSpan.Zero);
        IReadState? restored = restarted.Restore(watcher.GetLatestVersion(), retriever);

        Assert.NotNull(restored);
        Assert.Equal(firstVersion, restored.Version);

        long secondVersion = restarted.RunCycle(Movies(new Movie(1, "one", 2001), new Movie(2, "two", 2002)));

        consumer.TriggerRefreshTo(secondVersion);

        Assert.Same(stateEngine, consumer.StateEngine);
        Assert.Equal("one", Title(consumer, 0));
        Assert.Equal("two", Title(consumer, 1));
    }

    [Fact]
    public void StagedBlobsAreCleanedUpAfterEachCycle()
    {
        HollowProducer producer = Producer();

        producer.RunCycle(Movies(new Movie(1, "one", 2001)));
        producer.RunCycle(Movies(new Movie(1, "one", 2001), new Movie(2, "two", 2002)));

        Assert.Empty(Directory.EnumerateFileSystemEntries(Staging));
    }

    [Fact]
    public void TheBlobStoreHoldsTheExpectedFiles()
    {
        HollowProducer producer = Producer();

        long first = producer.RunCycle(Movies(new Movie(1, "one", 2001)));
        long second = producer.RunCycle(Movies(new Movie(1, "one", 2001), new Movie(2, "two", 2002)));

        string[] names = [.. Directory.EnumerateFiles(BlobStore).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

        Assert.Equal(
            [
                HollowFilesystemAnnouncementWatcher.AnnouncementFileName,
                $"delta-{first}-{second}",
                $"header-{first}",
                $"header-{second}",
                $"reversedelta-{second}-{first}",
                $"snapshot-{first}",
                $"snapshot-{second}",
            ],
            names.Order(StringComparer.Ordinal));
    }

    private sealed class GzipCompressor : IBlobCompressor
    {
        public Stream Compress(Stream stream) => new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true);

        public Stream Decompress(Stream stream) => new GZipStream(stream, CompressionMode.Decompress);
    }

    private sealed class StagedBlobRecorder : HollowProducerListener
    {
        internal List<(string Path, byte[] FirstBytes)> Staged { get; } = [];

        public override void OnBlobStage(Status status, ProducerBlob blob, TimeSpan elapsed)
        {
            byte[] firstBytes = new byte[2];
            using FileStream file = File.OpenRead(blob.Path!);
            file.ReadExactly(firstBytes);

            Staged.Add((blob.Path!, firstBytes));
        }
    }

    /// <summary>
    /// A compressor wraps the <em>staged</em> bytes, not the published ones.
    /// </summary>
    /// <remarks>
    /// This is Java's behaviour and it is easy to misread: the compressor keeps a large cycle's staging
    /// files small, but a publisher reads a staged blob back through the same compressor, so what
    /// reaches the blob store is the plain bytes a consumer expects. A blob store that wants
    /// compression at rest does it in its own publisher.
    /// </remarks>
    [Fact]
    public void ACompressorCompressesTheStagedBlobsAndNotThePublishedOnes()
    {
        StagedBlobRecorder recorder = new();

        HollowProducer producer = new HollowProducerBuilder()
            .WithBlobStager(new HollowFilesystemBlobStager(Staging, new GzipCompressor()))
            .WithPublisher(new HollowFilesystemPublisher(BlobStore))
            .WithAnnouncer(new HollowFilesystemAnnouncer(BlobStore))
            .WithListeners(recorder)
            .Build();

        producer.InitializeDataModel(typeof(Movie));

        long version = producer.RunCycle(
            Movies([.. Enumerable.Range(1, 500).Select(i => new Movie(i, $"movie {i}", 2000 + (i % 20)))]));

        // Gzip's magic number: the staged snapshot really did go through the compressor.
        Assert.NotEmpty(recorder.Staged);
        Assert.All(recorder.Staged, staged => Assert.Equal([0x1F, 0x8B], staged.FirstBytes));

        // What a consumer downloads is not compressed, and reads back as it should.
        using HollowConsumer consumer = new HollowConsumerBuilder().WithLocalBlobStore(BlobStore).Build();
        consumer.TriggerRefreshTo(version);

        Assert.Equal(500, consumer.StateEngine!.GetTypeState("Movie")!.PopulatedOrdinals.Cardinality());
        Assert.Equal("movie 1", Title(consumer, 0));
    }

    [Fact]
    public void AnEmptyBlobStoreAnnouncesNothing()
    {
        Directory.CreateDirectory(BlobStore);

        using HollowFilesystemAnnouncementWatcher watcher = new(BlobStore, TimeSpan.Zero);

        Assert.Equal(HollowConstants.VersionNone, watcher.GetLatestVersion());
    }
}
