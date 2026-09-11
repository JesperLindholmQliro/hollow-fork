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

using Hollow.Core.Schema;

namespace Hollow.Core;

/// <summary>
/// The header of a Hollow blob: its format version, the randomized tags that bind a delta to the state
/// it applies to, the schemas it contains, and the producer's header tags.
/// </summary>
public sealed class HollowBlobHeader
{
    /// <summary>
    /// The blob format version. A change here signals backwards incompatibility.
    /// </summary>
    public const int HollowBlobVersionHeader = 1030;

    /// <summary>The producer's header tags, typically recording input source versions.</summary>
    public Dictionary<string, string> HeaderTags { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The schemas contained in this blob.</summary>
    public IReadOnlyList<HollowSchema> Schemas { get; set; } = [];

    /// <summary>
    /// The random tag of the state a delta applies to. Compared against the current state's tag so
    /// that a delta cannot be applied to the wrong state.
    /// </summary>
    public long OriginRandomizedTag { get; set; }

    /// <summary>The random tag of the state this blob produces.</summary>
    public long DestinationRandomizedTag { get; set; }

    /// <summary>The format version this blob was written with.</summary>
    public int BlobFormatVersion { get; set; } = HollowBlobVersionHeader;

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        obj is HollowBlobHeader other
        && BlobFormatVersion == other.BlobFormatVersion
        && OriginRandomizedTag == other.OriginRandomizedTag
        && DestinationRandomizedTag == other.DestinationRandomizedTag
        && HeaderTags.Count == other.HeaderTags.Count
        && !HeaderTags.Except(other.HeaderTags).Any();

    /// <inheritdoc />
    public override int GetHashCode() =>
        System.HashCode.Combine(BlobFormatVersion, OriginRandomizedTag, DestinationRandomizedTag);
}
