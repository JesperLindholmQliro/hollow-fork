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

using System.Globalization;
using Hollow.Api.Consumer;
using Hollow.Api.Consumer.Fs;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Schema;
using Hollow.Core.Util;
using Hollow.Core.Write;

namespace Hollow.Tests.Api;

/// <summary>
/// A consumer reading from a directory of blob files, which is the simplest real blob store there is.
/// </summary>
/// <remarks>
/// The file layout — <c>snapshot-{version}</c>, <c>delta-{from}-{to}</c> and so on — is the one a
/// filesystem-backed Java producer writes, so these also pin that layout.
/// </remarks>
public class FilesystemBlobStoreTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("hollow-filesystem-blob-store-").FullName;

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static HollowObjectSchema MovieSchema()
    {
        HollowObjectSchema schema = new("Movie", 1);
        schema.AddField("title", FieldType.String);
        return schema;
    }

    private static void AddMovie(HollowWriteStateEngine engine, HollowObjectSchema schema, string title)
    {
        HollowObjectWriteRecord record = new(schema);
        record.SetString("title", title);
        engine.Add("Movie", record);
    }

    /// <summary>
    /// Writes the blobs for one cycle into the directory, under the names the retriever looks for.
    /// </summary>
    /// <remarks>
    /// Java's producer API does this; it is not ported, so the layout is reproduced here.
    /// </remarks>
    private void Publish(HollowWriteStateEngine producer, long version, long? previousVersion)
    {
        HollowBlobWriter writer = new(producer);

        Write($"snapshot-{version.Invariant()}", writer.WriteSnapshot);

        if (previousVersion is { } from)
        {
            Write($"delta-{from.Invariant()}-{version.Invariant()}", writer.WriteDelta);
            Write($"reversedelta-{version.Invariant()}-{from.Invariant()}", writer.WriteReverseDelta);
        }

        producer.PrepareForNextCycle();
    }

    private void Write(string fileName, Action<Stream> write)
    {
        using FileStream stream = File.Create(Path.Combine(_directory, fileName));
        write(stream);
    }

    private void Announce(long version) =>
        File.WriteAllText(
            Path.Combine(_directory, HollowFilesystemAnnouncementWatcher.AnnouncementFileName),
            version.ToString(CultureInfo.InvariantCulture));

    private IReadOnlyList<string> Titles(HollowConsumer consumer)
    {
        HollowObjectTypeReadState state =
            Assert.IsType<HollowObjectTypeReadState>(consumer.StateEngine!.GetTypeState("Movie"));

        int titlePosition = state.Schema.GetPosition("title");

        return
        [
            .. Enumerable.Range(0, state.MaxOrdinal + 1)
                .Where(ordinal => state.PopulatedOrdinals.Get(ordinal))
                .Select(ordinal => state.ReadString(ordinal, titlePosition)!)
        ];
    }

    /// <summary>
    /// Publishes two cycles at versions 100 and 200.
    /// </summary>
    private void PublishTwoCycles()
    {
        HollowObjectSchema schema = MovieSchema();
        HollowWriteStateEngine producer = HollowWriteStateCreator.CreateWithSchemas([schema]);

        AddMovie(producer, schema, "one");
        Publish(producer, 100, previousVersion: null);

        AddMovie(producer, schema, "one");
        AddMovie(producer, schema, "two");
        Publish(producer, 200, previousVersion: 100);
    }

    [Fact]
    public void AConsumerReadsSnapshotsAndDeltasFromADirectory()
    {
        PublishTwoCycles();

        using HollowConsumer consumer = new HollowConsumerBuilder()
            .WithLocalBlobStore(_directory)
            .Build();

        consumer.TriggerRefreshTo(100);
        Assert.Equal(["one"], Titles(consumer));

        consumer.TriggerRefreshTo(200);
        Assert.Equal(["one", "two"], Titles(consumer));

        consumer.TriggerRefreshTo(100);
        Assert.Equal(["one"], Titles(consumer));
    }

    [Fact]
    public void AnAnnouncementWatcherFollowsTheAnnouncementFile()
    {
        PublishTwoCycles();
        Announce(100);

        // Polling off: this test drives the watcher directly, so it does not depend on timing.
        using HollowFilesystemAnnouncementWatcher watcher = new(_directory, TimeSpan.Zero);

        using HollowConsumer consumer = new HollowConsumerBuilder()
            .WithLocalBlobStore(_directory)
            .WithAnnouncementWatcher(watcher)
            .Build();

        Assert.Equal(100, watcher.GetLatestVersion());

        consumer.TriggerRefresh();
        Assert.Equal(100, consumer.CurrentVersionId);

        Announce(200);
        watcher.Poll();

        // Poll refreshes subscribed consumers in the background, so wait for it to land.
        SpinWait.SpinUntil(() => consumer.CurrentVersionId == 200, TimeSpan.FromSeconds(10));

        Assert.Equal(200, consumer.CurrentVersionId);
        Assert.Equal(["one", "two"], Titles(consumer));
    }

    [Fact]
    public void NothingAnnouncedReadsAsNoVersion()
    {
        using HollowFilesystemAnnouncementWatcher watcher = new(_directory, TimeSpan.Zero);

        Assert.Equal(IAnnouncementWatcher.NoAnnouncementAvailable, watcher.GetLatestVersion());
    }

    /// <summary>
    /// Producers leave temporary and checksum files in the blob directory, so a name whose trailing
    /// segment is not a number has to be ignored rather than taken for a blob.
    /// </summary>
    [Fact]
    public void FilesThatAreNotBlobsAreIgnored()
    {
        PublishTwoCycles();

        File.WriteAllText(Path.Combine(_directory, "snapshot-200.tmp"), "not a blob");
        File.WriteAllText(Path.Combine(_directory, "snapshot-abc"), "not a blob");

        HollowFilesystemBlobRetriever retriever = new(_directory);

        Blob snapshot = Assert.IsAssignableFrom<Blob>(retriever.RetrieveSnapshotBlob(150));
        Assert.Equal(100, snapshot.ToVersion);
    }

    /// <summary>
    /// Matching a transition on its from-version has to be exact: version 10 and version 100 share a
    /// prefix.
    /// </summary>
    [Fact]
    public void ATransitionIsMatchedOnItsWholeFromVersion()
    {
        HollowObjectSchema schema = MovieSchema();
        HollowWriteStateEngine producer = HollowWriteStateCreator.CreateWithSchemas([schema]);

        AddMovie(producer, schema, "one");
        Publish(producer, 10, previousVersion: null);

        AddMovie(producer, schema, "two");
        Publish(producer, 20, previousVersion: 10);

        HollowFilesystemBlobRetriever retriever = new(_directory);

        // There is a delta-10-20 but no delta from 1, and "delta-1" is a prefix of "delta-10-20".
        Assert.Null(retriever.RetrieveDeltaBlob(1));

        Blob delta = Assert.IsAssignableFrom<Blob>(retriever.RetrieveDeltaBlob(10));
        Assert.Equal(20, delta.ToVersion);
    }

    [Fact]
    public void AMissingDirectoryIsRejected()
    {
        Assert.Throws<DirectoryNotFoundException>(
            () => new HollowFilesystemBlobRetriever(Path.Combine(_directory, "nope")));
    }
}
