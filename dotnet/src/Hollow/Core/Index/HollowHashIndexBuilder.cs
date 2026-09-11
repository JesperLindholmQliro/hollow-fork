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

using Hollow.Core.Index.Traversal;
using Hollow.Core.Memory;
using Hollow.Core.Memory.Encoding;
using Hollow.Core.Memory.Pool;
using Hollow.Core.Read;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Iterator;
using Hollow.Core.Util;

namespace Hollow.Core.Index;

/// <summary>
/// Builds the two tables a <see cref="HollowHashIndex"/> queries.
/// </summary>
/// <remarks>
/// <para>
/// The work happens in two passes. The first traverses every record, groups the matches by their key,
/// and collects each group's selected ordinals into a linked list — using a hash table that grows as
/// the number of distinct keys turns out to be more than guessed. The second turns those lists into
/// one flat select array of per-group hash tables, and the groups into one match table whose entries
/// point into it.
/// </para>
/// <para>
/// Not intended for use outside the index.
/// </para>
/// </remarks>
internal sealed class HollowHashIndexBuilder
{
    private readonly HollowPreindexer _preindexer;
    private readonly IArraySegmentRecycler _memoryRecycler = WastefulRecycler.DefaultInstance;

    private GrowingSegmentedLongArray _matchIndexHashAndSizeArray = null!;
    private FixedLengthElementArray _intermediateMatchHashTable = null!;
    private long _intermediateMatchHashTableSize;
    private int _bitsPerIntermediateListIdentifier;
    private int _bitsPerIntermediateMatchHashEntry;
    private long _intermediateMatchHashMask;
    private long _intermediateMatchHashTableSizeBeforeGrow;
    private int _matchCount;

    /// <summary>
    /// Resolves the field paths and sizes the key, without yet reading any records.
    /// </summary>
    /// <exception cref="FieldPathException">One of the paths cannot be bound.</exception>
    internal HollowHashIndexBuilder(
        IHollowDataAccess dataAccess, string type, string selectField, string[] matchFields)
    {
        _preindexer = new HollowPreindexer(dataAccess, type, selectField, matchFields);
        _preindexer.BuildFieldSpecifications();

        HollowIndexerValueTraverser traverser = _preindexer.Traverser;

        BitsPerTraverserField = new int[traverser.FieldPathCount];
        OffsetPerTraverserField = new int[traverser.FieldPathCount];

        int bitsPerMatchHashKey = 0;
        for (int i = 0; i < traverser.FieldPathCount; i++)
        {
            int maxOrdinal = traverser.GetFieldTypeDataAccess(i).TypeState.MaxOrdinal;
            BitsPerTraverserField[i] = IFixedLengthData.BitsRequiredToRepresentValue(maxOrdinal + 1);
            OffsetPerTraverserField[i] = bitsPerMatchHashKey;

            // Only the match fields make up the stored key; the select field's ordinal lives in the
            // select table instead.
            if (i < _preindexer.MatchTraverserFieldCount)
            {
                bitsPerMatchHashKey += BitsPerTraverserField[i];
            }
        }

        BitsPerMatchHashKey = bitsPerMatchHashKey;
        BitsPerSelectHashEntry = BitsPerTraverserField[_preindexer.SelectFieldSpec.BaseIteratorFieldIndex];
    }

    /// <summary>The width of one match key, in bits.</summary>
    internal int BitsPerMatchHashKey { get; }

    /// <summary>The width of one entry of a select table, in bits.</summary>
    internal int BitsPerSelectHashEntry { get; }

    /// <summary>The width of each traverser field's ordinal, in bits.</summary>
    internal int[] BitsPerTraverserField { get; }

    /// <summary>The bit offset of each traverser field within a match key.</summary>
    internal int[] OffsetPerTraverserField { get; }

    /// <summary>The finished match table.</summary>
    internal FixedLengthElementArray FinalMatchHashTable { get; private set; } = null!;

    /// <summary>The finished select table.</summary>
    internal FixedLengthElementArray FinalSelectHashArray { get; private set; } = null!;

    /// <summary>The mask that turns a hash into a match table bucket.</summary>
    internal long FinalMatchHashMask { get; private set; }

    /// <summary>The width of one match table entry, in bits.</summary>
    internal int FinalBitsPerMatchHashEntry { get; private set; }

