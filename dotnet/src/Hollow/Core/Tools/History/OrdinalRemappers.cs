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

using Hollow.Core.Tools.Diff.Exact;
using Hollow.Core.Util;
using Hollow.Core.Write.Copy;

namespace Hollow.Core.Tools.History;

/// <summary>
/// A remapper driven by an explicit table per type.
/// </summary>
/// <remarks>
/// Named <c>IntMapOrdinalRemapper</c> in Java. The history builds one of these to say where each
/// record of a state it is about to discard has been copied to.
/// </remarks>
public sealed class IntMapOrdinalRemapper : IOrdinalRemapper
{
    private readonly Dictionary<string, IntMap> _ordinalMappings = new(StringComparer.Ordinal);

    /// <summary>Records the whole mapping for <paramref name="typeName"/>.</summary>
    public void AddOrdinalRemapping(string typeName, IntMap mapping) => _ordinalMappings[typeName] = mapping;

    /// <summary>The mapping for <paramref name="typeName"/>, or <see langword="null"/> if it has none.</summary>
    public IntMap? GetOrdinalRemapping(string typeName) => _ordinalMappings.GetValueOrDefault(typeName);

    /// <inheritdoc />
    public int GetMappedOrdinal(string type, int originalOrdinal) =>
        _ordinalMappings.TryGetValue(type, out IntMap? mapping) ? mapping.Get(originalOrdinal) : -1;

    /// <inheritdoc />
    public bool OrdinalIsMapped(string type, int originalOrdinal) =>
        _ordinalMappings.TryGetValue(type, out IntMap? mapping) && mapping.Get(originalOrdinal) != -1;

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">
    /// Always: the tables are supplied whole by <see cref="AddOrdinalRemapping"/>.
    /// </exception>
    public void RemapOrdinal(string type, int originalOrdinal, int mappedOrdinal) =>
        throw new NotSupportedException(
            $"Cannot explicitly remap an ordinal in an {nameof(IntMapOrdinalRemapper)}");
}

/// <summary>
/// A remapper that sends every record which survived a transition unchanged to the copy of it that is
/// already there, and only asks about the ones which did not.
/// </summary>
/// <remarks>
/// <para>
/// This is what keeps a history from holding the whole dataset once per state. Most records do not
/// change from one state to the next, and the equality mapping already knows which: those are given
/// the identity ordinal of the group they belong to, so a copy finds the record that has been written
/// once and stops. Only a record with no counterpart is copied afresh, and only those need a table.
/// </para>
/// <para>
/// Named <c>DiffEqualityMappingOrdinalRemapper</c> in Java.
/// </para>
/// </remarks>
public sealed class DiffEqualityMappingOrdinalRemapper : IOrdinalRemapper
{
    private readonly Dictionary<string, IntMap> _unmatchedOrdinalRemapping = new(StringComparer.Ordinal);

    /// <summary>Creates a remapper over <paramref name="mapping"/>.</summary>
    public DiffEqualityMappingOrdinalRemapper(DiffEqualityMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        EqualityMapping = mapping;
    }

    /// <summary>What says which records of the two states are identical.</summary>
    public DiffEqualityMapping EqualityMapping { get; }

    /// <inheritdoc />
    /// <remarks>
    /// A record with no counterpart falls through to its own ordinal, which is what lets a type be
    /// copied before anything has been said about it.
    /// </remarks>
    public int GetMappedOrdinal(string type, int originalOrdinal)
    {
        if (_unmatchedOrdinalRemapping.TryGetValue(type, out IntMap? remapping))
        {
            int remappedOrdinal = remapping.Get(originalOrdinal);

            if (remappedOrdinal != -1)
            {
                return remappedOrdinal;
            }
        }

        int matchedOrdinal = EqualityMapping.GetEqualOrdinalMap(type).GetIdentityFromOrdinal(originalOrdinal);

        return matchedOrdinal == -1 ? originalOrdinal : matchedOrdinal;
    }

    /// <summary>
    /// Says how many records of <paramref name="type"/> will need a mapping, which is what sizes the
    /// table.
    /// </summary>
    /// <remarks>
    /// The table does not grow, so this has to be right — see <see cref="IntMap"/>. It is called
    /// before any of the type's unmatched records are copied, when their number is already known.
    /// </remarks>
    public void HintUnmatchedOrdinalCount(string type, int numOrdinals) =>
        _unmatchedOrdinalRemapping[type] = new IntMap(numOrdinals);

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// <see cref="HintUnmatchedOrdinalCount"/> has not been called for <paramref name="type"/>.
    /// </exception>
    public void RemapOrdinal(string type, int originalOrdinal, int mappedOrdinal)
    {
        if (!_unmatchedOrdinalRemapping.TryGetValue(type, out IntMap? remap))
        {
            throw new InvalidOperationException(
                $"Must call {nameof(HintUnmatchedOrdinalCount)} for type {type} before attempting to remap unmatched ordinals");
        }

        remap.Put(originalOrdinal, mappedOrdinal);
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">
    /// Always: every ordinal is mapped, since an unmatched one maps to itself.
    /// </exception>
    public bool OrdinalIsMapped(string type, int originalOrdinal) => throw new NotSupportedException();

    /// <summary>
    /// Where <paramref name="type"/>'s unmatched records were copied to, or <see langword="null"/>
    /// when none were.
    /// </summary>
    public IntMap? GetUnmatchedOrdinalMapping(string type) => _unmatchedOrdinalRemapping.GetValueOrDefault(type);
}
