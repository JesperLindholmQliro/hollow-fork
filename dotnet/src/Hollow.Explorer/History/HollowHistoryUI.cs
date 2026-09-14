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

using System.Collections.Concurrent;
using Hollow.Core.Index.Key;
using Hollow.Core.Tools.History;
using Hollow.Explorer.Diff;
using Hollow.Explorer.Diff.Effigy;
using Hollow.Explorer.Diff.Effigy.Pairer;
using Hollow.Explorer.Diff.Models;
using Hollow.Explorer.History.Naming;

namespace Hollow.Explorer.History;

/// <summary>
/// A <see cref="HollowHistory"/> made readable: which versions changed what, and what each changed
/// record looked like on either side.
/// </summary>
/// <remarks>
/// <para>
/// The diff UI compares two states that may have nothing to do with each other. This compares two
/// adjacent states of one delta chain, which is a much easier question — an ordinal is stable across a
/// transition, so a record that did not move did not change.
/// </para>
/// <para>
/// Named <c>HollowHistoryUI</c> in Java, where it is also the router. Routing is
/// <see cref="HollowHistoryUIExtensions"/>' job here, so this is what a page needs and nothing else.
/// </para>
/// </remarks>
public sealed class HollowHistoryUI : IHollowRecordDiffUI
{
    private readonly Dictionary<string, ICustomHollowEffigyFactory> _customEffigyFactories =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, HollowHistoryRecordNamer> _customRecordNamers =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, PrimaryKey> _matchHints = new(StringComparer.Ordinal);

    /// <summary>
    /// Shows <paramref name="history"/>, with timestamps read in <paramref name="timeZone"/>.
    /// </summary>
    /// <param name="history">The history to show.</param>
    /// <param name="timeZone">
    /// What zone to read a clock-stamped version in. Java defaults this to Netflix's own Pacific zone;
    /// UTC is the only defensible default for a library, and a caller who wants another says so.
    /// </param>
    public HollowHistoryUI(HollowHistory history, TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(history);

        History = history;
        TimeZone = timeZone ?? TimeZoneInfo.Utc;
    }

    /// <summary>The history being shown.</summary>
    public HollowHistory History { get; }

    /// <summary>What zone a clock-stamped version is read in.</summary>
    public TimeZoneInfo TimeZone { get; }

    /// <summary>
    /// The header tags to show a column of on the overview, in the order the columns should appear.
    /// </summary>
    public IReadOnlyList<string> OverviewDisplayHeaders { get; set; } = [];

    /// <summary>
    /// Extra cells to put in every page's header bar, by key, each with the position it sorts at.
    /// </summary>
    /// <remarks>
    /// A host embedding the history uses this to put its own links beside the built-in ones. It is
    /// concurrent because a host may well change it while pages are being drawn.
    /// </remarks>
    public ConcurrentDictionary<string, CommonHeaderEntry> CommonHeaderEntries { get; } =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public IReadOnlyDictionary<string, PrimaryKey> MatchHints => _matchHints;

    /// <inheritdoc />
    /// <remarks>
    /// The history's two sides are two states of one delta chain, so an ordinal that did not move
    /// names a record that did not change — which is the whole answer, with nothing to compute.
    /// </remarks>
    public IExactRecordMatcher ExactRecordMatcher => HistoryExactRecordMatcher.Instance;

    /// <summary>Says how to lay out <paramref name="typeName"/>'s records on a record page.</summary>
    public void AddCustomHollowEffigyFactory(string typeName, ICustomHollowEffigyFactory factory) =>
        _customEffigyFactories[typeName] = factory;

    /// <inheritdoc />
    public ICustomHollowEffigyFactory? GetCustomHollowEffigyFactory(string typeName) =>
        _customEffigyFactories.GetValueOrDefault(typeName);

    /// <summary>Says what to call <paramref name="typeName"/>'s records.</summary>
    public void AddCustomHollowRecordNamer(string typeName, HollowHistoryRecordNamer recordNamer) =>
        _customRecordNamers[typeName] = recordNamer;

    /// <summary>What to call <paramref name="typeName"/>'s records.</summary>
    public HollowHistoryRecordNamer GetHistoryRecordNamer(string typeName) =>
        _customRecordNamers.GetValueOrDefault(typeName) ?? HollowHistoryRecordNamer.Default;

    /// <summary>
    /// Says which element of one collection is which of another, for the type
    /// <paramref name="matchHint"/> names.
    /// </summary>
    public void AddMatchHint(PrimaryKey matchHint)
    {
        ArgumentNullException.ThrowIfNull(matchHint);

        _matchHints[matchHint.Type] = matchHint;
    }

    /// <summary>Puts <paramref name="value"/> in every page's header bar.</summary>
    public void AddCommonHeaderEntry(string key, string value, int position) =>
        CommonHeaderEntries[key] = new CommonHeaderEntry(value, position);

    /// <summary>Takes <paramref name="key"/>'s cell back out of the header bar.</summary>
    public void RemoveCommonHeaderEntry(string key) => CommonHeaderEntries.TryRemove(key, out _);

    /// <summary>
    /// Every header tag either side of <paramref name="state"/>'s transition recorded, in name order.
    /// </summary>
    /// <remarks>
    /// The later side is the next state's tags, or the live state's when this is the newest state.
    /// </remarks>
    public IReadOnlyList<DiffHeaderEntry> GetHeaderEntries(HollowHistoricalState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        IReadOnlyDictionary<string, string> fromTags = state.HeaderEntries;
        IReadOnlyDictionary<string, string> toTags = NextStateHeaderTags(state);

        return
        [
            .. fromTags.Keys
                .Concat(toTags.Keys)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Select((key, index) => new DiffHeaderEntry(
                    index,
                    key,
                    fromTags.GetValueOrDefault(key),
                    toTags.GetValueOrDefault(key))),
        ];
    }

    /// <summary>
    /// The header tags of whatever came after <paramref name="state"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string> NextStateHeaderTags(HollowHistoricalState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return state.NextState?.HeaderEntries ?? History.LatestState.HeaderTags;
    }

    /// <summary>
    /// One cell a host put in the header bar, and where it sorts among the others.
    /// </summary>
    /// <remarks>Named <c>ValuePositionPair</c> in Java.</remarks>
    /// <param name="Value">The cell's markup.</param>
    /// <param name="Position">What the cells are ordered by, ascending.</param>
    public sealed record CommonHeaderEntry(string Value, int Position);
}
