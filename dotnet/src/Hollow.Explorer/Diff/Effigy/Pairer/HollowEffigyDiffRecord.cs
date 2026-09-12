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

namespace Hollow.Explorer.Diff.Effigy.Pairer;

/// <summary>
/// How unlike one record is to another, measured by the leaves they do and do not share.
/// </summary>
/// <remarks>
/// <para>
/// Built once from a record and then asked about many candidates, because pairing two collections
/// compares every element against every other. The record's leaves go into a bag with how many times
/// each occurs; a candidate is then walked and each of its leaves either spends one of those or counts
/// against it.
/// </para>
/// <para>
/// Flattened across the whole subtree on purpose: which field a value sat in does not matter to how
/// alike two records are, and insisting it did would stop a record from pairing with the one it
/// obviously came from.
/// </para>
/// </remarks>
public sealed class HollowEffigyDiffRecord
{
    private readonly Dictionary<HollowEffigyField, FieldDiffCount> _fieldCounts = [];

    private int _totalOriginalFieldCount;
    private int _runId;
    private int _simCount;
    private int _diffCount;

    /// <summary>Takes the leaves of <paramref name="basedOn"/> as what a candidate is measured against.</summary>
    public HollowEffigyDiffRecord(HollowEffigy basedOn)
    {
        ArgumentNullException.ThrowIfNull(basedOn);

        TraverseOriginalFields(basedOn);
    }

    /// <summary>
    /// How unlike <paramref name="comparison"/> is, or
    /// <see cref="HollowEffigyCollectionPairer.MaxMatrixElementFieldValue"/> for "not worth pairing".
    /// </summary>
    /// <param name="comparison">The candidate.</param>
    /// <param name="maxDiff">
    /// How different is too different. Walking stops once the count reaches it, which is what makes
    /// pairing a large collection affordable: most pairs are obviously wrong and are abandoned early.
    /// </param>
    public int CalculateDiff(HollowEffigy comparison, int maxDiff)
    {
        ArgumentNullException.ThrowIfNull(comparison);

        // The bag is reused between candidates, so each run is told apart by a number rather than by
        // clearing counts that most candidates will not touch.
        _runId++;
        _simCount = 0;
        _diffCount = 0;

        TraverseComparisonFields(comparison, maxDiff);

        return _diffCount >= maxDiff ? HollowEffigyCollectionPairer.MaxMatrixElementFieldValue : Score();
    }

    private void TraverseOriginalFields(HollowEffigy effigy)
    {
        foreach (HollowEffigyField field in effigy.Fields)
        {
            if (!field.IsLeafNode)
            {
                TraverseOriginalFields((HollowEffigy)field.Value!);

                continue;
            }

            if (!_fieldCounts.TryGetValue(field, out FieldDiffCount? count))
            {
                count = new FieldDiffCount();
                _fieldCounts[field] = count;
            }

            count.OriginalCount++;
            _totalOriginalFieldCount++;
        }
    }

    private void TraverseComparisonFields(HollowEffigy comparison, int maxDiff)
    {
        foreach (HollowEffigyField field in comparison.Fields)
        {
            if (!field.IsLeafNode)
            {
                TraverseComparisonFields((HollowEffigy)field.Value!, maxDiff);

                if (_diffCount >= maxDiff)
                {
                    return;
                }

                continue;
            }

            if (!_fieldCounts.TryGetValue(field, out FieldDiffCount? count))
            {
                // A value the original does not hold at all. Recorded anyway, since the next candidate
                // may hold it too and the bag is shared.
                if (_diffCount + 1 >= maxDiff)
                {
                    _diffCount++;

                    return;
                }

                count = new FieldDiffCount();
                _fieldCounts[field] = count;
            }

            if (count.IncrementComparisonCount(_runId))
            {
                if (++_diffCount >= maxDiff)
                {
                    return;
                }
            }
            else
            {
                _simCount++;
            }
        }
    }

    /// <summary>
    /// The leaves the original holds that the candidate did not account for, plus the ones the
    /// candidate holds that the original does not.
    /// </summary>
    /// <remarks>
    /// Two records with nothing whatever in common score as unpairable rather than merely distant, so
    /// that a collection whose elements were all replaced shows them as arrivals and departures rather
    /// than as a screen of rows where everything differs.
    /// </remarks>
    private int Score()
    {
        int totalDiff = (_totalOriginalFieldCount - _simCount) + _diffCount;

        return _simCount == 0 && totalDiff != 0
            ? HollowEffigyCollectionPairer.MaxMatrixElementFieldValue
            : totalDiff;
    }

    private sealed class FieldDiffCount
    {
        private int _comparisonCount;
        private int _lastComparisonUpdatedRunId;

        internal int OriginalCount { get; set; }

        /// <summary>
        /// Spends one of the original's occurrences of this value, returning whether there were none
        /// left to spend.
        /// </summary>
        internal bool IncrementComparisonCount(int runId)
        {
            if (runId != _lastComparisonUpdatedRunId)
            {
                _comparisonCount = 0;
                _lastComparisonUpdatedRunId = runId;
            }

            return ++_comparisonCount > OriginalCount;
        }
    }
}
