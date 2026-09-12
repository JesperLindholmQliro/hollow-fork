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
using Hollow.Core.Schema;

namespace Hollow.Explorer.Diff.Effigy.Pairer;

/// <summary>
/// Lines two effigies' fields up against each other, so a page can show them side by side.
/// </summary>
/// <remarks>
/// Which field goes opposite which is obvious for an object — same name — and is the whole problem for
/// a collection, where the two sides have no names and may not even be the same length. The subclasses
/// are the two answers to that.
/// </remarks>
/// <param name="from">The record from the earlier state, or nothing if it has none.</param>
/// <param name="to">The record from the later state.</param>
public abstract class HollowEffigyFieldPairer(HollowEffigy? from, HollowEffigy? to)
{
    /// <summary>The record from the earlier state.</summary>
    protected HollowEffigy? From { get; } = from;

    /// <summary>The record from the later state.</summary>
    protected HollowEffigy? To { get; } = to;

    /// <summary>The field pairings, in the order the page should show them.</summary>
    public abstract IReadOnlyList<EffigyFieldPair> Pair();

    /// <summary>
    /// Pairs <paramref name="from"/> against <paramref name="to"/>, picking how by what they are.
    /// </summary>
    /// <param name="from">The record from the earlier state.</param>
    /// <param name="to">The record from the later state.</param>
    /// <param name="matchHints">
    /// Per element type, the key that says which element of one collection is which element of the
    /// other. Without one a collection is paired by guessing, which is expensive and sometimes wrong.
    /// </param>
    public static IReadOnlyList<EffigyFieldPair> Pair(
        HollowEffigy? from, HollowEffigy? to, IReadOnlyDictionary<string, PrimaryKey> matchHints)
    {
        ArgumentNullException.ThrowIfNull(matchHints);

        // One side missing: every field of the other is an arrival or a departure.
        if (from is null || to is null)
        {
            return new HollowEffigyNullPartnerPairer(from, to).Pair();
        }

        // A node with no record behind it — a map entry — has named fields, so it pairs like an object.
        if (from.DataAccess is null)
        {
            return new HollowEffigyObjectPairer(from, to).Pair();
        }

        return from.DataAccess.Schema switch
        {
            HollowObjectSchema => new HollowEffigyObjectPairer(from, to).Pair(),

            HollowMapSchema mapSchema => new HollowEffigyMapPairer(
                from, to, matchHints.GetValueOrDefault(mapSchema.KeyType)).Pair(),

            HollowCollectionSchema collectionSchema => new HollowEffigyCollectionPairer(
                from, to, matchHints.GetValueOrDefault(collectionSchema.ElementType)).Pair(),

            { } schema => throw new ArgumentException(
                $"{schema.Name} is a {schema.GetType().Name}, whose fields this does not know how to pair",
                nameof(from)),
        };
    }
}

/// <summary>
/// One row of a side-by-side view: a field from each side, either of which may be missing.
/// </summary>
/// <param name="From">The field on the earlier side, or nothing where it only arrived.</param>
/// <param name="To">The field on the later side, or nothing where it only left.</param>
/// <param name="FromIndex">Where it sat on the earlier side, or -1 where position means nothing.</param>
/// <param name="ToIndex">Where it sits on the later side.</param>
public sealed record EffigyFieldPair(
    HollowEffigyField? From, HollowEffigyField? To, int FromIndex, int ToIndex)
{
    /// <summary>
    /// Whether this row is a difference.
    /// </summary>
    /// <remarks>
    /// Only leaves are judged here. A row that leads somewhere is never itself a difference — whatever
    /// differs below it will be a row of its own — so marking it too would count the same change at
    /// every level on the way down.
    /// </remarks>
    public bool IsDiff { get; } = CalculateIsDiff(From, To);

    /// <summary>Whether the row is a value rather than something to open.</summary>
    public bool IsLeafNode =>
        From?.Value is not null ? From.IsLeafNode : To?.IsLeafNode ?? true;

    /// <summary>
    /// Whether the two sides hold the same thing in different places.
    /// </summary>
    /// <remarks>
    /// Worth showing separately from a difference: a collection whose elements were reordered has not
    /// changed in any way a reader usually cares about, and saying so is more useful than a screen of
    /// rows that all look different.
    /// </remarks>
    public bool IsOrderingDiff => FromIndex != ToIndex;

    private static bool CalculateIsDiff(HollowEffigyField? from, HollowEffigyField? to)
    {
        if (from is null || to is null)
        {
            return !ReferenceEquals(from, to);
        }

        if (from.Value is null || to.Value is null)
        {
            return !ReferenceEquals(from.Value, to.Value);
        }

        return from.IsLeafNode && !from.Value.Equals(to.Value);
    }
}

/// <summary>
/// Pairs two objects' fields by name, which is what makes an object an object.
/// </summary>
public sealed class HollowEffigyObjectPairer(HollowEffigy from, HollowEffigy to)
    : HollowEffigyFieldPairer(from, to)
{
    /// <inheritdoc />
    public override IReadOnlyList<EffigyFieldPair> Pair()
    {
        List<EffigyFieldPair> pairs = [];

        foreach (HollowEffigyField fromField in From!.Fields)
        {
            // Position is not used, because a field's place among its siblings is not what identifies
            // it — so neither side is ever an "ordering" difference.
            pairs.Add(new EffigyFieldPair(fromField, FindField(To!, fromField.FieldName), -1, -1));
        }

        foreach (HollowEffigyField toField in To!.Fields)
        {
            if (FindField(From!, toField.FieldName) is null)
            {
                pairs.Add(new EffigyFieldPair(null, toField, -1, -1));
            }
        }

        return pairs;
    }

    private static HollowEffigyField? FindField(HollowEffigy effigy, string? fieldName) =>
        effigy.Fields.FirstOrDefault(
            field => string.Equals(field.FieldName, fieldName, StringComparison.Ordinal));
}

/// <summary>
/// Pairs a record against nothing, which makes every one of its fields a difference.
/// </summary>
public sealed class HollowEffigyNullPartnerPairer(HollowEffigy? from, HollowEffigy? to)
    : HollowEffigyFieldPairer(from, to)
{
    /// <inheritdoc />
    public override IReadOnlyList<EffigyFieldPair> Pair()
    {
        HollowEffigy? present = From ?? To;

        if (present is null)
        {
            return [];
        }

        bool isFrom = From is not null;

        return
        [
            .. present.Fields.Select((field, index) => isFrom
                ? new EffigyFieldPair(field, null, index, -1)
                : new EffigyFieldPair(null, field, -1, index)),
        ];
    }
}
