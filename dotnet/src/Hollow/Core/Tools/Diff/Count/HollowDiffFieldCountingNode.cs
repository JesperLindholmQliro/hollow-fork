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

using Hollow.Core.Read;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Schema;
using Hollow.Core.Util;

namespace Hollow.Core.Tools.Diff.Count;

/// <summary>
/// The leaf of the counting tree: one field holding a value rather than a reference.
/// </summary>
/// <remarks>
/// <para>
/// This is where a difference finally becomes a number. The values on the <c>from</c> side are hashed
/// with the count of how many times each occurs; each <c>to</c> value then spends one of those. What is
/// left unspent on either side is the score — so a value that moved counts twice, once for going and
/// once for arriving, and a value that merely repeated a different number of times counts the
/// difference.
/// </para>
/// <para>
/// The table is by value rather than by ordinal because two records can hold equal values at different
/// ordinals, and for the purposes of a diff those are the same value.
/// </para>
/// </remarks>
public sealed class HollowDiffFieldCountingNode : HollowDiffCountingNode
{
    private readonly HollowObjectTypeReadState? _fromState;
    private readonly HollowObjectTypeReadState? _toState;
    private readonly int _fromFieldIndex;
    private readonly int _toFieldIndex;
    private readonly HollowFieldDiff _fieldDiff;

    private int[] _hashedOrdinals = new int[16];
    private int[] _ordinalHashCodes = new int[16];
    private int[] _ordinalHashCounts = new int[16];
    private int _hashSizeBeforeGrow = 11;
    private int _hashSize;
    private int _unmatchedToFields;

    private int _currentTopLevelFromOrdinal;
    private int _currentTopLevelToOrdinal;

    /// <summary>Builds a node over field <paramref name="unionFieldIndex"/> of <paramref name="unionSchema"/>.</summary>
    public HollowDiffFieldCountingNode(
        HollowDiff? diff,
        HollowTypeDiff? topLevelTypeDiff,
        HollowDiffNodeIdentifier nodeId,
        HollowObjectTypeReadState? fromState,
        HollowObjectTypeReadState? toState,
        HollowObjectSchema unionSchema,
        int unionFieldIndex)
        : base(diff, topLevelTypeDiff, nodeId)
    {
        ArgumentNullException.ThrowIfNull(unionSchema);

        _fromState = fromState;
        _toState = toState;

        string fieldName = unionSchema.GetFieldName(unionFieldIndex);

        _fromFieldIndex = fromState?.Schema.GetPosition(fieldName) ?? -1;
        _toFieldIndex = toState?.Schema.GetPosition(fieldName) ?? -1;
        _fieldDiff = new HollowFieldDiff(nodeId);

        Array.Fill(_hashedOrdinals, HollowConstants.OrdinalNone);
    }

    /// <inheritdoc />
    public override void Prepare(int topLevelFromOrdinal, int topLevelToOrdinal)
    {
        _currentTopLevelFromOrdinal = topLevelFromOrdinal;
        _currentTopLevelToOrdinal = topLevelToOrdinal;
    }

    /// <inheritdoc />
    public override int TraverseDiffs(IntList fromOrdinals, IntList toOrdinals)
    {
        ArgumentNullException.ThrowIfNull(fromOrdinals);
        ArgumentNullException.ThrowIfNull(toOrdinals);

        // A field only one of the schemas has cannot be compared, only counted.
        if (_fromFieldIndex == -1 || _toFieldIndex == -1)
        {
            return TraverseMissingFields(fromOrdinals, toOrdinals);
        }

        ClearHashTable();

        for (int i = 0; i < fromOrdinals.Count; i++)
        {
            IndexFromOrdinal(fromOrdinals.Get(i));
        }

        for (int i = 0; i < toOrdinals.Count; i++)
        {
            CompareToOrdinal(toOrdinals.Get(i));
        }

        int score = _unmatchedToFields;

        foreach (int count in _ordinalHashCounts)
        {
            score += count;
        }

        if (score != 0)
        {
            _fieldDiff.AddDiff(_currentTopLevelFromOrdinal, _currentTopLevelToOrdinal, score);
        }

        return score;
    }

    /// <inheritdoc />
    public override int TraverseMissingFields(IntList fromOrdinals, IntList toOrdinals)
    {
        ArgumentNullException.ThrowIfNull(fromOrdinals);
        ArgumentNullException.ThrowIfNull(toOrdinals);

        // Everything present on the side that has the field is a difference, since the other side
        // cannot hold it at all.
        if (_fromFieldIndex == -1)
        {
            _fieldDiff.AddDiff(_currentTopLevelFromOrdinal, _currentTopLevelToOrdinal, toOrdinals.Count);

            return toOrdinals.Count;
        }

        if (_toFieldIndex == -1)
        {
            _fieldDiff.AddDiff(_currentTopLevelFromOrdinal, _currentTopLevelToOrdinal, fromOrdinals.Count);

            return fromOrdinals.Count;
        }

        return 0;
    }

