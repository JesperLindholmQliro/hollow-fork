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

using Hollow.Core.Memory.Encoding;
using Hollow.Core.Util;

namespace Hollow.Core.Schema;

/// <summary>
/// A short identifier for a whole data model, so that two of them can be compared without shipping the
/// schemas around.
/// </summary>
/// <remarks>
/// A producer puts this in its announcement metadata; a consumer compares it against its own to notice
/// that the data model has changed and that a snapshot is needed, since deltas carry records but not
/// schemas.
/// </remarks>
public sealed class HollowSchemaHash : IEquatable<HollowSchemaHash>
{
    /// <summary>
    /// Computes the hash of <paramref name="dataset"/>'s data model.
    /// </summary>
    public HollowSchemaHash(IHollowDataset dataset)
        : this(Argument(dataset).Schemas)
    {
    }

    /// <summary>
    /// Computes the hash of <paramref name="schemas"/>.
    /// </summary>
    /// <remarks>
    /// The schemas are sorted by name before hashing, so the order they were declared in does not
    /// change the result.
    /// </remarks>
    public HollowSchemaHash(IEnumerable<HollowSchema> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);

        SortedDictionary<string, HollowSchema> byName = new(StringComparer.Ordinal);
        foreach (HollowSchema schema in schemas)
        {
            byName[schema.Name] = schema;
        }

        string text = string.Concat(byName.Values.Select(schema => schema.ToString()));

        Hash = HashCodes.Compute(text).Invariant();
    }

    /// <summary>The hash, as the text a producer puts in its announcement metadata.</summary>
    public string Hash { get; }

    /// <inheritdoc />
    public bool Equals(HollowSchemaHash? other) =>
        other is not null && string.Equals(Hash, other.Hash, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as HollowSchemaHash);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Hash);

    /// <inheritdoc />
    public override string ToString() => Hash;

    private static IHollowDataset Argument(IHollowDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        return dataset;
    }
}
