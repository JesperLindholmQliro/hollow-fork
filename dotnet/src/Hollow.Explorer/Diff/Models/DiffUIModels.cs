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

namespace Hollow.Explorer.Diff.Models;

/// <summary>
/// One step of the trail back to the overview.
/// </summary>
/// <remarks>
/// Named <c>HollowDiffUIBreadcrumbs</c> in Java, plural for a single crumb; this is one of them.
/// </remarks>
/// <param name="Link">Where the crumb goes, or <see langword="null"/> for the page you are on.</param>
/// <param name="DisplayText">What the crumb reads.</param>
public sealed record DiffBreadcrumb(string? Link, string DisplayText);

/// <summary>
/// One row of the overview: what a type holds on each side and how far apart the two are.
/// </summary>
/// <remarks>Named <c>HollowDiffOverviewTypeEntry</c> in Java.</remarks>
public sealed record DiffOverviewTypeEntry
{
    /// <summary>The type this row is about.</summary>
    public required string TypeName { get; init; }

    /// <summary>Whether the type has a key saying which record matches which.</summary>
    /// <remarks>
    /// Without one nothing can be matched up, so the counts below are all this row can say — which is
    /// why it is coloured differently.
    /// </remarks>
    public required bool HasUniqueKey { get; init; }

    /// <summary>How much the matched records differ, summed over every field.</summary>
    public required long TotalDiffScore { get; init; }

    /// <summary>Records in the earlier state with no counterpart.</summary>
    public required int UnmatchedInFrom { get; init; }

    /// <summary>Records in the later state with no counterpart.</summary>
    public required int UnmatchedInTo { get; init; }

    /// <summary>Records in the earlier state.</summary>
    public required int TotalInFrom { get; init; }

    /// <summary>Records in the later state.</summary>
    public required int TotalInTo { get; init; }

    /// <summary>What the type costs in the earlier state, in bytes.</summary>
    public required long HeapInFrom { get; init; }

    /// <summary>What the type costs in the later state, in bytes.</summary>
    public required long HeapInTo { get; init; }

    /// <summary>What the earlier state spends on ordinals holding nothing, in bytes.</summary>
    public required long HoleInFrom { get; init; }

    /// <summary>What the later state spends on ordinals holding nothing, in bytes.</summary>
    public required long HoleInTo { get; init; }

    /// <summary>Whether either side holds any records at all.</summary>
    public bool HasData => TotalInFrom != 0 || TotalInTo != 0;

    /// <summary>Whether either side holds a record the other does not.</summary>
    public bool HasUnmatched => UnmatchedInFrom > 0 || UnmatchedInTo > 0;

    /// <summary>How many records the type gained or lost.</summary>
    public int DeltaSize => Math.Abs(TotalInFrom - TotalInTo);

    /// <summary><see cref="HeapInFrom"/> as something a person reads at a glance.</summary>
    public string HeapInFromFormatted => ByteSize.Format(HeapInFrom);

    /// <summary><see cref="HeapInTo"/> as something a person reads at a glance.</summary>
    public string HeapInToFormatted => ByteSize.Format(HeapInTo);

    /// <summary><see cref="HoleInFrom"/> as something a person reads at a glance.</summary>
    public string HoleInFromFormatted => ByteSize.Format(HoleInFrom);

    /// <summary><see cref="HoleInTo"/> as something a person reads at a glance.</summary>
    public string HoleInToFormatted => ByteSize.Format(HoleInTo);

    /// <summary>
    /// What the row should be coloured, which is how the overview is read at a glance.
    /// </summary>
    /// <remarks>
    /// Grey for a type with no records either side, yellow for one with no key — deeper where the
    /// counts also moved, since that is the only evidence of change available without a key — and
    /// orange for one that genuinely differs.
    /// </remarks>
    public string BackgroundColor =>
        !HasData ? "#D0D0D0"
        : !HasUniqueKey ? (TotalInFrom != TotalInTo ? "#F0E592" : "#FFFBDB")
        : TotalDiffScore > 0 || HasUnmatched ? "#FFCC99"
        : "";
}

