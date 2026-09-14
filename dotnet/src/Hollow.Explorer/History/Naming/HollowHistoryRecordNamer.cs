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
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Tools.History;
using Hollow.Core.Tools.History.KeyIndex;

namespace Hollow.Explorer.History.Naming;

/// <summary>
/// What to call a record on a history page.
/// </summary>
/// <remarks>
/// <para>
/// By default a record is named by its primary key, which is all the history is sure to have. An
/// application that knows better — that a movie should be listed by its title rather than its id —
/// subclasses this and says so.
/// </para>
/// <para>
/// Named <c>HollowHistoryRecordNamer</c> in Java.
/// </para>
/// </remarks>
public class HollowHistoryRecordNamer
{
    /// <summary>The naming used when the application has supplied none.</summary>
    public static readonly HollowHistoryRecordNamer Default = new();

    /// <summary>
    /// What to call the record at <paramref name="recordOrdinal"/>, falling back to its key.
    /// </summary>
    /// <param name="historicalState">The state the record is being shown in.</param>
    /// <param name="typeKeyMapping">The type's keys in that state.</param>
    /// <param name="keyOrdinal">The record's key.</param>
    /// <param name="dataAccess">Where the record can be read, or null if the type is not readable.</param>
    /// <param name="recordOrdinal">Where the record sits.</param>
    public virtual string GetRecordName(
        HollowHistoricalState historicalState,
        HollowHistoricalStateTypeKeyOrdinalMapping typeKeyMapping,
        int keyOrdinal,
        IHollowObjectTypeDataAccess? dataAccess,
        int recordOrdinal)
    {
        ArgumentNullException.ThrowIfNull(typeKeyMapping);

        return GetRecordName(dataAccess, recordOrdinal)
            ?? typeKeyMapping.KeyIndex.GetKeyDisplayString(keyOrdinal);
    }

    /// <summary>
    /// What to call the record at <paramref name="recordOrdinal"/>, or <see langword="null"/> to fall
    /// back to its key.
    /// </summary>
    /// <remarks>This is the one to override; the default knows nothing about the data model.</remarks>
    public virtual string? GetRecordName(IHollowObjectTypeDataAccess? dataAccess, int recordOrdinal) => null;

    /// <summary>
    /// What to call the group of records sharing <paramref name="value"/> in key field
    /// <paramref name="keyFieldIndex"/>.
    /// </summary>
    public virtual string GetKeyFieldName(HollowHistoricalState historicalState, object? value, int keyFieldIndex) =>
        Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
}
