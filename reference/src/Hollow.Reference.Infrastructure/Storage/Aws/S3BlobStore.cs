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

using System.Net;
using System.Runtime.CompilerServices;
using Amazon.S3;
using Amazon.S3.Model;

namespace Hollow.Reference.Infrastructure.Storage.Aws;

/// <summary>
/// The blob store the Java reference implementation uses: an S3 bucket.
/// </summary>
/// <remarks>
/// <para>
/// This is the half of <c>S3Publisher</c> and <c>S3BlobRetriever</c> that is genuinely about S3. The
/// key layout, the metadata names and the snapshot index moved up into
/// <see cref="Hollow.Reference.Infrastructure.Adapters"/>, where the other two modes share them; what
/// is left here is put, get, head and list.
/// </para>
/// <para>
/// A missing object is reported as <see langword="null"/> rather than as an exception, which is what
/// makes the retriever's "is there a delta yet?" an ordinary question. The SDK still throws, but it
/// throws here, once, where the meaning of a 404 is known.
/// </para>
/// </remarks>
public sealed class S3BlobStore : IHollowBlobStore
{
    private const string UserMetadataPrefix = "x-amz-meta-";

    private readonly IAmazonS3 _s3;
    private readonly string _bucketName;

    public S3BlobStore(IAmazonS3 s3, string bucketName)
    {
        ArgumentNullException.ThrowIfNull(s3);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketName);

        _s3 = s3;
        _bucketName = bucketName;
    }

    public async Task WriteAsync(
        string key,
        Stream content,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(metadata);

        PutObjectRequest request = new()
        {
            BucketName = _bucketName,
            Key = key,
            InputStream = content,

            // The stream belongs to the caller, which opened it and will close it. Left on, the SDK
            // would close a publisher's blob out from under it.
            AutoCloseStream = false,
        };

        foreach ((string name, string value) in metadata)
        {
            request.Metadata.Add(name, value);
        }

        await _s3.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        string path = DownloadedBlob.ReserveFileFor(key);

        try
        {
            using GetObjectResponse response = await _s3
                .GetObjectAsync(_bucketName, key, cancellationToken)
                .ConfigureAwait(false);

            await response
                .WriteResponseStreamToFileAsync(path, append: false, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
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

        GetObjectMetadataResponse response;

        try
        {
            response = await _s3
                .GetObjectMetadataAsync(_bucketName, key, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AmazonS3Exception e) when (e.StatusCode is HttpStatusCode.NotFound)
        {
            return null;
        }

        Dictionary<string, string> metadata = new(StringComparer.Ordinal);

        foreach (string name in response.Metadata.Keys)
        {
            // S3 hands user metadata back under the header name it stored it as, prefix and all. The
            // publisher wrote "to_state"; without stripping it, the retriever would look for a key
            // called "x-amz-meta-to_state" and never find it.
            string stripped = name.StartsWith(UserMetadataPrefix, StringComparison.OrdinalIgnoreCase)
                ? name[UserMetadataPrefix.Length..]
                : name;

            metadata[stripped] = response.Metadata[name];
        }

        return metadata;
    }

    public async IAsyncEnumerable<string> ListKeysAsync(
        string keyPrefix,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyPrefix);

        ListObjectsV2Request request = new() { BucketName = _bucketName, Prefix = keyPrefix };

        while (true)
        {
            ListObjectsV2Response response =
                await _s3.ListObjectsV2Async(request, cancellationToken).ConfigureAwait(false);

            foreach (S3Object summary in response.S3Objects ?? [])
            {
                yield return summary.Key;
            }

            if (response.IsTruncated is not true)
            {
                yield break;
            }

            request.ContinuationToken = response.NextContinuationToken;
        }
    }
}
