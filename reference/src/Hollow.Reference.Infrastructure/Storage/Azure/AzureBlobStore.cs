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

using System.Runtime.CompilerServices;
using global::Azure;
using global::Azure.Storage.Blobs;
using global::Azure.Storage.Blobs.Models;

namespace Hollow.Reference.Infrastructure.Storage.Azure;

/// <summary>
/// The Azure counterpart of <see cref="Aws.S3BlobStore"/>: a Blob Storage container.
/// </summary>
/// <remarks>
/// <para>
/// Block blobs in a standard general-purpose v2 account, which is the cheapest thing in Azure that
/// stores bytes and the direct equivalent of what the Java reference implementation asks of S3. A key
/// with slashes in it is a flat name here rather than a folder, exactly as in S3, so the layout
/// <see cref="Adapters.BlobKeys"/> produces needs no translating.
/// </para>
/// <para>
/// The container is created on first write if it is not there. That is a convenience for a reference
/// implementation and not what a deployed service should do — it wants the container provisioned with
/// the rest of its infrastructure, and a credential that cannot create one.
/// </para>
/// </remarks>
public sealed class AzureBlobStore : IHollowBlobStore, IDisposable
{
    private readonly BlobContainerClient _container;
    private readonly SemaphoreSlim _containerLock = new(1, 1);

    private bool _containerChecked;

    public AzureBlobStore(BlobContainerClient container)
    {
        ArgumentNullException.ThrowIfNull(container);

        _container = container;
    }

    /// <inheritdoc />
    public string Description => _container.Uri.ToString();

    public async Task WriteAsync(
        string key,
        Stream content,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(metadata);

        await EnsureContainerAsync(cancellationToken).ConfigureAwait(false);

        BlobUploadOptions options = new()
        {
            Metadata = metadata.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
        };

        await _container
            .GetBlobClient(key)
            .UploadAsync(content, options, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        string path = DownloadedBlob.ReserveFileFor(key);

        try
        {
            await using (FileStream target = File.Create(path))
            {
                await _container
                    .GetBlobClient(key)
                    .DownloadToAsync(target, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            DownloadedBlob.Discard(path);
            return null;
        }
        catch
        {
            DownloadedBlob.Discard(path);
            throw;
        }

        return DownloadedBlob.OpenAndDeleteOnClose(path);
    }

    public async Task<IReadOnlyDictionary<string, string>?> ReadMetadataAsync(
        string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        try
        {
            BlobProperties properties = await _container
                .GetBlobClient(key)
                .GetPropertiesAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return new Dictionary<string, string>(properties.Metadata, StringComparer.Ordinal);
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            return null;
        }
    }

    public async IAsyncEnumerable<string> ListKeysAsync(
        string keyPrefix,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyPrefix);

        IAsyncEnumerable<BlobItem> blobs;

        try
        {
            // No traits and no extra states: listing is only ever used to rebuild the snapshot index
            // from the keys, and fetching each blob's metadata to do that would be a request apiece.
            blobs = _container.GetBlobsAsync(
                BlobTraits.None, BlobStates.None, keyPrefix, cancellationToken);
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            yield break;
        }

        await foreach (BlobItem blob in blobs.ConfigureAwait(false))
        {
            yield return blob.Name;
        }
    }

    public void Dispose() => _containerLock.Dispose();

    private async Task EnsureContainerAsync(CancellationToken cancellationToken)
    {
        if (_containerChecked)
        {
            return;
        }

        await _containerLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_containerChecked)
            {
                return;
            }

            await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            _containerChecked = true;
        }
        finally
        {
            _containerLock.Release();
        }
    }
}
