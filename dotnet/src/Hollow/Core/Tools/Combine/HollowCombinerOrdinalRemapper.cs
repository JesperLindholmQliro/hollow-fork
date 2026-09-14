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

using Hollow.Core.Index;
using Hollow.Core.Read.Engine;
using Hollow.Core.Write.Copy;

namespace Hollow.Core.Tools.Combine;

/// <summary>
/// Where one input's ordinals ended up in the combined output.
/// </summary>
/// <remarks>
/// Asking about an ordinal that has not been copied yet copies it, which is what pulls a record's
/// references across behind it.
/// </remarks>
public sealed class HollowCombinerOrdinalRemapper : IOrdinalRemapper
{
    private readonly HollowCombiner _combiner;
    private readonly Dictionary<string, int[]> _typeMappings;

    /// <summary>
    /// Tracks <paramref name="inputStateEngine"/>'s ordinals into the state
    /// <paramref name="combiner"/> is building.
    /// </summary>
    public HollowCombinerOrdinalRemapper(HollowCombiner combiner, HollowReadStateEngine inputStateEngine)
    {
        ArgumentNullException.ThrowIfNull(combiner);
        ArgumentNullException.ThrowIfNull(inputStateEngine);

        _combiner = combiner;
        _typeMappings = inputStateEngine.TypeStates.Values.ToDictionary(
            typeState => typeState.Schema.Name,
            typeState => Unmapped(typeState.MaxOrdinal + 1),
            StringComparer.Ordinal);
    }

    /// <inheritdoc />
    /// <remarks>A type this input does not have passes its ordinal through unchanged.</remarks>
    public int GetMappedOrdinal(string type, int originalOrdinal)
    {
        if (!_typeMappings.TryGetValue(type, out int[]? typeMapping))
        {
            return originalOrdinal;
        }

        if (typeMapping[originalOrdinal] == HollowConstants.OrdinalNone)
        {
            typeMapping[originalOrdinal] = _combiner.CopyOrdinal(type, originalOrdinal);
        }

        return typeMapping[originalOrdinal];
    }

    /// <inheritdoc />
    public void RemapOrdinal(string type, int originalOrdinal, int mappedOrdinal) =>
        _typeMappings[type][originalOrdinal] = mappedOrdinal;

    /// <inheritdoc />
    public bool OrdinalIsMapped(string type, int originalOrdinal) =>
        _typeMappings[type][originalOrdinal] != HollowConstants.OrdinalNone;

    private static int[] Unmapped(int length)
    {
        int[] mapping = new int[length];

        Array.Fill(mapping, HollowConstants.OrdinalNone);

        return mapping;
    }
}

/// <summary>
/// A remapper that folds every input's copy of a keyed record onto the one output record.
/// </summary>
/// <remarks>
/// The work is in <see cref="RemapOrdinal"/>: having placed a record from one input, it looks the same
/// key up in every <em>other</em> input and points that input's ordinal at the same output record. A
/// later reference from another input therefore resolves to the copy already written instead of
/// copying a duplicate.
/// </remarks>
internal sealed class HollowCombinerPrimaryKeyOrdinalRemapper(
    IOrdinalRemapper[] baseRemappers,
    IReadOnlyDictionary<string, HollowPrimaryKeyIndex?[]> primaryKeyIndexes,
    int stateEngineIndex) : IOrdinalRemapper
{
    /// <inheritdoc />
    public int GetMappedOrdinal(string type, int originalOrdinal) =>
        baseRemappers[stateEngineIndex].GetMappedOrdinal(type, originalOrdinal);

    /// <inheritdoc />
    public bool OrdinalIsMapped(string type, int originalOrdinal) =>
        baseRemappers[stateEngineIndex].OrdinalIsMapped(type, originalOrdinal);

    /// <inheritdoc />
    public void RemapOrdinal(string type, int originalOrdinal, int mappedOrdinal)
    {
        baseRemappers[stateEngineIndex].RemapOrdinal(type, originalOrdinal, mappedOrdinal);

        if (primaryKeyIndexes.GetValueOrDefault(type) is not { } typeKeyIndexes)
        {
            return;
        }

        object?[] primaryKey = typeKeyIndexes[stateEngineIndex]!.GetRecordKey(originalOrdinal);

        for (int i = 0; i < baseRemappers.Length; i++)
        {
            if (i == stateEngineIndex || typeKeyIndexes[i] is not { } otherIndex)
            {
                continue;
            }

            int matchOrdinal = otherIndex.GetMatchingOrdinal(primaryKey);

            if (matchOrdinal != HollowConstants.OrdinalNone)
            {
                baseRemappers[i].RemapOrdinal(type, matchOrdinal, mappedOrdinal);
            }
        }
    }
}
