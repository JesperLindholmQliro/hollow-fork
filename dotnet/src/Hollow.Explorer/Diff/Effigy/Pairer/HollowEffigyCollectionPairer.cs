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

using Hollow.Core.Index.Key;
using Hollow.Core.Memory.Encoding;
using Hollow.Core.Read;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Schema;
using Hollow.Core.Util;

namespace Hollow.Explorer.Diff.Effigy.Pairer;

/// <summary>
/// Lines two collections' elements up against each other.
/// </summary>
/// <remarks>
/// <para>
/// The hard case. Two collections have no field names to go by and need not be the same length, so
/// which element on the left is which element on the right has to be worked out. There are two ways,
/// and which one is used decides whether a diff page is readable.
/// </para>
/// <para>
/// Given a <em>match hint</em> — a key for the element type — elements pair by that key, which is both
/// cheap and right. Without one, every element is compared against every other and the closest pairs
/// are taken first, which is quadratic and only a guess. Supplying hints is the single biggest thing a
/// caller can do for a diff over collections.
/// </para>
/// </remarks>
public class HollowEffigyCollectionPairer(
    HollowEffigy fromCollection, HollowEffigy toCollection, PrimaryKey? matchHint)
    : HollowEffigyFieldPairer(fromCollection, toCollection)
{
    /// <summary>
    /// The largest value any of the three fields packed into a matrix element can hold.
    /// </summary>
    /// <remarks>
    /// Three 21-bit fields in one long, so that the whole matrix can be sorted by score with one
    /// <see cref="Array.Sort(Array)"/> rather than by a comparator over objects. As a score it doubles
    /// as "do not pair these".
    /// </remarks>
    internal const int MaxMatrixElementFieldValue = 0x1FFFFF;

    /// <inheritdoc />
    public override IReadOnlyList<EffigyFieldPair> Pair() =>
        matchHint is null ? PairByMinDifference() : PairByMatchHint(matchHint);

    /// <summary>
    /// What to compare an element by, which for a map entry is its key rather than the whole entry.
    /// </summary>
    protected virtual HollowEffigy ComparisonEffigy(HollowEffigy effigy) => effigy;

    /// <summary>
    /// Pairs elements by the key the caller named.
    /// </summary>
    /// <remarks>
    /// Two things here do not follow Java, both because Java is wrong. Its probe does not stop once an
    /// element has been paired, so one left-hand element can be paired with several right-hand ones and
    /// appear on several rows. And its early return for an empty collection is missing on one of the
    /// two branches, so a collection that lost all its elements falls through and reports each of them
    /// twice. Both are handled here by letting every unpaired element fall out the same way.
    /// </remarks>
    private IReadOnlyList<EffigyFieldPair> PairByMatchHint(PrimaryKey hint)
    {
        List<EffigyFieldPair> pairs = [];

        if (From!.Fields.Count == 0 || To!.Fields.Count == 0)
        {
            AddUnmatchedElements(pairs, new BitSet(1), new BitSet(1));

            return pairs;
        }

        int[][] fromFieldPathIndexes = new int[hint.FieldCount][];
        int[][] toFieldPathIndexes = new int[hint.FieldCount][];

        for (int i = 0; i < hint.FieldCount; i++)
        {
            fromFieldPathIndexes[i] = hint.GetFieldPathIndex(From.DataAccess!.DataAccess, i);
            toFieldPathIndexes[i] = hint.GetFieldPathIndex(To.DataAccess!.DataAccess, i);
        }

        int[] hashedToFieldIndexes = new int[HashCodes.HashTableSize(To.Fields.Count)];
        Array.Fill(hashedToFieldIndexes, -1);

        for (int i = 0; i < To.Fields.Count; i++)
        {
            int hash = KeyHashCode(ElementAt(To, i), toFieldPathIndexes) & (hashedToFieldIndexes.Length - 1);

            while (hashedToFieldIndexes[hash] != -1)
            {
                hash = (hash + 1) & (hashedToFieldIndexes.Length - 1);
            }

            hashedToFieldIndexes[hash] = i;
        }

        BitSet matchedFromElements = new(From.Fields.Count);
        BitSet matchedToElements = new(To.Fields.Count);

        for (int i = 0; i < From.Fields.Count; i++)
        {
            HollowEffigy fromElement = ElementAt(From, i);
            int hash = KeyHashCode(fromElement, fromFieldPathIndexes) & (hashedToFieldIndexes.Length - 1);

            while (hashedToFieldIndexes[hash] != -1)
            {
                int toIndex = hashedToFieldIndexes[hash];

                if (!matchedToElements.Get(toIndex)
                    && KeysMatch(fromElement, ElementAt(To, toIndex), fromFieldPathIndexes, toFieldPathIndexes))
                {
                    pairs.Add(new EffigyFieldPair(From.Fields[i], To.Fields[toIndex], i, toIndex));
                    matchedFromElements.Set(i);
                    matchedToElements.Set(toIndex);

                    break;
                }

                hash = (hash + 1) & (hashedToFieldIndexes.Length - 1);
            }
        }

        AddUnmatchedElements(pairs, matchedFromElements, matchedToElements);

        return pairs;
    }

    /// <summary>
    /// Pairs elements by finding, repeatedly, the closest remaining pair.
    /// </summary>
    /// <remarks>
    /// The matrix of every-against-every is built at an increasing tolerance — first only near-identical
    /// pairs, then progressively looser — so that an obvious pairing is never stolen by a distant one
    /// that happened to sort first. Each pass abandons a comparison as soon as it exceeds the
    /// tolerance, which is what keeps the quadratic affordable.
    /// </remarks>
    private IReadOnlyList<EffigyFieldPair> PairByMinDifference()
    {
        List<EffigyFieldPair> pairs = [];

        BitSet pairedFromIndices = new(Math.Max(From!.Fields.Count, 1));
        BitSet pairedToIndices = new(Math.Max(To!.Fields.Count, 1));

        int[] maxDiffBackoff = [1, 2, 4, 8, int.MaxValue];
        int maxPairs = Math.Min(From.Fields.Count, To.Fields.Count);

        foreach (int maxDiff in maxDiffBackoff)
        {
            if (pairs.Count >= maxPairs)
            {
                break;
            }

            long[] diffMatrix = BuildDiffMatrix(pairedFromIndices, pairedToIndices, maxDiff);

            // Sorting the packed longs orders by score first, because the score is in the high bits.
            Array.Sort(diffMatrix);

            foreach (long element in diffMatrix)
            {
                if (pairs.Count == maxPairs)
                {
                    break;
                }

                // Sorted, so the first unpairable one means the rest are too.
                if (DiffScore(element) == MaxMatrixElementFieldValue)
                {
                    break;
                }

                int fromIndex = FromIndex(element);
                int toIndex = ToIndex(element);

                if (pairedFromIndices.Get(fromIndex) || pairedToIndices.Get(toIndex))
                {
                    continue;
                }

                pairs.Add(new EffigyFieldPair(From.Fields[fromIndex], To.Fields[toIndex], fromIndex, toIndex));
                pairedFromIndices.Set(fromIndex);
                pairedToIndices.Set(toIndex);
            }
        }

        AddUnmatchedElements(pairs, pairedFromIndices, pairedToIndices);

        return pairs;
    }

    private long[] BuildDiffMatrix(BitSet pairedFromIndices, BitSet pairedToIndices, int maxDiff)
    {
        long[] diffMatrix = new long[From!.Fields.Count * To!.Fields.Count];
        int index = 0;

        for (int fromIndex = 0; fromIndex < From.Fields.Count; fromIndex++)
        {
            if (pairedFromIndices.Get(fromIndex))
            {
                // Already spoken for, but it keeps its row so the matrix stays rectangular.
                for (int toIndex = 0; toIndex < To.Fields.Count; toIndex++)
                {
                    diffMatrix[index++] = MatrixElement(fromIndex, toIndex, MaxMatrixElementFieldValue);
                }

                continue;
            }

            // Built once per row, since it is asked about every candidate in that row.
            HollowEffigyDiffRecord diffRecord = new(ElementAt(From, fromIndex));

            for (int toIndex = 0; toIndex < To.Fields.Count; toIndex++)
            {
                int score = pairedToIndices.Get(toIndex)
                    ? MaxMatrixElementFieldValue
                    : diffRecord.CalculateDiff(ElementAt(To, toIndex), maxDiff);

                diffMatrix[index++] = MatrixElement(fromIndex, toIndex, score);
            }
        }

        return diffMatrix;
    }

    private void AddUnmatchedElements(
        List<EffigyFieldPair> pairs, BitSet pairedFromIndices, BitSet pairedToIndices)
    {
        for (int i = 0; i < From!.Fields.Count; i++)
        {
            if (!pairedFromIndices.Get(i))
            {
                pairs.Add(new EffigyFieldPair(From.Fields[i], null, i, -1));
            }
        }

        for (int i = 0; i < To!.Fields.Count; i++)
        {
            if (!pairedToIndices.Get(i))
            {
                pairs.Add(new EffigyFieldPair(null, To.Fields[i], -1, i));
            }
        }
    }

    private HollowEffigy ElementAt(HollowEffigy collection, int index) =>
        ComparisonEffigy((HollowEffigy)collection.Fields[index].Value!);

    private static int KeyHashCode(HollowEffigy element, int[][] fieldPathIndexes)
    {
        int hash = 0;

        foreach (int[] fieldPath in fieldPathIndexes)
        {
            hash *= 31;
            hash ^= FieldHashCode(element, fieldPath);
        }

        return hash;
    }

    private static int FieldHashCode(HollowEffigy element, int[] fieldPath)
    {
        (IHollowObjectTypeDataAccess dataAccess, int ordinal) = WalkTo(element, fieldPath);

        return HashCodes.HashInt(
            HollowReadFieldUtils.FieldHashCode(dataAccess, ordinal, fieldPath[^1]));
    }

    private static bool KeysMatch(
        HollowEffigy fromElement,
        HollowEffigy toElement,
        int[][] fromFieldPathIndexes,
        int[][] toFieldPathIndexes)
    {
        for (int i = 0; i < fromFieldPathIndexes.Length; i++)
        {
            (IHollowObjectTypeDataAccess fromAccess, int fromOrdinal) =
                WalkTo(fromElement, fromFieldPathIndexes[i]);

            (IHollowObjectTypeDataAccess toAccess, int toOrdinal) =
                WalkTo(toElement, toFieldPathIndexes[i]);

            if (!HollowReadFieldUtils.FieldsAreEqual(
                fromAccess, fromOrdinal, fromFieldPathIndexes[i][^1],
                toAccess, toOrdinal, toFieldPathIndexes[i][^1]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Follows all but the last step of <paramref name="fieldPath"/>.</summary>
    private static (IHollowObjectTypeDataAccess DataAccess, int Ordinal) WalkTo(
        HollowEffigy element, int[] fieldPath)
    {
        IHollowObjectTypeDataAccess dataAccess = (IHollowObjectTypeDataAccess)element.DataAccess!;
        int ordinal = element.Ordinal;

        for (int i = 0; i < fieldPath.Length - 1; i++)
        {
            HollowObjectSchema schema = dataAccess.Schema;
            int nextOrdinal = dataAccess.ReadOrdinal(ordinal, fieldPath[i]);

            dataAccess = (IHollowObjectTypeDataAccess)dataAccess.DataAccess.GetTypeDataAccess(
                schema.GetReferencedType(fieldPath[i])!)!;

            ordinal = nextOrdinal;
        }

        return (dataAccess, ordinal);
    }

    private static long MatrixElement(int fromIndex, int toIndex, int diffScore) =>
        ((long)diffScore << 42) | ((long)fromIndex << 21) | (uint)toIndex;

    private static int DiffScore(long element) => (int)((element >> 42) & MaxMatrixElementFieldValue);

    private static int FromIndex(long element) => (int)((element >> 21) & MaxMatrixElementFieldValue);

    private static int ToIndex(long element) => (int)(element & MaxMatrixElementFieldValue);
}

/// <summary>
/// Lines two maps' entries up, by their keys.
/// </summary>
/// <remarks>
/// A map entry's identity is its key, so that is what an entry is compared by — otherwise an entry
/// whose value changed would pair with nothing and read as a removal and an addition.
/// </remarks>
public sealed class HollowEffigyMapPairer(
    HollowEffigy fromCollection, HollowEffigy toCollection, PrimaryKey? matchHint)
    : HollowEffigyCollectionPairer(fromCollection, toCollection, matchHint)
{
    /// <inheritdoc />
    protected override HollowEffigy ComparisonEffigy(HollowEffigy effigy) =>
        (HollowEffigy)effigy.Fields[0].Value!;
}
