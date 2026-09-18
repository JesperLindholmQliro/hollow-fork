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

namespace Hollow.Reference.Infrastructure.Storage;

/// <summary>
/// Somewhere to keep blobs, keyed by name and carrying a little metadata.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam the three modes differ at. The Java reference implementation writes its publisher
/// and its blob retriever against S3 directly; here that same logic is written once against this
/// interface, and S3, Azure Blob Storage and a local directory each implement it. Everything Hollow
/// actually sees — the key layout, the metadata names, the snapshot index — is identical in all three.
/// </para>
/// <para>
/// Absence is reported by returning <see langword="null"/> rather than by throwing. The Java original
/// catches <c>AmazonS3Exception</c> to discover that a delta does not exist yet, which is the normal
/// case on every cycle rather than an error.
/// </para>
/// <para>
/// The interface is asynchronous because both cloud SDKs are. Hollow's own publisher and retriever
/// contracts are synchronous, so the adapters in
/// <see cref="Hollow.Reference.Infrastructure.Adapters"/> block — in one place, deliberately, and
/// documented there.
/// </para>
/// </remarks>
public interface IHollowBlobStore
{
    /// <summary>
    /// Where this store keeps things, in a form somebody can go and look at: a directory path, a
    /// bucket, a container URL.
    /// </summary>
    /// <remarks>
    /// Only ever printed. It is here because the first question anyone running the reference
    /// implementation asks is where the blobs went, and the answer in the local mode is a temporary
    /// directory nobody chose.
    /// </remarks>
    string Description { get; }

    /// <summary>Writes <paramref name="content"/> to <paramref name="key"/>, replacing what is there.</summary>
    /// <remarks>
    /// <paramref name="content"/> is read to its end and left open; the caller owns it.
    /// </remarks>
    Task WriteAsync(
        string key,
        Stream content,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens <paramref name="key"/> for reading, or returns <see langword="null"/> if it is not there.
    /// </summary>
    /// <remarks>
    /// The stream is seekable and is the caller's to dispose. Implementations backed by a remote store
    /// fetch the whole object to a temporary file first, which is what the Java original does too:
    /// Hollow reads a blob more than once, and a network stream cannot be rewound.
    /// </remarks>
    Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the metadata of <paramref name="key"/> without its content, or returns
    /// <see langword="null"/> if it is not there.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>?> ReadMetadataAsync(
        string key, CancellationToken cancellationToken = default);

    /// <summary>Lists every key beginning with <paramref name="keyPrefix"/>, in no particular order.</summary>
    IAsyncEnumerable<string> ListKeysAsync(string keyPrefix, CancellationToken cancellationToken = default);
}
