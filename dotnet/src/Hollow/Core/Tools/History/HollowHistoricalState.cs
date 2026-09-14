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

using Hollow.Core.Tools.History.KeyIndex;

namespace Hollow.Core.Tools.History;

/// <summary>
/// One version in the past, held as the changes the next transition made.
/// </summary>
/// <remarks>
/// <para>
/// To find a particular record as it stood here: ask the history's
/// <see cref="HollowHistoryKeyIndex"/> for the key ordinal of the key you are after, then ask this
/// state's <see cref="KeyOrdinalMapping"/> where that key sat. If this state did not change the
/// record, walk <see cref="NextState"/> until one did.
/// </para>
/// <para>
/// Named <c>HollowHistoricalState</c> in Java.
/// </para>
/// </remarks>
public sealed class HollowHistoricalState
{
    /// <summary>
    /// Holds <paramref name="version"/>'s changes.
    /// </summary>
    /// <param name="version">The version this state is of.</param>
    /// <param name="keyOrdinalMapping">Which keys this transition added and removed.</param>
    /// <param name="dataAccess">The records themselves.</param>
    /// <param name="headerEntries">The header tags the blob of this version carried.</param>
    public HollowHistoricalState(
        long version,
        HollowHistoricalStateKeyOrdinalMapping keyOrdinalMapping,
        HollowHistoricalStateDataAccess dataAccess,
        IReadOnlyDictionary<string, string> headerEntries)
    {
        ArgumentNullException.ThrowIfNull(keyOrdinalMapping);
        ArgumentNullException.ThrowIfNull(dataAccess);
        ArgumentNullException.ThrowIfNull(headerEntries);

        Version = version;
        KeyOrdinalMapping = keyOrdinalMapping;
        DataAccess = dataAccess;
        HeaderEntries = headerEntries;
    }

    /// <summary>The version this state is of.</summary>
    public long Version { get; }

    /// <summary>
    /// The records, readable through a generated client or the generic object API just as a live state
    /// would be.
    /// </summary>
    public HollowHistoricalStateDataAccess DataAccess { get; }

    /// <summary>Which keys this transition added and removed.</summary>
    public HollowHistoricalStateKeyOrdinalMapping KeyOrdinalMapping { get; }

    /// <summary>The header tags the blob of this version carried.</summary>
    public IReadOnlyDictionary<string, string> HeaderEntries { get; }

    /// <summary>The state that came after this one.</summary>
    public HollowHistoricalState? NextState { get; internal set; }

    /// <summary>Roughly how much memory this state's kept records occupy.</summary>
    public long ApproximateHeapFootprintInBytes =>
        DataAccess.TypeDataAccessMap.Values.Sum(access => access.RemovedRecords.ApproxHeapFootprintInBytes);
}