    /// <summary>The width of a select table's size, in bits.</summary>
    internal int FinalBitsPerSelectTableSize { get; private set; }

    /// <summary>The width of a pointer into the select table, in bits.</summary>
    internal int FinalBitsPerSelectTablePointer { get; private set; }

    /// <summary>The resolved match fields.</summary>
    internal HollowHashIndexField[] MatchFields => _preindexer.MatchFieldSpecs;

    /// <summary>The resolved select field.</summary>
    internal HollowHashIndexField SelectField => _preindexer.SelectFieldSpec;

    /// <summary>The number of buckets a table may fill before it has to grow.</summary>
    internal static long SizeBeforeGrow(long hashTableSize) => hashTableSize * 7 / 10;

    /// <summary>The size to grow a full table to.</summary>
    /// <exception cref="InvalidOperationException">The table is already at its limit.</exception>
    internal static long GrownTableSize(long hashTableSize) =>
        hashTableSize > HollowConstants.IndexHashTableMaxSize
            ? throw new InvalidOperationException(
                $"cannot grow the intermediate match hash table beyond {hashTableSize.Invariant()} buckets; a hash "
                + $"index supports at most {HollowConstants.IndexHashTableMaxSize.Invariant()} matches")
            : hashTableSize * 2;

    /// <summary>
    /// Traverses every record of the indexed type and builds the tables.
    /// </summary>
    internal void BuildIndex()
    {
        _matchIndexHashAndSizeArray = new GrowingSegmentedLongArray(_memoryRecycler);

        BitSet populatedOrdinals = _preindexer.TypeDataAccess.TypeState.PopulatedOrdinals;

        // An initial guess: one match per record of the indexed type. The table grows if the paths turn
        // out to produce more distinct keys than that.
        _intermediateMatchHashTableSize = HashCodes.IndexHashTableSize(populatedOrdinals.Cardinality());
        _bitsPerIntermediateListIdentifier =
            IFixedLengthData.BitsRequiredToRepresentValue(_intermediateMatchHashTableSize - 1);
        _bitsPerIntermediateMatchHashEntry = BitsPerMatchHashKey + _bitsPerIntermediateListIdentifier;
        _intermediateMatchHashMask = _intermediateMatchHashTableSize - 1;
        _intermediateMatchHashTableSizeBeforeGrow = SizeBeforeGrow(_intermediateMatchHashTableSize);
        _matchCount = 0;

        _intermediateMatchHashTable = new FixedLengthElementArray(
            _memoryRecycler, _intermediateMatchHashTableSize * _bitsPerIntermediateMatchHashEntry);

        MultiLinkedElementArray intermediateSelectLists = new(_memoryRecycler);

        CollectMatches(populatedOrdinals, intermediateSelectLists);
        BuildFinalTables(intermediateSelectLists);

        intermediateSelectLists.Destroy();
        _intermediateMatchHashTable.Destroy(_memoryRecycler);
        _matchIndexHashAndSizeArray.Destroy();
        _memoryRecycler.Swap();
    }

