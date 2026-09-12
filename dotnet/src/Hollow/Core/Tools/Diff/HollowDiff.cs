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

using Hollow.Core.Index.Key;
using Hollow.Core.Read.Engine;
using Hollow.Core.Schema;
using Hollow.Core.Tools.Diff.Exact;

namespace Hollow.Core.Tools.Diff;

/// <summary>
/// A detailed accounting of what changed between two states.
/// </summary>
/// <remarks>
/// <para>
/// Records of each type are paired by primary key; the pairs that are not identical are walked in
/// tandem and the difference attributed to the fields it is spread across. Records that pair with
/// nothing are reported as such, since for a diff it is enough to know that they arrived or left.
/// </para>
/// <para>
/// Build one, configure which types to look at, then call <see cref="CalculateDiffs"/>.
/// </para>
/// </remarks>
public sealed class HollowDiff
{
    /// <summary>
    /// The field types a single-field record can be identified by, where the type declares no key.
    /// </summary>
    /// <remarks>
    /// A type holding one value has an obvious key whether or not it declares one, which is what lets
    /// the shared <c>String</c> and <c>Integer</c> types be diffed rather than skipped.
    /// </remarks>
    private static readonly FieldType[] SingleFieldSupportedTypes =
        [FieldType.Int, FieldType.Long, FieldType.Double, FieldType.String, FieldType.Float, FieldType.Boolean];

    private readonly Dictionary<string, HollowTypeDiff> _typeDiffs = new(StringComparer.Ordinal);

    /// <summary>
    /// Prepares a diff between two states.
    /// </summary>
    /// <param name="from">The earlier state.</param>
    /// <param name="to">The later one.</param>
    /// <param name="autoDiscoverTypeDiffs">
    /// Whether to look at every object type declaring a primary key, which is the usual thing to want.
    /// </param>
    /// <param name="includeNonPrimaryKeyTypes">
    /// Whether to also look at object types declaring no key. Their records cannot be paired, so what
    /// comes out is counts rather than changes.
    /// </param>
    public HollowDiff(
        HollowReadStateEngine from,
        HollowReadStateEngine to,
        bool autoDiscoverTypeDiffs = true,
        bool includeNonPrimaryKeyTypes = false)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        FromStateEngine = from;
        ToStateEngine = to;
        EqualityMapping = new DiffEqualityMapping(from, to);

        if (!autoDiscoverTypeDiffs)
        {
            return;
        }

        foreach (HollowSchema schema in from.Schemas.Concat(to.Schemas))
        {
            if (schema is not HollowObjectSchema objectSchema || _typeDiffs.ContainsKey(schema.Name))
            {
                continue;
            }

            PrimaryKey? primaryKey = objectSchema.PrimaryKey;

            if (primaryKey is null && !includeNonPrimaryKeyTypes)
            {
                continue;
            }

            if (primaryKey is null
                && objectSchema.FieldCount == 1
                && SingleFieldSupportedTypes.Contains(objectSchema.GetFieldType(0)))
            {
                primaryKey = new PrimaryKey(schema.Name, objectSchema.GetFieldName(0));
            }

            AddTypeDiff(schema.Name, primaryKey is null ? null : [.. primaryKey.FieldPaths]);
        }
    }

    /// <summary>The earlier state.</summary>
    public HollowReadStateEngine FromStateEngine { get; }

    /// <summary>The later state.</summary>
    public HollowReadStateEngine ToStateEngine { get; }

    /// <summary>Which records are exactly equal across the two states.</summary>
    public DiffEqualityMapping EqualityMapping { get; }

    /// <summary>The types being diffed, in the order they were added.</summary>
    public IReadOnlyList<HollowTypeDiff> TypeDiffs => [.. _typeDiffs.Values];

    /// <summary>The report for one type, or <see langword="null"/> if it is not being diffed.</summary>
    public HollowTypeDiff? GetTypeDiff(string type) => _typeDiffs.GetValueOrDefault(type);

    /// <summary>
    /// Adds a type to the diff, paired by <paramref name="primaryKeyPaths"/>.
    /// </summary>
    /// <remarks>
    /// A type neither state has is not added, since there would be nothing to report.
    /// </remarks>
    public HollowTypeDiff AddTypeDiff(string type, params string[]? primaryKeyPaths)
    {
        HollowTypeDiff typeDiff = new(this, type, primaryKeyPaths);

        if (typeDiff.HasAnyData)
        {
            _typeDiffs[type] = typeDiff;
        }

        return typeDiff;
    }

    /// <summary>Runs the diff.</summary>
    public void CalculateDiffs()
    {
        // Both halves of this are prerequisites of the walk: which records pair by key, and which are
        // identical and can therefore be skipped.
        foreach (HollowTypeDiff typeDiff in _typeDiffs.Values)
        {
            EqualityMapping.GetEqualOrdinalMap(typeDiff.TypeName);
        }

        foreach (HollowTypeDiff typeDiff in _typeDiffs.Values)
        {
            typeDiff.CalculateMatches();
        }

        // From here on, a type nobody asked about answers empty rather than starting a long calculation
        // in the middle of a traversal.
        EqualityMapping.MarkPrepared();

        foreach (HollowTypeDiff typeDiff in _typeDiffs.Values)
        {
            typeDiff.CalculateDiffs();
        }
    }
}