/// <summary>
/// How far apart one field is across every pair of records that matched.
/// </summary>
/// <remarks>Named <c>HollowFieldDiffScore</c> in Java.</remarks>
/// <param name="TypeName">The type the field belongs to.</param>
/// <param name="TypeFieldIndex">Where the field sits in the type's list of differing fields.</param>
/// <param name="DisplayName">The path to the field, as the page shows it.</param>
/// <param name="NumDiffObjects">Record pairs in which this field differs.</param>
/// <param name="NumTotalObjectPairs">Record pairs there were.</param>
/// <param name="DiffScore">How far apart the field is, summed over those pairs.</param>
public sealed record FieldDiffScore(
    string TypeName,
    int TypeFieldIndex,
    string DisplayName,
    int NumDiffObjects,
    int NumTotalObjectPairs,
    long DiffScore) : IComparable<FieldDiffScore>
{
    /// <summary>Orders the field that differs in the most records first.</summary>
    public int CompareTo(FieldDiffScore? other) => other is null ? -1 : other.NumDiffObjects - NumDiffObjects;

    /// <summary>Orders the field that differs in the most records first.</summary>
    public static bool operator <(FieldDiffScore left, FieldDiffScore right) => left.CompareTo(right) < 0;

    /// <summary>Orders the field that differs in the most records first.</summary>
    public static bool operator >(FieldDiffScore left, FieldDiffScore right) => left.CompareTo(right) > 0;

    /// <summary>Orders the field that differs in the most records first.</summary>
    public static bool operator <=(FieldDiffScore left, FieldDiffScore right) => left.CompareTo(right) <= 0;

    /// <summary>Orders the field that differs in the most records first.</summary>
    public static bool operator >=(FieldDiffScore left, FieldDiffScore right) => left.CompareTo(right) >= 0;
}

/// <summary>
/// Two records that matched, and how far apart they are.
/// </summary>
/// <remarks>
/// Named <c>HollowObjectPairDiffScore</c> in Java. The score is built up a field at a time, which is
/// why it is settable where the rest of the pair is not.
/// </remarks>
/// <param name="DisplayKey">The key both records share, as the page shows it.</param>
/// <param name="FromOrdinal">The record in the earlier state.</param>
/// <param name="ToOrdinal">The record in the later state.</param>
public sealed record ObjectPairDiffScore(string DisplayKey, int FromOrdinal, int ToOrdinal)
    : IComparable<ObjectPairDiffScore>
{
    /// <summary>How far apart the two records are.</summary>
    public int DiffScore { get; private set; }

    /// <summary>Adds one field's contribution to the score.</summary>
    public void IncrementDiffScore(int incrementBy) => DiffScore += incrementBy;

    /// <summary>Orders the widest-apart pair first.</summary>
    public int CompareTo(ObjectPairDiffScore? other) => other is null ? -1 : other.DiffScore - DiffScore;

    /// <summary>Orders the widest-apart pair first.</summary>
    public static bool operator <(ObjectPairDiffScore left, ObjectPairDiffScore right) => left.CompareTo(right) < 0;

    /// <summary>Orders the widest-apart pair first.</summary>
    public static bool operator >(ObjectPairDiffScore left, ObjectPairDiffScore right) => left.CompareTo(right) > 0;

    /// <summary>Orders the widest-apart pair first.</summary>
    public static bool operator <=(ObjectPairDiffScore left, ObjectPairDiffScore right) => left.CompareTo(right) <= 0;

    /// <summary>Orders the widest-apart pair first.</summary>
    public static bool operator >=(ObjectPairDiffScore left, ObjectPairDiffScore right) => left.CompareTo(right) >= 0;
}

/// <summary>
/// A record on one side with no counterpart on the other.
/// </summary>
/// <remarks>Named <c>HollowUnmatchedObject</c> in Java.</remarks>
/// <param name="DisplayKey">The record's key, as the page shows it.</param>
/// <param name="Ordinal">Where the record sits in its own state.</param>
public sealed record UnmatchedObject(string DisplayKey, int Ordinal);

/// <summary>
/// One header tag, as the two blobs each recorded it.
/// </summary>
/// <remarks>Named <c>HollowHeaderEntry</c> in Java.</remarks>
/// <param name="Index">Where the tag sits in the table, counting from zero.</param>
/// <param name="Key">The tag's name.</param>
/// <param name="FromValue">What the earlier blob recorded, or <see langword="null"/>.</param>
/// <param name="ToValue">What the later blob recorded, or <see langword="null"/>.</param>
public sealed record DiffHeaderEntry(int Index, string Key, string? FromValue, string? ToValue)
{
    /// <summary>Whether both blobs recorded the same thing.</summary>
    public bool IsSame => string.Equals(FromValue, ToValue, StringComparison.Ordinal);

    /// <summary>What the row should be coloured, which marks out the tags that moved.</summary>
    public string BackgroundColor => IsSame ? "" : "#FFCC99";

    /// <summary>What the earlier blob recorded, with a missing tag spelled out.</summary>
    public string FromValueDisplay => FromValue ?? "null";

    /// <summary>What the later blob recorded, with a missing tag spelled out.</summary>
    public string ToValueDisplay => ToValue ?? "null";
}
