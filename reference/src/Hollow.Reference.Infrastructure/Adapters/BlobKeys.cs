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
using Hollow.Core.Memory.Encoding;

namespace Hollow.Reference.Infrastructure.Adapters;

/// <summary>
/// Where a blob goes, and what its metadata is called.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>S3Publisher.getS3ObjectName</c> and the constants scattered through its retriever.
/// The layout is <c>{namespace}/{type}/{hash}-{version}</c>, and the hash is there on purpose: keys
/// that all begin with the same prefix and differ only in a trailing number land on one partition of
/// the store, and a leading hash spreads them. S3 no longer needs the help, but Azure Blob Storage
/// still partitions by key prefix, so the trick earns its keep in two of the three modes.
/// </para>
/// <para>
/// A version is looked up by the transition it starts from, not the one it ends at: a delta is keyed on
/// its <em>from</em> version, because a consumer holding version N asks for "the delta out of N"
/// without knowing what it leads to.
/// </para>
/// </remarks>
public static class BlobKeys
{
    /// <summary>The metadata name a blob's starting version is written under.</summary>
    public const string FromStateMetadata = "from_state";

    /// <summary>The metadata name a blob's resulting version is written under.</summary>
    public const string ToStateMetadata = "to_state";

    /// <summary>The key type a header blob is filed under. The Java original has no equivalent.</summary>
    public const string HeaderType = "header";

    /// <summary>The key of the blob of <paramref name="blobType"/> looked up by <paramref name="lookupVersion"/>.</summary>
    public static string For(string blobNamespace, BlobType blobType, long lookupVersion) =>
        For(blobNamespace, blobType.GetPrefix(), lookupVersion);

    /// <summary>The key of the <paramref name="fileType"/> blob looked up by <paramref name="lookupVersion"/>.</summary>
    public static string For(string blobNamespace, string fileType, long lookupVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobNamespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileType);

        // Java writes Integer.toHexString(HashCodes.hashLong(v)), which is unsigned hex without leading
        // zeroes. Casting through uint is what reproduces that for a negative hash; ToString("x") on the
        // int would print a sign-extended value instead.
        string hash = ((uint)HashCodes.HashLong(lookupVersion)).ToString("x", CultureInfo.InvariantCulture);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{PrefixFor(blobNamespace, fileType)}{hash}-{lookupVersion}");
    }

    /// <summary>The prefix every <paramref name="fileType"/> key begins with, for listing them.</summary>
    public static string PrefixFor(string blobNamespace, string fileType) =>
        $"{blobNamespace}/{fileType}/";

    /// <summary>The key the index of available snapshots is kept at.</summary>
    public static string SnapshotIndexFor(string blobNamespace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobNamespace);

        return $"{blobNamespace}/snapshot.index";
    }

    /// <summary>
    /// Reads the version back out of a key, or returns <see langword="false"/> if it does not end in one.
    /// </summary>
    public static bool TryParseVersion(string key, out long version)
    {
        ArgumentNullException.ThrowIfNull(key);

        int separator = key.LastIndexOf('-');

        if (separator < 0)
        {
            version = 0;
            return false;
        }

        return long.TryParse(
            key.AsSpan(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out version);
    }
}