    /// <summary>
    /// The first pass: group every record's matches by key, collecting each group's selected ordinals.
    /// </summary>
    private void CollectMatches(BitSet populatedOrdinals, MultiLinkedElementArray intermediateSelectLists)
    {
        HollowIndexerValueTraverser traverser = _preindexer.Traverser;
        int selectFieldIndex = _preindexer.SelectFieldSpec.BaseIteratorFieldIndex;

        foreach (int ordinal in populatedOrdinals.EnumerateSetBits())
        {
            traverser.Traverse(ordinal);

            for (int i = 0; i < traverser.MatchCount; i++)
            {
                int matchHash = GetMatchHash(i);

                long bucket = matchHash & _intermediateMatchHashMask;
                long hashBucketBit = bucket * _bitsPerIntermediateMatchHashEntry;

                // The first traverser field's ordinal is stored one higher, so zero means empty.
                bool bucketIsEmpty = _intermediateMatchHashTable.GetElementValue(
                    hashBucketBit, BitsPerTraverserField[0]) == 0;

                long bucketMatchListIndex = _intermediateMatchHashTable.GetElementValue(
                    hashBucketBit + BitsPerMatchHashKey, _bitsPerIntermediateListIdentifier);
                int bucketMatchHashCode = (int)_matchIndexHashAndSizeArray.Get(bucketMatchListIndex);

                // Compare the stored hash first: a full key comparison walks field paths.
                while (!bucketIsEmpty
                    && (bucketMatchHashCode != (matchHash & int.MaxValue)
                        || !IntermediateMatchIsEqual(i, hashBucketBit)))
                {
                    bucket = (bucket + 1) & _intermediateMatchHashMask;
                    hashBucketBit = bucket * _bitsPerIntermediateMatchHashEntry;
                    bucketIsEmpty = _intermediateMatchHashTable.GetElementValue(
                        hashBucketBit, BitsPerTraverserField[0]) == 0;
                    bucketMatchListIndex = _intermediateMatchHashTable.GetElementValue(
                        hashBucketBit + BitsPerMatchHashKey, _bitsPerIntermediateListIdentifier);
                    bucketMatchHashCode = (int)_matchIndexHashAndSizeArray.Get(bucketMatchListIndex);
                }

                int matchListIndex;

                if (bucketIsEmpty)
                {
                    matchListIndex = intermediateSelectLists.NewList();

                    for (int j = 0; j < _preindexer.MatchTraverserFieldCount; j++)
                    {
                        _intermediateMatchHashTable.SetElementValue(
                            hashBucketBit + OffsetPerTraverserField[j],
                            BitsPerTraverserField[j],
                            traverser.GetMatchOrdinal(i, j) + 1);
                    }

                    _intermediateMatchHashTable.SetElementValue(
                        hashBucketBit + BitsPerMatchHashKey, _bitsPerIntermediateListIdentifier, matchListIndex);

                    _matchIndexHashAndSizeArray.Set(matchListIndex, matchHash & int.MaxValue);
                    _matchCount++;

                    if (_matchCount > _intermediateMatchHashTableSizeBeforeGrow)
                    {
                        GrowIntermediateHashTable();
                    }
                }
                else
                {
                    matchListIndex = (int)_intermediateMatchHashTable.GetElementValue(
                        hashBucketBit + BitsPerMatchHashKey, _bitsPerIntermediateListIdentifier);
                }

                intermediateSelectLists.Add(matchListIndex, traverser.GetMatchOrdinal(i, selectFieldIndex));
            }
        }
    }

    /// <summary>
    /// The second pass: lay the grouped matches out as a match table pointing into one flat array of
    /// per-group select hash tables.
    /// </summary>
    private void BuildFinalTables(MultiLinkedElementArray intermediateSelectLists)
    {
        (long totalNumberOfSelectBuckets, int bitsPerSelectTableSize) =
            CalculateDedupedSizesAndTotalNumberOfSelectBuckets(intermediateSelectLists);

        long totalNumberOfMatchBuckets = HashCodes.IndexHashTableSize(_matchCount);

        int bitsPerFinalSelectBucketPointer =
            IFixedLengthData.BitsRequiredToRepresentValue(totalNumberOfSelectBuckets);
        int finalBitsPerMatchHashEntry =
            BitsPerMatchHashKey + bitsPerSelectTableSize + bitsPerFinalSelectBucketPointer;

        FixedLengthElementArray finalMatchArray = new(
            _memoryRecycler, totalNumberOfMatchBuckets * finalBitsPerMatchHashEntry);
        FixedLengthElementArray finalSelectArray = new(
            _memoryRecycler, totalNumberOfSelectBuckets * BitsPerSelectHashEntry);

        long finalMatchHashMask = totalNumberOfMatchBuckets - 1;
        long currentSelectArrayBucket = 0;

        for (int i = 0; i < _matchCount; i++)
        {
            long matchIndexHashAndSize = _matchIndexHashAndSizeArray.Get(i);
            int matchIndexSize = (int)(matchIndexHashAndSize >> 32);
            int matchIndexTableSize = HashCodes.HashTableSize(matchIndexSize);
            int matchIndexBucketMask = matchIndexTableSize - 1;

            FillSelectTable(
                finalSelectArray,
                intermediateSelectLists.Iterator(i),
                currentSelectArrayBucket,
                matchIndexBucketMask);

            long finalMatchIndexBucket = matchIndexHashAndSize & finalMatchHashMask;
            long finalMatchIndexBucketBit = finalMatchIndexBucket * finalBitsPerMatchHashEntry;

            while (finalMatchArray.GetElementValue(finalMatchIndexBucketBit, BitsPerTraverserField[0]) != 0)
            {
                finalMatchIndexBucket = (finalMatchIndexBucket + 1) & finalMatchHashMask;
                finalMatchIndexBucketBit = finalMatchIndexBucket * finalBitsPerMatchHashEntry;
            }

            long intermediateMatchIndexBucketBit = FindIntermediateBucket(matchIndexHashAndSize, i);

            CopyMatchHashKey(
                finalMatchArray, finalMatchIndexBucketBit, intermediateMatchIndexBucketBit);

            finalMatchArray.SetElementValue(
                finalMatchIndexBucketBit + BitsPerMatchHashKey, bitsPerSelectTableSize, matchIndexSize);
            finalMatchArray.SetElementValue(
                finalMatchIndexBucketBit + BitsPerMatchHashKey + bitsPerSelectTableSize,
                bitsPerFinalSelectBucketPointer,
                currentSelectArrayBucket);

            currentSelectArrayBucket += matchIndexTableSize;
        }

        FinalMatchHashTable = finalMatchArray;
        FinalSelectHashArray = finalSelectArray;
        FinalBitsPerMatchHashEntry = finalBitsPerMatchHashEntry;
        FinalBitsPerSelectTablePointer = bitsPerFinalSelectBucketPointer;
        FinalBitsPerSelectTableSize = bitsPerSelectTableSize;
        FinalMatchHashMask = finalMatchHashMask;
    }