    /// <inheritdoc />
    public override IReadOnlyList<HollowFieldDiff> GetFieldDiffs() =>
        _fieldDiff.TotalDiffScore > 0 ? [_fieldDiff] : [];

    private void ClearHashTable()
    {
        Array.Fill(_hashedOrdinals, HollowConstants.OrdinalNone);
        Array.Clear(_ordinalHashCounts);
        _unmatchedToFields = 0;
        _hashSize = 0;
    }

    private void IndexFromOrdinal(int ordinal)
    {
        if (_hashSize == _hashSizeBeforeGrow)
        {
            GrowHashTable();
        }

        int hashCode = HollowReadFieldUtils.FieldHashCode(_fromState!, ordinal, _fromFieldIndex);

        if (HashIntoArray(ordinal, hashCode, 1, _hashedOrdinals, _ordinalHashCodes, _ordinalHashCounts))
        {
            _hashSize++;
        }
    }

    private void CompareToOrdinal(int ordinal)
    {
        int hashCode = HollowReadFieldUtils.FieldHashCode(_toState!, ordinal, _toFieldIndex);
        int bucket = hashCode & (_hashedOrdinals.Length - 1);

        while (_hashedOrdinals[bucket] != HollowConstants.OrdinalNone)
        {
            if (HollowReadFieldUtils.FieldsAreEqual(
                _fromState!, _hashedOrdinals[bucket], _fromFieldIndex, _toState!, ordinal, _toFieldIndex))
            {
                // Spend one of the from side's occurrences, or count this as arriving from nowhere.
                if (_ordinalHashCounts[bucket] > 0)
                {
                    _ordinalHashCounts[bucket]--;
                }
                else
                {
                    _unmatchedToFields++;
                }

                return;
            }

            bucket = (bucket + 1) & (_hashedOrdinals.Length - 1);
        }

        _unmatchedToFields++;
    }

    private void GrowHashTable()
    {
        int[] newHashedOrdinals = new int[_hashedOrdinals.Length * 2];
        int[] newOrdinalHashCodes = new int[_ordinalHashCodes.Length * 2];
        int[] newOrdinalHashCounts = new int[_ordinalHashCounts.Length * 2];

        Array.Fill(newHashedOrdinals, HollowConstants.OrdinalNone);

        foreach (long ordinalAndHashCode in OrdinalsAndHashCodes())
        {
            int ordinal = (int)(ordinalAndHashCode >> 32);
            int hashCode = (int)ordinalAndHashCode;

            HashIntoArray(
                ordinal,
                hashCode,
                FindOrdinalCount(ordinal, hashCode),
                newHashedOrdinals,
                newOrdinalHashCodes,
                newOrdinalHashCounts);
        }

        _hashedOrdinals = newHashedOrdinals;
        _ordinalHashCodes = newOrdinalHashCodes;
        _ordinalHashCounts = newOrdinalHashCounts;
        _hashSizeBeforeGrow = newHashedOrdinals.Length * 7 / 10;
    }

    /// <summary>
    /// The table's live entries, ordered, so that regrowing it is deterministic rather than depending on
    /// where the old table happened to put things.
    /// </summary>
    private long[] OrdinalsAndHashCodes()
    {
        long[] ordinalsAndHashCodes = new long[_hashSize];
        int count = 0;

        for (int i = 0; i < _hashedOrdinals.Length; i++)
        {
            if (_hashedOrdinals[i] != HollowConstants.OrdinalNone)
            {
                ordinalsAndHashCodes[count++] =
                    ((long)_hashedOrdinals[i] << 32) | (uint)_ordinalHashCodes[i];
            }
        }

        Array.Sort(ordinalsAndHashCodes);

        return ordinalsAndHashCodes;
    }

    private int FindOrdinalCount(int ordinal, int hashCode)
    {
        int bucket = hashCode & (_hashedOrdinals.Length - 1);

        while (_hashedOrdinals[bucket] != ordinal)
        {
            bucket = (bucket + 1) & (_hashedOrdinals.Length - 1);
        }

        return _ordinalHashCounts[bucket];
    }

    private bool HashIntoArray(
        int ordinal,
        int hashCode,
        int count,
        int[] hashedOrdinals,
        int[] ordinalHashCodes,
        int[] ordinalHashCounts)
    {
        int bucket = hashCode & (hashedOrdinals.Length - 1);

        while (hashedOrdinals[bucket] != HollowConstants.OrdinalNone)
        {
            // Keyed by value: a second record holding the same value adds to the count rather than
            // taking a bucket of its own.
            if (HollowReadFieldUtils.FieldsAreEqual(
                _fromState!, hashedOrdinals[bucket], _fromFieldIndex, _fromState!, ordinal, _fromFieldIndex))
            {
                ordinalHashCounts[bucket]++;

                return false;
            }

            bucket = (bucket + 1) & (hashedOrdinals.Length - 1);
        }

        hashedOrdinals[bucket] = ordinal;
        ordinalHashCodes[bucket] = hashCode;
        ordinalHashCounts[bucket] = count;

        return true;
    }
}
