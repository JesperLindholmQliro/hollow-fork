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

using Hollow.Core.Read.DataAccess;
using Hollow.Core.Tools.Diff.Exact;

namespace Hollow.Explorer.Diff.Effigy.Pairer;

/// <summary>
/// Whether two records are identical, asked of whatever already knows.
/// </summary>
/// <remarks>
/// A diff page uses this to collapse a whole subtree into one line when nothing under it moved. The
/// answer already exists — the diff worked it out to decide what to skip — so this is an interface
/// rather than a computation, and the history UI answers it a different way.
/// </remarks>
public interface IExactRecordMatcher
{
    /// <summary>Whether the two records are exactly equal.</summary>
    bool IsExactMatch(
        IHollowTypeDataAccess? fromType, int fromOrdinal, IHollowTypeDataAccess? toType, int toOrdinal);
}

/// <summary>
/// Answers from the equality mapping a <see cref="Core.Tools.Diff.HollowDiff"/> already built.
/// </summary>
public sealed class DiffExactRecordMatcher(DiffEqualityMapping equalityMapping) : IExactRecordMatcher
{
    /// <inheritdoc />
    public bool IsExactMatch(
        IHollowTypeDataAccess? fromType, int fromOrdinal, IHollowTypeDataAccess? toType, int toOrdinal)
    {
        // A type only one side has cannot have an exact match in the other.
        if (fromType is null || toType is null)
        {
            return false;
        }

        return equalityMapping
            .GetEqualOrdinalMap(fromType.Schema.Name)
            .GetEqualOrdinals(fromOrdinal)
            .Contains(toOrdinal);
    }
}

/// <summary>
/// Answers that nothing is an exact match, for a caller with no equality mapping to hand.
/// </summary>
/// <remarks>
/// Safe rather than useless: saying "not identical" only costs the page a subtree it could have
/// collapsed.
/// </remarks>
public sealed class NoExactRecordMatcher : IExactRecordMatcher
{
    /// <summary>The shared instance, since it holds nothing.</summary>
    public static readonly NoExactRecordMatcher Instance = new();

    private NoExactRecordMatcher()
    {
    }

    /// <inheritdoc />
    public bool IsExactMatch(
        IHollowTypeDataAccess? fromType, int fromOrdinal, IHollowTypeDataAccess? toType, int toOrdinal) =>
        false;
}