    /// <summary>
    /// Hashes one group's selected ordinals into its own table within the flat select array.
    /// </summary>
    private void FillSelectTable(
        FixedLengthElementArray finalSelectArray,
        IHollowOrdinalIterator selectOrdinalIterator,
        long currentSelectArrayBucket,
        int matchIndexBucketMask)
    {
        for (int selectOrdinal = selectOrdinalIterator.Next();
            selectOrdinal != IHollowOrdinalIterator.NoMoreOrdinals;
            selectOrdinal = selectOrdinalIterator.Next())
        {
            int selectBucket = HashCodes.HashInt(selectOrdinal) & matchIndexBucketMask;
            int bucketOrdinal = ReadSelectBucket(
                finalSelectArray, currentSelectArrayBucket + selectBucket);

            // The same ordinal can be selected more than once by one key, so a repeat is dropped
            // rather than stored twice.
            while (bucketOrdinal != HollowConstants.OrdinalNone && bucketOrdinal != selectOrdinal)
            {
                selectBucket = (selectBucket + 1) & matchIndexBucketMask;
                bucketOrdinal = ReadSelectBucket(finalSelectArray, currentSelectArrayBucket + selectBucket);
            }

            if (bucketOrdinal == HollowConstants.OrdinalNone)
            {
                finalSelectArray.SetElementValue(
                    (currentSelectArrayBucket + selectBucket) * BitsPerSelectHashEntry,
                    BitsPerSelectHashEntry,
                    selectOrdinal + 1);
            }
        }
    }

    private int ReadSelectBucket(FixedLengthElementArray selectArray, long bucket) =>
        (int)selectArray.GetElementValue(bucket * BitsPerSelectHashEntry, BitsPerSelectHashEntry) - 1;

    /// <summary>
    /// Finds where the intermediate table put the group with list index <paramref name="listIndex"/>,
    /// which is where its key still lives.
    /// </summary>
    private long FindIntermediateBucket(long matchIndexHashAndSize, int listIndex)
    {
        long bucket = matchIndexHashAndSize & _intermediateMatchHashMask;
        long bucketBit = bucket * _bitsPerIntermediateMatchHashEntry;

        while (_intermediateMatchHashTable.GetElementValue(
            bucketBit + BitsPerMatchHashKey, _bitsPerIntermediateListIdentifier) != listIndex)
        {
            bucket = (bucket + 1) & _intermediateMatchHashMask;
            bucketBit = bucket * _bitsPerIntermediateMatchHashEntry;
        }

        return bucketBit;
    }

