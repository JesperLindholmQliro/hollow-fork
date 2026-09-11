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

namespace Hollow.Api.Producer.Fs;

/// <summary>
/// No compression, which is what a producer uses unless it is given something else.
/// </summary>
public sealed class NoBlobCompressor : IBlobCompressor
{
    /// <summary>The single instance; the type carries no state.</summary>
    public static IBlobCompressor Instance { get; } = new NoBlobCompressor();

    private NoBlobCompressor()
    {
    }

    /// <inheritdoc />
    public Stream Compress(Stream stream) => stream;

    /// <inheritdoc />
    public Stream Decompress(Stream stream) => stream;
}

/// <summary>
/// Stages a cycle's blobs as files in a directory.
/// </summary>
/// <remarks>
/// This is what a producer of any size should use: a snapshot of a large dataset is large, and holding
/// one in memory alongside the write state and the read state doubles the producer's footprint at the
/// worst moment. The staged files are deleted at the end of the cycle.
/// </remarks>
public sealed class HollowFilesystemBlobStager : IBlobStager
{
    private readonly string _stagingDirectory;
    private readonly IBlobCompressor _compressor;

    /// <summary>
    /// Stages blobs in <paramref name="stagingDirectory"/>, creating it if it does not exist.
    /// </summary>
    /// <param name="stagingDirectory">Where to write the staged blobs.</param>
    /// <param name="compressor">
    /// Wraps the staged bytes, or <see langword="null"/> for no compression. A publisher that reads a
    /// staged blob gets the compressed bytes, so whatever consumers read has to match.
    /// </param>
    public HollowFilesystemBlobStager(string stagingDirectory, IBlobCompressor? compressor = null)
    {
        ArgumentNullException.ThrowIfNull(stagingDirectory);

        Directory.CreateDirectory(stagingDirectory);

        _stagingDirectory = stagingDirectory;
        _compressor = compressor ?? NoBlobCompressor.Instance;
    }

    /// <inheritdoc />
    public Blob OpenSnapshot(long version) =>
        new FilesystemBlob(
            StagingPath("snapshot", HollowConstants.VersionNone, version),
            HollowConstants.VersionNone,
            version,
            BlobType.Snapshot,
            _compressor);

    /// <inheritdoc />
    public HeaderBlob OpenHeader(long version) =>
        new FilesystemHeaderBlob(
            StagingPath("header", HollowConstants.VersionNone, version), version, _compressor);

    /// <inheritdoc />
    public Blob OpenDelta(long fromVersion, long toVersion) =>
        new FilesystemBlob(
            StagingPath("delta", fromVersion, toVersion), fromVersion, toVersion, BlobType.Delta, _compressor);

    /// <inheritdoc />
    public Blob OpenReverseDelta(long fromVersion, long toVersion) =>
        new FilesystemBlob(
            StagingPath("reversedelta", fromVersion, toVersion),
            fromVersion,
            toVersion,
            BlobType.ReverseDelta,
            _compressor);

    /// <summary>
    /// Where a blob is staged. The name carries a random suffix so that two producers sharing a staging
    /// directory, or a retried cycle, cannot collide on a half-written file.
    /// </summary>
    private string StagingPath(string prefix, long fromVersion, long toVersion)
    {
        string versions = fromVersion == HollowConstants.VersionNone
            ? toVersion.Invariant()
            : $"{fromVersion.Invariant()}-{toVersion.Invariant()}";

        return Path.Combine(
            _stagingDirectory, $"{prefix}-{versions}-{Random.Shared.Next(int.MaxValue).Invariant()}");
    }

    private sealed class FilesystemBlob(
        string path, long fromVersion, long toVersion, BlobType blobType, IBlobCompressor compressor)
        : Blob(fromVersion, toVersion, blobType)
    {
        public override string Path => path;

        public override void Write(HollowBlobWriter blobWriter)
        {
            ArgumentNullException.ThrowIfNull(blobWriter);

            using FileStream file = File.Create(path);
            using Stream stream = compressor.Compress(file);

            switch (BlobType)
            {
                case BlobType.Snapshot:
                    blobWriter.WriteSnapshot(stream);
                    break;

                case BlobType.Delta:
                    blobWriter.WriteDelta(stream);
                    break;

                case BlobType.ReverseDelta:
                    blobWriter.WriteReverseDelta(stream);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(blobWriter), BlobType, "unknown blob type");
            }
        }

        public override Stream OpenStream() => compressor.Decompress(File.OpenRead(path));

        public override void Cleanup() => File.Delete(path);
    }

    private sealed class FilesystemHeaderBlob(string path, long version, IBlobCompressor compressor)
        : HeaderBlob(version)
    {
        public override string Path => path;

        public override void Write(HollowBlobWriter blobWriter)
        {
            ArgumentNullException.ThrowIfNull(blobWriter);

            using FileStream file = File.Create(path);
            using Stream stream = compressor.Compress(file);

            blobWriter.WriteHeader(stream);
        }

        public override Stream OpenStream() => compressor.Decompress(File.OpenRead(path));

        public override void Cleanup() => File.Delete(path);
    }
}

/// <summary>
/// Stages a cycle's blobs in memory.
/// </summary>
/// <remarks>
/// Convenient for a test or a small dataset. A production producer should stage to disk instead — see
/// <see cref="HollowFilesystemBlobStager"/> for why.
/// </remarks>
public sealed class HollowInMemoryBlobStager : IBlobStager
{
    /// <inheritdoc />
    public Blob OpenSnapshot(long version) =>
        new InMemoryBlob(HollowConstants.VersionNone, version, BlobType.Snapshot);

    /// <inheritdoc />
    public HeaderBlob OpenHeader(long version) => new InMemoryHeaderBlob(version);

    /// <inheritdoc />
    public Blob OpenDelta(long fromVersion, long toVersion) =>
        new InMemoryBlob(fromVersion, toVersion, BlobType.Delta);

    /// <inheritdoc />
    public Blob OpenReverseDelta(long fromVersion, long toVersion) =>
        new InMemoryBlob(fromVersion, toVersion, BlobType.ReverseDelta);

    private sealed class InMemoryBlob(long fromVersion, long toVersion, BlobType blobType)
        : Blob(fromVersion, toVersion, blobType)
    {
        private byte[] _bytes = [];

        public override void Write(HollowBlobWriter blobWriter)
        {
            ArgumentNullException.ThrowIfNull(blobWriter);

            using MemoryStream stream = new();

            switch (BlobType)
            {
                case BlobType.Snapshot:
                    blobWriter.WriteSnapshot(stream);
                    break;

                case BlobType.Delta:
                    blobWriter.WriteDelta(stream);
                    break;

                case BlobType.ReverseDelta:
                    blobWriter.WriteReverseDelta(stream);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(blobWriter), BlobType, "unknown blob type");
            }

            _bytes = stream.ToArray();
        }

        public override Stream OpenStream() => new MemoryStream(_bytes, writable: false);

        public override void Cleanup() => _bytes = [];
    }

    private sealed class InMemoryHeaderBlob(long version) : HeaderBlob(version)
    {
        private byte[] _bytes = [];

        public override void Write(HollowBlobWriter blobWriter)
        {
            ArgumentNullException.ThrowIfNull(blobWriter);

            using MemoryStream stream = new();
            blobWriter.WriteHeader(stream);

            _bytes = stream.ToArray();
        }

        public override Stream OpenStream() => new MemoryStream(_bytes, writable: false);

        public override void Cleanup() => _bytes = [];
    }
}
