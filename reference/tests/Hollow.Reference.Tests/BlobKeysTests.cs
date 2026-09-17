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
using Hollow.Reference.Infrastructure.Adapters;

namespace Hollow.Reference.Tests;

/// <summary>
/// Where a blob goes, which is the one thing the publisher and the retriever have to agree about.
/// </summary>
public sealed class BlobKeysTests
{
    [Fact]
    public void AKeyIsTheNamespaceThenTheTypeThenAHashAndTheVersion()
    {
        const long version = 20240101000000001;

        string expectedHash =
            ((uint)HashCodes.HashLong(version)).ToString("x", CultureInfo.InvariantCulture);

        Assert.Equal(
            $"catalogue/snapshot/{expectedHash}-{version.ToString(CultureInfo.InvariantCulture)}",
            BlobKeys.For("catalogue", BlobType.Snapshot, version));
    }

    [Fact]
    public void TheHashIsUnsignedHexJustAsJavaWritesIt()
    {
        // Java writes Integer.toHexString(HashCodes.hashLong(v)), which prints a negative hash as eight
        // unsigned hex digits. Formatting the signed int would give the same eight — but the cast is
        // what says so on purpose rather than by accident, and the key only has to be self-consistent.
        foreach (long version in (long[])[0, 1, long.MaxValue, 20240101000000001])
        {
            string key = BlobKeys.For("catalogue", BlobType.Snapshot, version);
            string hash = key.Split('/')[^1].Split('-')[0];

            Assert.Equal(
                ((uint)HashCodes.HashLong(version)).ToString("x", CultureInfo.InvariantCulture), hash);
        }
    }

    [Fact]
    public void EachKindOfBlobHasItsOwnPrefix()
    {
        Assert.StartsWith(
            "catalogue/snapshot/", BlobKeys.For("catalogue", BlobType.Snapshot, 1), StringComparison.Ordinal);
        Assert.StartsWith(
            "catalogue/delta/", BlobKeys.For("catalogue", BlobType.Delta, 1), StringComparison.Ordinal);
        Assert.StartsWith(
            "catalogue/reversedelta/",
            BlobKeys.For("catalogue", BlobType.ReverseDelta, 1),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheNamespaceIsWhatKeepsTwoDatasetsOutOfEachOthersWay()
    {
        Assert.NotEqual(
            BlobKeys.For("catalogue", BlobType.Snapshot, 1),
            BlobKeys.For("inventory", BlobType.Snapshot, 1));
    }

    [Fact]
    public void AKeyCarriesItsVersionBackOut()
    {
        const long version = 20240101000000001;

        Assert.True(
            BlobKeys.TryParseVersion(BlobKeys.For("catalogue", BlobType.Snapshot, version), out long parsed));
        Assert.Equal(version, parsed);
    }

    [Fact]
    public void SomethingElseInTheBucketIsNotMistakenForABlob()
    {
        // The publisher rebuilds the snapshot index by listing keys and reading the version off each.
        // Anything else under the prefix has to be passed over rather than parsed into a version.
        Assert.False(BlobKeys.TryParseVersion("catalogue/snapshot/README", out _));
        Assert.False(BlobKeys.TryParseVersion("catalogue/snapshot.index", out _));
    }

    [Fact]
    public void TheSnapshotIndexSitsBesideTheBlobsRatherThanAmongThem()
    {
        string index = BlobKeys.SnapshotIndexFor("catalogue");

        Assert.Equal("catalogue/snapshot.index", index);
        Assert.DoesNotContain(BlobKeys.PrefixFor("catalogue", "snapshot"), index, StringComparison.Ordinal);
    }
}