    /// <summary>
    /// Copies a key between tables, element-wise when it fits in one and bit-wise when it does not.
    /// </summary>
    private void CopyMatchHashKey(FixedLengthElementArray target, long targetBit, long sourceBit)
    {
        if (BitsPerMatchHashKey < 56)
        {
            target.SetElementValue(
                targetBit,
                BitsPerMatchHashKey,
                _intermediateMatchHashTable.GetElementValue(sourceBit, BitsPerMatchHashKey));
        }
        else
        {
            target.CopyBits(_intermediateMatchHashTable, sourceBit, targetBit, BitsPerMatchHashKey);
        }
    }

    /// <summary>
    /// Rebuilds the intermediate table at twice the size, rehashing the groups already in it.
    /// </summary>
    private void GrowIntermediateHashTable()
    {
        long newMatchHashTableSize = GrownTableSize(_intermediateMatchHashTableSize);
        long newMatchHashMask = newMatchHashTableSize - 1;
        int newBitsForListIdentifier =
            IFixedLengthData.BitsRequiredToRepresentValue(newMatchHashTableSize - 1);
        int newBitsPerMatchHashEntry = BitsPerMatchHashKey + newBitsForListIdentifier;

        FixedLengthElementArray newMatchHashTable = new(
            _memoryRecycler, newMatchHashTableSize * newBitsPerMatchHashEntry);

        for (int j = 0; j < _matchCount; j++)
        {
            int rehashCode = (int)_matchIndexHashAndSizeArray.Get(j);
            long oldHashBucketBit = FindIntermediateBucket(rehashCode, j);

            long rehashBucket = rehashCode & newMatchHashMask;
            long rehashBucketBit = rehashBucket * newBitsPerMatchHashEntry;

            while (newMatchHashTable.GetElementValue(rehashBucketBit, BitsPerTraverserField[0]) != 0)
            {
                rehashBucket = (rehashBucket + 1) & newMatchHashMask;
                rehashBucketBit = rehashBucket * newBitsPerMatchHashEntry;
            }

            CopyMatchHashKey(newMatchHashTable, rehashBucketBit, oldHashBucketBit);

            long listIndex = _intermediateMatchHashTable.GetElementValue(
                oldHashBucketBit + BitsPerMatchHashKey, _bitsPerIntermediateListIdentifier);

            newMatchHashTable.SetElementValue(
                rehashBucketBit + BitsPerMatchHashKey, newBitsForListIdentifier, listIndex);
        }

        _intermediateMatchHashTable.Destroy(_memoryRecycler);
        _memoryRecycler.Swap();

        _intermediateMatchHashTable = newMatchHashTable;
        _intermediateMatchHashTableSize = newMatchHashTableSize;
        _intermediateMatchHashTableSizeBeforeGrow = SizeBeforeGrow(newMatchHashTableSize);
        _bitsPerIntermediateListIdentifier = newBitsForListIdentifier;
        _bitsPerIntermediateMatchHashEntry = newBitsPerMatchHashEntry;
        _intermediateMatchHashMask = newMatchHashMask;
    }

    /// <summary>
    /// Counts each group's distinct selected ordinals, recording the size alongside its hash, and
    /// returns how many select buckets they need in total and how wide a group's size must be.
    /// </summary>
    private (long TotalBuckets, int BitsPerSelectTableSize) CalculateDedupedSizesAndTotalNumberOfSelectBuckets(
        MultiLinkedElementArray elementArray)
    {
        long totalBuckets = 0;
        long maxSize = 0;
        int[] selectArray = new int[8];

        for (int i = 0; i < elementArray.ListCount; i++)
        {
            int listSize = elementArray.ListSize(i);
            int predictedBuckets = HashCodes.HashTableSize(listSize);
            int hashMask = predictedBuckets - 1;

            if (predictedBuckets > selectArray.Length)
            {
                selectArray = new int[predictedBuckets];
            }

            Array.Fill(selectArray, -1, 0, predictedBuckets);

            int setSize = 0;
            IHollowOrdinalIterator iterator = elementArray.Iterator(i);

            for (int selectOrdinal = iterator.Next();
                selectOrdinal != IHollowOrdinalIterator.NoMoreOrdinals;
                selectOrdinal = iterator.Next())
            {
                int bucket = HashCodes.HashInt(selectOrdinal) & hashMask;

                while (true)
                {
                    if (selectArray[bucket] == selectOrdinal)
                    {
                        break;
                    }

                    if (selectArray[bucket] == -1)
                    {
                        selectArray[bucket] = selectOrdinal;
                        setSize++;
                        break;
                    }

                    bucket = (bucket + 1) & hashMask;
                }
            }

            // The size shares a word with the hash, in the high 32 bits.
            _matchIndexHashAndSizeArray.Set(i, _matchIndexHashAndSizeArray.Get(i) | ((long)setSize << 32));

            totalBuckets += HashCodes.HashTableSize(setSize);
            maxSize = Math.Max(maxSize, setSize);
        }

        return (totalBuckets, IFixedLengthData.BitsRequiredToRepresentValue(maxSize));
    }

