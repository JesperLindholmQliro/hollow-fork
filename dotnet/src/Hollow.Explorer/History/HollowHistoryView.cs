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

using Hollow.Explorer.Diff;
using Hollow.Explorer.Diff.Effigy.Pairer;

namespace Hollow.Explorer.History;

/// <summary>
/// One record as it stood on either side of one transition.
/// </summary>
/// <remarks>
/// Named <c>HollowHistoryView</c> in Java. The record is found by key rather than by a pair of
/// ordinals, because that is what a history page has: an ordinal means nothing on its own once the
/// state it came from is several transitions back.
/// </remarks>
/// <param name="historicalVersion">The version this is a view of.</param>
/// <param name="type">The record's type.</param>
/// <param name="keyOrdinal">The record's key.</param>
/// <param name="latestStateEngineRandomizedTag">
/// What the live state was on when this was built. A refresh replaces it, and every ordinal held here
/// stops meaning anything.
/// </param>
/// <param name="rootRow">The root of the row tree.</param>
/// <param name="exactRecordMatcher">What knows whether two records are identical.</param>
public sealed class HollowHistoryView(
    long historicalVersion,
    string type,
    int keyOrdinal,
    long latestStateEngineRandomizedTag,
    HollowDiffViewRow rootRow,
    IExactRecordMatcher exactRecordMatcher)
    : HollowObjectView(rootRow, exactRecordMatcher)
{
    /// <summary>The version this is a view of.</summary>
    public long HistoricalVersion { get; } = historicalVersion;

    /// <summary>The record's type.</summary>
    public string Type { get; } = type;

    /// <summary>The record's key.</summary>
    public int KeyOrdinal { get; } = keyOrdinal;

    /// <summary>What the live state was on when this was built.</summary>
    public long LatestStateEngineRandomizedTag { get; } = latestStateEngineRandomizedTag;
}
