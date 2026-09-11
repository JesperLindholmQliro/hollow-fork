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
using Hollow.Core;
using Hollow.Core.Util;

namespace Hollow.Api.Consumer.Fs;

/// <summary>
/// A blob store that is a directory of files.
/// </summary>
/// <remarks>
/// <para>
/// The layout is the one a filesystem-backed Hollow producer writes, and is shared with Java:
/// <c>snapshot-{version}</c>, <c>delta-{from}-{to}</c>, <c>reversedelta-{from}-{to}</c> and
/// <c>header-{version}</c>. A file whose trailing segment is not a number is ignored rather than
/// treated as corrupt, because producers write temporary and checksummed files alongside the blobs.
/// </para>
/// <para>
/// <strong>Port note.</strong> Java's version can also front a remote store, caching what it fetches
/// into the directory, and can carry optional blob parts. Neither is ported.
/// </para>
/// </remarks>
public sealed class HollowFilesystemBlobRetriever : IBlobRetriever
{
    private readonly string _blobStoreDirectory;

    /// <summary>
    /// Reads blobs from <paramref name="blobStoreDirectory"/>.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">The directory does not exist.</exception>
    public HollowFilesystemBlobRetriever(string blobStoreDirectory)
    {
        ArgumentNullException.ThrowIfNull(blobStoreDirectory);

        if (!Directory.Exists(blobStoreDirectory))
        {
            throw new DirectoryNotFoundException($"The blob store directory {blobStoreDirectory} does not exist.");
        }

        _blobStoreDirectory = blobStoreDirectory;
    }

    /// <inheritdoc />
    public Blob? RetrieveSnapshotBlob(long desiredVersion)
    {
        string exactPath = PathFor($"snapshot-{desiredVersion.Invariant()}");

        if (File.Exists(exactPath))
        {
            return new FilesystemBlob(exactPath, desiredVersion);
        }

        long nearest = HollowConstants.VersionNone;

        foreach ((long version, _) in EnumerateBlobs("snapshot-"))
        {
            if (version < desiredVersion && version > nearest)
            {
                nearest = version;
            }
        }

        return nearest == HollowConstants.VersionNone
            ? null
            : new FilesystemBlob(PathFor($"snapshot-{nearest.Invariant()}"), nearest);
    }

    /// <inheritdoc />
    public Blob? RetrieveDeltaBlob(long currentVersion) => RetrieveTransition("delta-", currentVersion);

    /// <inheritdoc />
    public Blob? RetrieveReverseDeltaBlob(long currentVersion) =>
        RetrieveTransition("reversedelta-", currentVersion);

    /// <inheritdoc />
    public HeaderBlob? RetrieveHeaderBlob(long currentVersion)
    {
        string path = PathFor($"header-{currentVersion.Invariant()}");

        return File.Exists(path) ? new FilesystemHeaderBlob(path, currentVersion) : null;
    }

    private Blob? RetrieveTransition(string prefix, long fromVersion)
    {
        // Match on the full "prefix-from-" so that, say, version 1 does not pick up version 12's
        // transitions. Java matches on the prefix alone, which it gets away with only because a real
        // version is a timestamp of a fixed width.
        string transitionPrefix = $"{prefix}{fromVersion.Invariant()}-";

        foreach ((long toVersion, string path) in EnumerateBlobs(transitionPrefix))
        {
            return new FilesystemBlob(path, fromVersion, toVersion);
        }

        return null;
    }

    /// <summary>
    /// The versions of the blobs in the store whose names begin with <paramref name="prefix"/>, taken
    /// from the last hyphen-separated segment of each name.
    /// </summary>
    private IEnumerable<(long Version, string Path)> EnumerateBlobs(string prefix)
    {
        foreach (string path in Directory.EnumerateFiles(_blobStoreDirectory, $"{prefix}*"))
        {
            string fileName = Path.GetFileName(path);
            string versionText = fileName[(fileName.LastIndexOf('-') + 1)..];

            if (long.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out long version))
            {
                yield return (version, path);
            }
        }
    }

    private string PathFor(string fileName) => Path.Combine(_blobStoreDirectory, fileName);

    private sealed class FilesystemBlob : Blob
    {
        private readonly string _path;

        internal FilesystemBlob(string path, long toVersion)
            : base(toVersion) => _path = path;

        internal FilesystemBlob(string path, long fromVersion, long toVersion)
            : base(fromVersion, toVersion) => _path = path;

        public override Stream OpenStream() => File.OpenRead(_path);
    }

    private sealed class FilesystemHeaderBlob(string path, long version) : HeaderBlob(version)
    {
        public override Stream OpenStream() => File.OpenRead(path);
    }
}
