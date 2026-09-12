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
using Hollow.Core.Index.Key;
using Hollow.Core.Schema;

namespace Hollow.Explorer.Models;

/// <summary>
/// One row of the home page: what a type holds and what it costs.
/// </summary>
/// <param name="TypeName">The type.</param>
/// <param name="NumRecords">How many of its ordinals hold a record.</param>
/// <param name="NumHoles">How many ordinals below the highest populated one hold nothing.</param>
/// <param name="ApproxHoleFootprint">What those holes cost, in bytes.</param>
/// <param name="ApproxHeapFootprint">What the whole type costs, in bytes.</param>
/// <param name="PrimaryKey">The key its records are identified by, if it declares one.</param>
/// <param name="Schema">Its schema.</param>
/// <param name="NumShards">How many shards its records are split across.</param>
/// <remarks>
/// Java gives each figure two getters — one returning the number for sorting and one returning it
/// formatted for display — because a Velocity template can only read properties. A record with the
/// numbers as its members and the formatting as computed properties says the same thing once.
/// </remarks>
public sealed record TypeOverview(
    string TypeName,
    int NumRecords,
    int NumHoles,
    long ApproxHoleFootprint,
    long ApproxHeapFootprint,
    PrimaryKey? PrimaryKey,
    HollowSchema Schema,
    int NumShards)
{
    /// <summary><see cref="NumRecords"/> with thousands separated.</summary>
    public string NumRecordsDisplay => NumRecords.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary><see cref="NumHoles"/> with thousands separated.</summary>
    public string NumHolesDisplay => NumHoles.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary><see cref="ApproxHoleFootprint"/> in the largest unit it fills.</summary>
    public string ApproxHoleFootprintDisplay => ByteSize.Format(ApproxHoleFootprint);

    /// <summary><see cref="ApproxHeapFootprint"/> in the largest unit it fills.</summary>
    public string ApproxHeapFootprintDisplay => ByteSize.Format(ApproxHeapFootprint);

    /// <summary>The key as it is written in a schema, or nothing when the type declares none.</summary>
    public string PrimaryKeyDisplay => PrimaryKey?.ToString() ?? "";

    /// <summary>The schema as it is written in a schema file.</summary>
    public string SchemaDisplay => Schema.ToString() ?? "";

    /// <summary><see cref="NumShards"/> with thousands separated.</summary>
    public string NumShardsDisplay => NumShards.ToString("N0", CultureInfo.InvariantCulture);
}
