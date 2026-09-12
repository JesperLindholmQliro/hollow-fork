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

using Hollow.Core.Util;

namespace Hollow.Core.Tools.Diff;

/// <summary>
/// How much one field differs between two states, and in which records.
/// </summary>
/// <remarks>
/// <para>
/// The score is a count of values that did not pair off, on either side. It is a rough measure on
/// purpose: what it is for is ordering fields by how much they moved, so that whoever is looking at a
/// diff starts where the change is.
/// </para>
/// <para>
/// The record pairs are kept alongside, so the page can offer the records that account for the score
/// rather than only the number.
/// </para>
/// </remarks>
/// <param name="fieldIdentifier">Where in the type's hierarchy this field sits.</param>
public sealed class HollowFieldDiff(HollowDiffNodeIdentifier fieldIdentifier)
    : IComparable<HollowFieldDiff>
{
    private readonly IntList _diffFromOrdinals = new();
    private readonly IntList _diffToOrdinals = new();
    private readonly IntList _diffPairScores = new();

    /// <summary>Where in the type's hierarchy this field sits.</summary>
    public HollowDiffNodeIdentifier FieldIdentifier { get; } = fieldIdentifier;

    /// <summary>The whole difference in this field, across every record.</summary>
    public long TotalDiffScore { get; private set; }

    /// <summary>How many record pairs differ in this field.</summary>
    public int NumDiffs => _diffToOrdinals.Count;

    /// <summary>The <c>from</c> ordinal of the pair at <paramref name="diffPairIndex"/>.</summary>
    public int GetFromOrdinal(int diffPairIndex) => _diffFromOrdinals.Get(diffPairIndex);

    /// <summary>The <c>to</c> ordinal of the pair at <paramref name="diffPairIndex"/>.</summary>
    public int GetToOrdinal(int diffPairIndex) => _diffToOrdinals.Get(diffPairIndex);

    /// <summary>How much that pair differs in this field.</summary>
    public int GetPairScore(int diffPairIndex) => _diffPairScores.Get(diffPairIndex);

    /// <summary>
    /// Records that the pair differs in this field by <paramref name="score"/>.
    /// </summary>
    /// <remarks>
    /// A pair reached more than once in a row — the same field under several branches of one record —
    /// adds to its existing entry rather than making another, which is why only the last is checked.
    /// </remarks>
    public void AddDiff(int fromOrdinal, int toOrdinal, int score)
    {
        if (_diffFromOrdinals.Count > 0
            && _diffFromOrdinals.Get(_diffFromOrdinals.Count - 1) == fromOrdinal
            && _diffToOrdinals.Get(_diffToOrdinals.Count - 1) == toOrdinal)
        {
            int index = _diffPairScores.Count - 1;
            _diffPairScores.Set(index, _diffPairScores.Get(index) + score);
        }
        else
        {
            _diffFromOrdinals.Add(fromOrdinal);
            _diffToOrdinals.Add(toOrdinal);
            _diffPairScores.Add(score);
        }

        TotalDiffScore += score;
    }

    /// <summary>Folds another accounting of the same field into this one.</summary>
    public void AddResults(HollowFieldDiff other)
    {
        ArgumentNullException.ThrowIfNull(other);

        for (int i = 0; i < other.NumDiffs; i++)
        {
            AddDiff(other.GetFromOrdinal(i), other.GetToOrdinal(i), other.GetPairScore(i));
        }
    }

    /// <summary>Orders by score, largest first, since that is the order a diff is read in.</summary>
    public int CompareTo(HollowFieldDiff? other) =>
        other is null ? -1 : other.TotalDiffScore.CompareTo(TotalDiffScore);
}
