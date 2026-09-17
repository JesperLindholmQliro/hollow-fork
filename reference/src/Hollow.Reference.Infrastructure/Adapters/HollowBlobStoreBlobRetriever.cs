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

using System.Globalization;
using Hollow.Api.Consumer;
using Hollow.Reference.Infrastructure.Storage;

namespace Hollow.Reference.Infrastructure.Adapters;

/// <summary>
/// Finds the blobs a consumer needs in whichever blob store the mode chose.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>how.hollow.consumer.infrastructure.S3BlobRetriever</c>. It is the reading half of
/// <see cref="HollowBlobStorePublisher"/> and shares the key layout in <see cref="BlobKeys"/> with it,
/// which is what keeps the two from drifting: the Java pair agree by each spelling the same format
/// string.
/// </para>
/// <para>
/// A blob is found by its metadata rather than by fetching it, so deciding what to load costs a HEAD
/// and not a download — and a consumer asks for a delta on every refresh, most of which do not exist
/// yet.
/// </para>
/// </remarks>
public sealed class HollowBlobStoreBlobRetriever : IBlobRetriever
{
    private readonly IHollowBlobStore _store;
    private readonly string _blobNamespace;

    public HollowBlobStoreBlobRetriever(IHollowBlobStore store, string blobNamespace)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(blobNamespace);

        _store = store;
        _blobNamespace = blobNamespace;
    }

    public Blob? RetrieveSnapshotBlob(long desiredVersion) =>
        Synchronously.Run(() => RetrieveSnapshotBlobAsync(desiredVersion));

    public Blob? RetrieveDeltaBlob(long currentVersion) =>
        Synchronously.Run(() => RetrieveTransitionAsync(BlobType.Delta, currentVersion));

    public Blob? RetrieveReverseDeltaBlob(long currentVersion) =>
        Synchronously.Run(() => RetrieveTransitionAsync(BlobType.ReverseDelta, currentVersion));

    public HeaderBlob? RetrieveHeaderBlob(long currentVersion) =>
        Synchronously.Run(() => RetrieveHeaderBlobAsync(currentVersion));

    private async Task<Blob?> RetrieveSnapshotBlobAsync(
        long desiredVersion, CancellationToken cancellationToken = default)
    {
        // The happy path: a snapshot of exactly the version asked for.
        if (await KnownSnapshotAsync(desiredVersion, cancellationToken).ConfigureAwait(false) is { } exact)
        {
            return exact;
        }

        // There was not one, so fall back to the newest snapshot at or below it and let the consumer
        // walk deltas forwards from there.
        byte[]? index = await ReadSnapshotIndexAsync(cancellationToken).ConfigureAwait(false);

        if (index is null || SnapshotIndex.GreatestAtMost(index, desiredVersion) is not { } nearest)
        {
            return null;
        }

        return await KnownSnapshotAsync(nearest, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Blob?> KnownSnapshotAsync(long version, CancellationToken cancellationToken)
    {
        string key = BlobKeys.For(_blobNamespace, BlobType.Snapshot, version);

        IReadOnlyDictionary<string, string>? metadata =
            await _store.ReadMetadataAsync(key, cancellationToken).ConfigureAwait(false);

        if (metadata is null)
        {
            return null;
        }

        return new BlobStoreBlob(_store, key, ReadVersion(metadata, BlobKeys.ToStateMetadata, version));
    }

    private async Task<Blob?> RetrieveTransitionAsync(
        BlobType blobType, long fromVersion, CancellationToken cancellationToken = default)
    {
        string key = BlobKeys.For(_blobNamespace, blobType, fromVersion);

        IReadOnlyDictionary<string, string>? metadata =
            await _store.ReadMetadataAsync(key, cancellationToken).ConfigureAwait(false);

        // Not an error: a consumer asks for the delta out of the version it holds on every refresh, and
        // until the producer's next cycle there is none.
        if (metadata is null)
        {
            return null;
        }

        return new BlobStoreBlob(
            _store,
            key,
            ReadVersion(metadata, BlobKeys.FromStateMetadata, fromVersion),
            ReadVersion(metadata, BlobKeys.ToStateMetadata, fromVersion));
    }

    private async Task<HeaderBlob?> RetrieveHeaderBlobAsync(
        long currentVersion, CancellationToken cancellationToken = default)
    {
        string key = BlobKeys.For(_blobNamespace, BlobKeys.HeaderType, currentVersion);

        IReadOnlyDictionary<string, string>? metadata =
            await _store.ReadMetadataAsync(key, cancellationToken).ConfigureAwait(false);

        return metadata is null
            ? null
            : new BlobStoreHeaderBlob(
                _store, key, ReadVersion(metadata, BlobKeys.ToStateMetadata, currentVersion));
    }

    private async Task<byte[]?> ReadSnapshotIndexAsync(CancellationToken cancellationToken)
    {
        await using Stream? index = await _store
            .OpenReadAsync(BlobKeys.SnapshotIndexFor(_blobNamespace), cancellationToken)
            .ConfigureAwait(false);

        if (index is null)
        {
            return null;
        }

        using MemoryStream buffer = new();
        await index.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        return buffer.ToArray();
    }

    /// <summary>
    /// Reads a version out of the metadata, falling back to what the key said if it is missing or
    /// unreadable.
    /// </summary>
    /// <remarks>
    /// Java parses it and lets a <c>NumberFormatException</c> out. The key already carries the version
    /// the blob was filed under, so there is a better answer available than failing the refresh.
    /// </remarks>
    private static long ReadVersion(
        IReadOnlyDictionary<string, string> metadata, string name, long fallback) =>
        metadata.TryGetValue(name, out string? text)
            && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long version)
                ? version
                : fallback;

    private sealed class BlobStoreBlob : Blob
    {
        private readonly IHollowBlobStore _store;
        private readonly string _key;

        internal BlobStoreBlob(IHollowBlobStore store, string key, long toVersion)
            : base(toVersion)
        {
            _store = store;
            _key = key;
        }

        internal BlobStoreBlob(IHollowBlobStore store, string key, long fromVersion, long toVersion)
            : base(fromVersion, toVersion)
        {
            _store = store;
            _key = key;
        }

        public override Stream OpenStream() => OpenOrThrow(_store, _key);
    }

    private sealed class BlobStoreHeaderBlob : HeaderBlob
    {
        private readonly IHollowBlobStore _store;
        private readonly string _key;

        internal BlobStoreHeaderBlob(IHollowBlobStore store, string key, long version)
            : base(version)
        {
            _store = store;
            _key = key;
        }

        public override Stream OpenStream() => OpenOrThrow(_store, _key);
    }

    /// <summary>
    /// Opens the blob, failing loudly if it has gone since its metadata was read.
    /// </summary>
    /// <remarks>
    /// By the time a blob is opened the consumer has already committed to a plan that includes it, so
    /// there is no sensible way to answer "it is not there any more" other than to fail the refresh.
    /// That is what the store deleting a blob mid-plan looks like, and it is worth a message saying so.
    /// </remarks>
    private static Stream OpenOrThrow(IHollowBlobStore store, string key) =>
        Synchronously.Run(() => store.OpenReadAsync(key))
        ?? throw new IOException($"the blob '{key}' was there a moment ago and is not there now");
}