    /// <summary>
    /// Whether the match at <paramref name="matchIndex"/> has the same key as the group stored at
    /// <paramref name="hashBucketBit"/>.
    /// </summary>
    private bool IntermediateMatchIsEqual(int matchIndex, long hashBucketBit)
    {
        for (int i = 0; i < _preindexer.MatchFieldSpecs.Length; i++)
        {
            HollowHashIndexField field = _preindexer.MatchFieldSpecs[i];

            int matchOrdinal =
                _preindexer.Traverser.GetMatchOrdinal(matchIndex, field.BaseIteratorFieldIndex);
            int hashOrdinal = (int)_intermediateMatchHashTable.GetElementValue(
                hashBucketBit + OffsetPerTraverserField[field.BaseIteratorFieldIndex],
                BitsPerTraverserField[field.BaseIteratorFieldIndex]) - 1;

            HollowHashIndexField.FieldPathSegment[] fieldPath = field.SchemaFieldPositionPath;

            if (fieldPath.Length == 0)
            {
                if (matchOrdinal != hashOrdinal)
                {
                    return false;
                }

                continue;
            }

            for (int j = 0; j < fieldPath.Length - 1; j++)
            {
                if (matchOrdinal != HollowConstants.OrdinalNone)
                {
                    matchOrdinal = fieldPath[j].GetOrdinalForField(matchOrdinal);
                }

                if (hashOrdinal != HollowConstants.OrdinalNone)
                {
                    hashOrdinal = fieldPath[j].GetOrdinalForField(hashOrdinal);
                }
            }

            // The same record trivially has the same value; otherwise the values have to be compared,
            // and a null on either side cannot be read.
            if (matchOrdinal == hashOrdinal)
            {
                continue;
            }

            HollowHashIndexField.FieldPathSegment last = fieldPath[^1];

            if (matchOrdinal == HollowConstants.OrdinalNone
                || hashOrdinal == HollowConstants.OrdinalNone
                || !HollowReadFieldUtils.FieldsAreEqual(
                    last.ObjectTypeDataAccess,
                    matchOrdinal,
                    last.SegmentFieldPosition,
                    last.ObjectTypeDataAccess,
                    hashOrdinal,
                    last.SegmentFieldPosition))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Hashes the key of the match at <paramref name="matchIndex"/>.
    /// </summary>
    private int GetMatchHash(int matchIndex)
    {
        int matchHash = 0;

        foreach (HollowHashIndexField field in _preindexer.MatchFieldSpecs)
        {
            int ordinal = _preindexer.Traverser.GetMatchOrdinal(matchIndex, field.BaseIteratorFieldIndex);
            HollowHashIndexField.FieldPathSegment[] fieldPath = field.SchemaFieldPositionPath;

            if (fieldPath.Length == 0)
            {
                matchHash ^= HashCodes.HashInt(ordinal);
                continue;
            }

            for (int j = 0; j < fieldPath.Length - 1; j++)
            {
                ordinal = fieldPath[j].GetOrdinalForField(ordinal);

                if (ordinal == HollowConstants.OrdinalNone)
                {
                    break;
                }
            }

            HollowHashIndexField.FieldPathSegment last = field.LastFieldPositionPathElement;

            int fieldHashCode = ordinal == HollowConstants.OrdinalNone
                ? HollowConstants.OrdinalNone
                : HollowReadFieldUtils.FieldHashCode(
                    last.ObjectTypeDataAccess, ordinal, last.SegmentFieldPosition);

            matchHash ^= HashCodes.HashInt(fieldHashCode);
        }

        return matchHash;
    }
}
