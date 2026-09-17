/*
 *  Copyright 2016 Netflix, Inc.
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
using Hollow.Reference.Infrastructure.Storage;
using Hollow.Reference.Infrastructure.Storage.Local;

namespace Hollow.Reference.Tests;

/// <summary>
/// The watching folder that stands in for the Java reference implementation's DynamoDB table.
/// </summary>
public sealed class LocalAnnouncementStoreTests
{
    private static readonly Dictionary<string, string> NoMetadata = new(StringComparer.Ordinal);

    [Fact]
    public async Task AnnouncingAVersionLeavesAFileNamedAfterIt()
    {
        using TemporaryDirectory root = new();
        LocalAnnouncementStore store = new(root.Path, SimulatedLatency.None);

        await store.AnnounceAsync(20240101000000001, NoMetadata, Token);

        Assert.True(File.Exists(Path.Combine(store.WatchingDirectory, "20240101000000001")));
    }

    [Fact]
    public async Task TheAnnouncedVersionIsTheNewestFileInTheFolder()
    {
        using TemporaryDirectory root = new();
        LocalAnnouncementStore store = new(root.Path, SimulatedLatency.None);

        await store.AnnounceAsync(20240101000000001, NoMetadata, Token);
        await store.AnnounceAsync(20240101000010002, NoMetadata, Token);
        await store.AnnounceAsync(20240101000020003, NoMetadata, Token);

        AnnouncedVersions announced = await store.ReadAsync(Token);

        Assert.Equal(20240101000020003, announced.AnnouncedVersion);
        Assert.Equal(20240101000020003, announced.EffectiveVersion);
        Assert.False(announced.IsPinned);
    }

    [Fact]
    public async Task NothingAnnouncedYetIsAnOrdinaryAnswer()
    {
        // A consumer may well start before its producer.
        using TemporaryDirectory root = new();
        LocalAnnouncementStore store = new(root.Path, SimulatedLatency.None);

        AnnouncedVersions announced = await store.ReadAsync(Token);

        Assert.Equal(IAnnouncementWatcher.NoAnnouncementAvailable, announced.AnnouncedVersion);
        Assert.Equal(IAnnouncementWatcher.NoAnnouncementAvailable, announced.EffectiveVersion);
    }

    [Fact]
    public async Task WhateverTheProducerSaidAboutTheAnnouncementComesBackWithIt()
    {
        using TemporaryDirectory root = new();
        LocalAnnouncementStore store = new(root.Path, SimulatedLatency.None);

        await store.AnnounceAsync(
            20240101000000001,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["source"] = "nightly" },
            Token);

        Assert.Equal("nightly", (await store.ReadAsync(Token)).Metadata["source"]);
    }

    [Fact]
    public async Task APinBeatsEveryAnnouncementAfterIt()
    {
        // The reason DynamoDB's row carries pin_version: a bad dataset is rolled back by holding
        // consumers on the last good version, without rolling back the producer.
        using TemporaryDirectory root = new();
        LocalAnnouncementStore store = new(root.Path, SimulatedLatency.None);

        await store.AnnounceAsync(20240101000000001, NoMetadata, Token);
        await store.AnnounceAsync(20240101000010002, NoMetadata, Token);

        store.Pin(20240101000000001);

        AnnouncedVersions pinned = await store.ReadAsync(Token);

        Assert.True(pinned.IsPinned);
        Assert.Equal(20240101000010002, pinned.AnnouncedVersion);
        Assert.Equal(20240101000000001, pinned.EffectiveVersion);

        store.Pin(null);

        Assert.Equal(20240101000010002, (await store.ReadAsync(Token)).EffectiveVersion);
    }

    [Fact]
    public async Task ThePinFileIsNotMistakenForAnAnnouncement()
    {
        using TemporaryDirectory root = new();
        LocalAnnouncementStore store = new(root.Path, SimulatedLatency.None);

        store.Pin(20240101000000001);

        // Nothing has been announced: only pinned. The two are separate answers.
        Assert.Equal(
            IAnnouncementWatcher.NoAnnouncementAvailable, (await store.ReadAsync(Token)).AnnouncedVersion);
    }

    [Fact]
    public async Task TheFolderIsPrunedSoThatAProducerCanRunForWeeks()
    {
        using TemporaryDirectory root = new();
        LocalAnnouncementStore store = new(root.Path, SimulatedLatency.None, announcementsToKeep: 3);

        for (int i = 1; i <= 10; i++)
        {
            await store.AnnounceAsync(20240101000000000 + i, NoMetadata, Token);
        }

        Assert.Equal(3, Directory.GetFiles(store.WatchingDirectory).Length);

        // The newest survives, which is the only one that has to.
        Assert.Equal(20240101000000010, (await store.ReadAsync(Token)).AnnouncedVersion);
    }

    [Fact]
    public async Task ANewFileInTheFolderWakesTheWatcher()
    {
        // This is the whole point of the watching folder: the consumer is told rather than polling.
        using TemporaryDirectory root = new();
        LocalAnnouncementStore store = new(root.Path, SimulatedLatency.None);

        using SemaphoreSlim changed = new(0);
        using IDisposable? subscription = store.Subscribe(() => changed.Release());

        Assert.NotNull(subscription);

        await store.AnnounceAsync(20240101000000001, NoMetadata, Token);

        Assert.True(
            await changed.WaitAsync(TimeSpan.FromSeconds(15), Token),
            "the file system watcher never reported the new announcement");
    }
}
