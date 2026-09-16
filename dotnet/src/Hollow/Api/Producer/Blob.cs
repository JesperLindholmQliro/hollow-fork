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

using Hollow.Api.Consumer;
using Hollow.Core;
using Hollow.Core.Util;
using Hollow.Core.Write;

namespace Hollow.Api.Producer;

/// <summary>
/// Something a producer writes during a cycle and then hands to a <see cref="IPublisher"/>.
/// </summary>
/// <remarks>
/// A producer stages an artifact — usually to a temporary file — before publishing it, so that a blob
/// store never sees a half-written one. <see cref="Cleanup"/> discards the staged copy once the cycle
/// is over, whether or not it was published.
/// </remarks>
public interface IPublishArtifact : IDisposable
{
    /// <summary>
    /// Writes this artifact's bytes using <paramref name="blobWriter"/>.
    /// </summary>
    void Write(HollowBlobWriter blobWriter);

    /// <summary>
    /// Opens a stream over the staged bytes, for a publisher to copy to wherever it keeps blobs.
    /// </summary>
    Stream OpenStream();

    /// <summary>
    /// Discards the staged copy.
    /// </summary>
    /// <remarks>
    /// Called once per artifact at the end of a cycle. <see cref="IDisposable.Dispose"/> forwards here,
    /// so a <c>using</c> works too.
    /// </remarks>
    void Cleanup();

    /// <summary>
    /// The path of the staged copy, or <see langword="null"/> when it is not a file.
    /// </summary>
    string? Path => null;

    /// <inheritdoc />
    void IDisposable.Dispose() => Cleanup();
}

/// <summary>
/// The header of a state, carrying its schemas without its records.
/// </summary>
public abstract class HeaderBlob : IPublishArtifact
{
    /// <summary>
    /// Initialises a header blob for <paramref name="version"/>.
    /// </summary>
    protected HeaderBlob(long version) => Version = version;

    /// <summary>The version this header describes.</summary>
    public long Version { get; }

    /// <inheritdoc />
    public abstract void Write(HollowBlobWriter blobWriter);

    /// <inheritdoc />
    public abstract Stream OpenStream();

    /// <inheritdoc />
    public abstract void Cleanup();

    /// <inheritdoc />
    public virtual string? Path => null;
}

/// <summary>
/// A snapshot, delta or reverse delta a producer writes for one cycle.
/// </summary>
/// <remarks>
/// <strong>Port note.</strong> Java declares its own <c>HollowProducer.Blob.Type</c> alongside the
/// consumer's identical <c>HollowConsumer.Blob.BlobType</c>. This port has one
/// <see cref="Consumer.BlobType"/> that both sides use; the producer already depends on the consumer
/// namespace for restore, so nothing is gained by having two.
/// </remarks>
public abstract class Blob : IPublishArtifact
{
    /// <summary>
    /// Initialises a blob of <paramref name="blobType"/> covering the given transition.
    /// </summary>
    /// <param name="fromVersion">
    /// The version the blob applies to, or <see cref="HollowConstants.VersionNone"/> for a snapshot.
    /// </param>
    /// <param name="toVersion">The version the blob produces.</param>
    /// <param name="blobType">What the blob does to a consumer's state.</param>
    protected Blob(long fromVersion, long toVersion, BlobType blobType)
    {
        FromVersion = fromVersion;
        ToVersion = toVersion;
        BlobType = blobType;
    }

    /// <summary>
    /// The version this blob applies to, or <see cref="HollowConstants.VersionNone"/> for a snapshot.
    /// </summary>
    public long FromVersion { get; }

    /// <summary>The version a consumer arrives at once this blob is applied.</summary>
    public long ToVersion { get; }

    /// <summary>What this blob does to a consumer's state.</summary>
    public BlobType BlobType { get; }

    /// <summary>
    /// The optional parts written alongside this blob, by name, or nothing where it carries
    /// everything.
    /// </summary>
    public virtual IReadOnlyCollection<string> OptionalPartNames => [];

    /// <summary>Opens a stream over one of this blob's optional parts.</summary>
    /// <exception cref="ArgumentException">This blob has no such part.</exception>
    public virtual Stream OpenOptionalPartStream(string partName) =>
        throw new ArgumentException($"this blob has no optional part named '{partName}'", nameof(partName));

    /// <inheritdoc />
    public abstract void Write(HollowBlobWriter blobWriter);

    /// <inheritdoc />
    public abstract Stream OpenStream();

    /// <inheritdoc />
    public abstract void Cleanup();

    /// <inheritdoc />
    public virtual string? Path => null;

    /// <inheritdoc />
    public override string ToString() =>
        BlobType == BlobType.Snapshot
            ? $"{BlobType.GetPrefix()} to {ToVersion.Invariant()}"
            : $"{BlobType.GetPrefix()} from {FromVersion.Invariant()} to {ToVersion.Invariant()}";
}
