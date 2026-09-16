/*
 *  Copyright 2021 Netflix, Inc.
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
/// What sits at the front of an optional blob part: its name, the two randomized tags that tie it to
/// a main blob, and the schemas of the types it carries.
/// </summary>
/// <remarks>
/// A part is useless on its own and dangerous against the wrong main blob, which is why it repeats the
/// tags rather than trusting the file name. A reader that finds them disagreeing refuses the part.
/// </remarks>
public sealed class HollowBlobOptionalPartHeader
{
    /// <summary>
    /// The four bytes an optional part begins with, distinct from a main blob's own version.
    /// </summary>
    public const int HollowBlobPartVersionHeader = 1031;

    /// <summary>Names the part whose header this is.</summary>
    public HollowBlobOptionalPartHeader(string partName)
    {
        ArgumentException.ThrowIfNullOrEmpty(partName);

        PartName = partName;
    }

    /// <summary>The part's name, which the consumer asked for by name.</summary>
    public string PartName { get; }

    /// <summary>The schemas of the types this part carries, which the main blob does not.</summary>
    public IReadOnlyList<HollowSchema> Schemas { get; set; } = [];

    /// <summary>The randomized tag of the state this part applies to.</summary>
    public long OriginRandomizedTag { get; set; }

    /// <summary>The randomized tag of the state this part produces.</summary>
    public long DestinationRandomizedTag { get; set; }
}
