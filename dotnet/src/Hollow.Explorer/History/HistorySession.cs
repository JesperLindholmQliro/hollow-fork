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

using Hollow.Explorer.History.Models;

namespace Hollow.Explorer.History;

/// <summary>
/// What one reader is in the middle of: the type's changes they are looking at, and the record they
/// have open.
/// </summary>
/// <remarks>
/// Both are here for the same reason. Gathering a type's changes walks every changed key, and the page
/// that expands one group has to reach the same tree the page was drawn from; likewise the row tree of
/// an open record is built lazily and changed in place, so the request that opens a row has to find
/// the tree the page came from.
/// </remarks>
public sealed class HistorySession
{
    /// <summary>The type's changes this reader is looking at, if any.</summary>
    public HistoryStateTypeChanges? StateTypeChanges { get; private set; }

    /// <summary>The record this reader has open, if any.</summary>
    public HollowHistoryView? HistoryView { get; private set; }

    /// <summary>
    /// The changes to <paramref name="type"/> at <paramref name="version"/>, gathering them if this
    /// reader is not already looking at exactly those.
    /// </summary>
    public HistoryStateTypeChanges GetOrCreateStateTypeChanges(
        long version,
        string type,
        IReadOnlyList<string> groupedFieldNames,
        Func<HistoryStateTypeChanges> create)
    {
        ArgumentNullException.ThrowIfNull(create);

        if (StateTypeChanges is { } existing
            && existing.StateVersion == version
            && string.Equals(existing.TypeName, type, StringComparison.Ordinal)
            && existing.GroupedFieldNames.SequenceEqual(groupedFieldNames, StringComparer.Ordinal))
        {
            return existing;
        }

        StateTypeChanges = create();

        return StateTypeChanges;
    }

    /// <summary>
    /// The view over <paramref name="type"/>'s record at <paramref name="keyOrdinal"/>, building one
    /// if this reader does not already have it open.
    /// </summary>
    /// <param name="version">The state the record is being looked at in.</param>
    /// <param name="type">The record's type.</param>
    /// <param name="keyOrdinal">The record's key.</param>
    /// <param name="latestRandomizedTag">
    /// What the live state is on. A refresh replaces it, which invalidates every ordinal the open view
    /// holds.
    /// </param>
    /// <param name="create">How to build the view, when the held one will not do.</param>
    public HollowHistoryView GetOrCreateHistoryView(
        long version,
        string type,
        int keyOrdinal,
        long latestRandomizedTag,
        Func<HollowHistoryView> create)
    {
        ArgumentNullException.ThrowIfNull(create);

        if (HistoryView is { } existing
            && existing.HistoricalVersion == version
            && string.Equals(existing.Type, type, StringComparison.Ordinal)
            && existing.KeyOrdinal == keyOrdinal
            && existing.LatestStateEngineRandomizedTag == latestRandomizedTag)
        {
            return existing;
        }

        HollowHistoryView view = create();
        view.ResetView();
        HistoryView = view;

        return view;
    }
}

/// <summary>
/// The history UI's sessions, found from the cookie naming one.
/// </summary>
public sealed class HistorySessionStore : UISessionStore<HistorySession>
{
    /// <inheritdoc />
    protected override string CookieName => "hollow-history-session";
}
