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
using Hollow.Api.Producer;
using Hollow.Reference.Infrastructure.Storage;
using Blob = Hollow.Api.Producer.Blob;
using HeaderBlob = Hollow.Api.Producer.HeaderBlob;

namespace Hollow.Reference.Infrastructure.Adapters;

/// <summary>
/// Publishes the blobs a cycle produced into whichever blob store the mode chose.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>how.hollow.producer.infrastructure.S3Publisher</c>, with the S3 calls behind
/// <see cref="IHollowBlobStore"/> so that the same key layout, metadata and snapshot index serve all
/// three modes.
/// </para>
/// <para>
/// The snapshot index is this class's other job. Every snapshot published is added to a list of the
/// versions a snapshot exists for, and that list is written back to the store as one small object —
/// see <see cref="SnapshotIndex"/> for why a consumer cannot do without it.
/// </para>
/// </remarks>
public sealed class HollowBlobStorePublisher : IPublisher, IDisposable
{
    private readonly IHollowBlobStore _store;
    private readonly string _blobNamespace;
    private readonly SemaphoreSlim _snapshotIndexLock = new(1, 1);

    private List<long>? _snapshotIndex;

    public HollowBlobStorePublisher(IHollowBlobStore store, string blobNamespace)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(blobNamespace);

        _store = store;
        _blobNamespace = blobNamespace;
    }

    public void Publish(IPublishArtifact publishArtifact)
    {
        ArgumentNullException.ThrowIfNull(publishArtifact);

        Synchronously.Run(() => PublishAsync(publishArtifact));
    }

    public async Task PublishAsync(
        IPublishArtifact publishArtifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publishArtifact);

        switch (publishArtifact)
        {
            case Blob { OptionalPartNames.Count: > 0 }:
                // The Java reference implementation has no notion of optional blob parts, and neither
                // has this. Refusing is better than publishing the blob and dropping its parts, which
                // would read back as a dataset that is quietly missing types.
                throw new NotSupportedException(
                    "This reference implementation does not publish optional blob parts. Configure the "
                    + "producer without them, or extend HollowBlobStorePublisher and "
                    + "HollowBlobStoreBlobRetriever together.");

            case Blob { BlobType: BlobType.Snapshot } snapshot:
                // Read before the blob is written, because the index is rebuilt by listing the store:
                // done afterwards, the listing would find the version about to be added and conclude it
                // was already indexed, and the index object would never be written at all.
                await EnsureSnapshotIndexLoadedAsync(cancellationToken).ConfigureAwait(false);

                await WriteAsync(
                    BlobKeys.For(_blobNamespace, BlobType.Snapshot, snapshot.ToVersion),
                    snapshot,
                    ToState(snapshot.ToVersion),
                    cancellationToken).ConfigureAwait(false);

                await AddToSnapshotIndexAsync(snapshot.ToVersion, cancellationToken).ConfigureAwait(false);
                break;

            case Blob blob:
                // A delta is filed under the version it starts from: a consumer holding that version
                // asks for the delta out of it without knowing where it leads.
                await WriteAsync(
                    BlobKeys.For(_blobNamespace, blob.BlobType, blob.FromVersion),
                    blob,
                    FromAndToState(blob.FromVersion, blob.ToVersion),
                    cancellationToken).ConfigureAwait(false);
                break;

            case HeaderBlob header:
                await WriteAsync(
                    BlobKeys.For(_blobNamespace, BlobKeys.HeaderType, header.Version),
                    header,
                    ToState(header.Version),
                    cancellationToken).ConfigureAwait(false);
                break;

            default:
                throw new ArgumentException(
                    $"Cannot publish an artifact of type {publishArtifact.GetType().Name}.",
                    nameof(publishArtifact));
        }
    }

    public void Dispose() => _snapshotIndexLock.Dispose();

    private async Task WriteAsync(
        string key,
        IPublishArtifact artifact,
        Dictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        await using Stream content = artifact.OpenStream();

        await _store.WriteAsync(key, content, metadata, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the versions a snapshot already exists for, once.
    /// </summary>
    /// <remarks>
    /// Lazily rather than in the constructor, so that a producer starting up does not pay for a listing
    /// it may never use, and so that a misconfigured bucket fails on the first publish with the rest of
    /// the cycle's errors rather than at construction.
    /// </remarks>
    private async Task EnsureSnapshotIndexLoadedAsync(CancellationToken cancellationToken)
    {
        if (_snapshotIndex is not null)
        {
            return;
        }

        await _snapshotIndexLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _snapshotIndex ??=
                await ReadExistingSnapshotVersionsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _snapshotIndexLock.Release();
        }
    }

    /// <summary>
    /// Adds <paramref name="version"/> to the index of snapshots and writes the index back.
    /// </summary>
    /// <remarks>
    /// The index is written whether or not the version was new to it. Writing only on a change would
    /// mean a producer that restarts and re-publishes a version it already has leaves whatever index
    /// happens to be in the store — including none at all, which is the case a cold-starting consumer
    /// cannot recover from.
    /// </remarks>
    private async Task AddToSnapshotIndexAsync(long version, CancellationToken cancellationToken)
    {
        await _snapshotIndexLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _snapshotIndex ??=
                await ReadExistingSnapshotVersionsAsync(cancellationToken).ConfigureAwait(false);

            int position = _snapshotIndex.BinarySearch(version);

            if (position < 0)
            {
                _snapshotIndex.Insert(~position, version);
            }

            using MemoryStream encoded = new(SnapshotIndex.Encode(_snapshotIndex));

            await _store.WriteAsync(
                BlobKeys.SnapshotIndexFor(_blobNamespace),
                encoded,
                new Dictionary<string, string>(StringComparer.Ordinal),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _snapshotIndexLock.Release();
        }
    }

    private async Task<List<long>> ReadExistingSnapshotVersionsAsync(CancellationToken cancellationToken)
    {
        List<long> versions = [];

        // Listed from the keys rather than read from the index object, so that an index lost or never
        // written is rebuilt rather than starting again from this cycle. Java does the same.
        string prefix = BlobKeys.PrefixFor(_blobNamespace, BlobType.Snapshot.GetPrefix());

        await foreach (string key in _store.ListKeysAsync(prefix, cancellationToken).ConfigureAwait(false))
        {
            if (BlobKeys.TryParseVersion(key, out long version))
            {
                versions.Add(version);
            }
        }

        versions.Sort();

        return versions;
    }

    private static Dictionary<string, string> ToState(long toVersion) =>
        new(StringComparer.Ordinal)
        {
            [BlobKeys.ToStateMetadata] = toVersion.ToString(CultureInfo.InvariantCulture),
        };

    private static Dictionary<string, string> FromAndToState(long fromVersion, long toVersion) =>
        new(StringComparer.Ordinal)
        {
            [BlobKeys.FromStateMetadata] = fromVersion.ToString(CultureInfo.InvariantCulture),
            [BlobKeys.ToStateMetadata] = toVersion.ToString(CultureInfo.InvariantCulture),
        };
}
