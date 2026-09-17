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

using System.Text;
using Hollow.Reference.Infrastructure.Storage.Local;

namespace Hollow.Reference.Tests;

/// <summary>
/// The directory that stands in for a bucket, held to what the retriever assumes of one.
/// </summary>
public sealed class LocalBlobStoreTests
{
    [Fact]
    public async Task WhatIsWrittenComesBackWithItsMetadata()
    {
        using TemporaryDirectory root = new();
        LocalBlobStore store = new(root.Path, SimulatedLatency.None);

        await store.WriteAsync(
            "catalogue/snapshot/abc-1",
            new MemoryStream("a snapshot"u8.ToArray()),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["to_state"] = "1" },
            Token);

        await using Stream? content = await store.OpenReadAsync("catalogue/snapshot/abc-1", Token);

        Assert.NotNull(content);
        Assert.Equal("a snapshot", await new StreamReader(content, Encoding.UTF8).ReadToEndAsync(Token));

        IReadOnlyDictionary<string, string>? metadata =
            await store.ReadMetadataAsync("catalogue/snapshot/abc-1", Token);

        Assert.NotNull(metadata);
        Assert.Equal("1", metadata["to_state"]);
    }

    [Fact]
    public async Task AMissingBlobIsNullRatherThanAnException()
    {
        // The retriever asks for a delta out of the current version on every refresh, and until the
        // producer's next cycle there is none. It has to be an ordinary answer.
        using TemporaryDirectory root = new();
        LocalBlobStore store = new(root.Path, SimulatedLatency.None);

        Assert.Null(await store.OpenReadAsync("catalogue/delta/abc-1", Token));
        Assert.Null(await store.ReadMetadataAsync("catalogue/delta/abc-1", Token));
    }

    [Fact]
    public async Task AKeyWithSlashesInItBecomesADirectoryStructure()
    {
        using TemporaryDirectory root = new();
        LocalBlobStore store = new(root.Path, SimulatedLatency.None);

        await store.WriteAsync("catalogue/snapshot/abc-1", new MemoryStream([1]), Empty, Token);

        Assert.True(File.Exists(Path.Combine(store.BlobDirectory, "catalogue", "snapshot", "abc-1")));
    }

    [Fact]
    public async Task ListingFindsTheBlobsUnderAPrefixAndNotTheirMetadata()
    {
        using TemporaryDirectory root = new();
        LocalBlobStore store = new(root.Path, SimulatedLatency.None);

        await store.WriteAsync("catalogue/snapshot/abc-1", new MemoryStream([1]), Empty, Token);
        await store.WriteAsync("catalogue/snapshot/def-2", new MemoryStream([2]), Empty, Token);
        await store.WriteAsync("catalogue/delta/ghi-1", new MemoryStream([3]), Empty, Token);

        List<string> keys = [];

        await foreach (string key in store.ListKeysAsync("catalogue/snapshot/", Token))
        {
            keys.Add(key);
        }

        // The sidecar the metadata lives in is an implementation detail of this store, and the
        // publisher rebuilding its snapshot index would otherwise count every version twice.
        Assert.Equal(
            ["catalogue/snapshot/abc-1", "catalogue/snapshot/def-2"],
            keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AKeyCannotPointOutsideTheBlobDirectory()
    {
        using TemporaryDirectory root = new();
        LocalBlobStore store = new(root.Path, SimulatedLatency.None);

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.WriteAsync("../../escaped", new MemoryStream([1]), Empty, Token));
    }

    [Fact]
    public async Task AWrittenBlobIsNeverVisibleHalfFinished()
    {
        // The retriever lists a directory to rebuild the snapshot index and opens whatever it finds.
        // A blob that appeared before its bytes did would read as a truncated snapshot.
        using TemporaryDirectory root = new();
        LocalBlobStore store = new(root.Path, SimulatedLatency.None);

        byte[] large = new byte[4 * 1024 * 1024];
        await store.WriteAsync("catalogue/snapshot/abc-1", new MemoryStream(large), Empty, Token);

        await using Stream? content = await store.OpenReadAsync("catalogue/snapshot/abc-1", Token);

        Assert.NotNull(content);
        Assert.Equal(large.Length, content.Length);
    }

    private static Dictionary<string, string> Empty => new(StringComparer.Ordinal);
}
