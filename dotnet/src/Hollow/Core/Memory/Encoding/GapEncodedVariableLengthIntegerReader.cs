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

using System.Numerics;
using Hollow.Core.Memory.Pool;
using Hollow.Core.Read;
using Hollow.Core.Write;

namespace Hollow.Core.Memory.Encoding;

/// <summary>
/// Reads an ascending sequence of ordinals stored as the gaps between them, each gap a variable-length
/// integer.
/// </summary>
/// <remarks>
/// This is how a delta records which ordinals it adds and which it removes: the ordinals are ascending,
/// so storing differences keeps them small and the variable-length encoding then keeps them to a byte
/// or two each.
/// </remarks>
public class GapEncodedVariableLengthIntegerReader
{
    /// <summary>A reader over no ordinals at all.</summary>
    public static readonly GapEncodedVariableLengthIntegerReader EmptyReader = new EmptyGapEncodedReader();

    private readonly SegmentedByteArray? _data;
    private readonly int _numBytes;
    private int _currentPosition;
    private int _nextElement;
    private int _elementIndex;

    /// <summary>
    /// Initialises a reader over <paramref name="numBytes"/> bytes of gap-encoded ordinals.
    /// </summary>
    public GapEncodedVariableLengthIntegerReader(SegmentedByteArray? data, int numBytes)
    {
        _data = data;
        _numBytes = numBytes;
        Reset();
    }

    /// <summary>Whether this reader holds no ordinals.</summary>
    public bool IsEmpty => _numBytes == 0;

    /// <summary>
    /// The current ordinal, or <see cref="int.MaxValue"/> once the sequence is exhausted.
    /// </summary>
    public virtual int NextElement() => _nextElement;

    /// <summary>The index of the current ordinal within the sequence.</summary>
    public int ElementIndex => _elementIndex;

    /// <summary>Advances to the next ordinal.</summary>
    public virtual void Advance()
    {
        if (_currentPosition == _numBytes)
        {
            _nextElement = int.MaxValue;
        }
        else
        {
            int nextElementDelta = VarInt.ReadVInt(_data!, _currentPosition);
            _currentPosition += VarInt.SizeOfVInt(nextElementDelta);
            _nextElement += nextElementDelta;
            _elementIndex++;
        }
    }

    /// <summary>Rewinds to the first ordinal.</summary>
    public void Reset()
    {
        _currentPosition = 0;
        _elementIndex = -1;
        _nextElement = 0;
        Advance();
    }

    /// <summary>
    /// Counts the ordinals remaining, consuming the reader. Call <see cref="Reset"/> to read again.
    /// </summary>
    public int RemainingElements()
    {
        int count = 0;
        while (NextElement() != int.MaxValue)
        {
            count++;
            Advance();
        }

        return count;
    }

    /// <summary>Enumerates every ordinal from the start, leaving the reader exhausted.</summary>
    public IEnumerable<int> EnumerateOrdinals()
    {
        Reset();
        for (int ordinal = NextElement(); ordinal != int.MaxValue; ordinal = NextElement())
        {
            yield return ordinal;
            Advance();
        }
    }

    /// <summary>Returns this reader's storage to its recycler.</summary>
    public void Destroy() => _data?.Destroy();

    /// <summary>
    /// Divides this sequence the way a type's records are divided when its shard count grows: ordinal
    /// <c>o</c> goes to split <c>o &amp; (numSplits - 1)</c> as <c>o &gt;&gt; log2(numSplits)</c>.
    /// </summary>
    /// <remarks>Consumes this reader; call <see cref="Reset"/> to read it again.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="numSplits"/> is not a positive power of two.
    /// </exception>
    public GapEncodedVariableLengthIntegerReader[] Split(int numSplits)
    {
        if (numSplits <= 0 || (numSplits & (numSplits - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(numSplits), numSplits, "A sequence can only be split by a power of two.");
        }

        int toMask = numSplits - 1;
        int toOrdinalShift = BitOperations.TrailingZeroCount((uint)numSplits);

        ByteDataArray?[] splitOrdinals = new ByteDataArray?[numSplits];
        int[] previousSplitOrdinal = new int[numSplits];

        foreach (int ordinal in EnumerateOrdinals())
        {
            int toIndex = ordinal & toMask;
            int toOrdinal = ordinal >> toOrdinalShift;

            splitOrdinals[toIndex] ??= new ByteDataArray(WastefulRecycler.DefaultInstance);

            VarInt.WriteVInt(splitOrdinals[toIndex]!, toOrdinal - previousSplitOrdinal[toIndex]);
            previousSplitOrdinal[toIndex] = toOrdinal;
        }

        return [.. splitOrdinals.Select(FromOrdinals)];
    }

    /// <summary>
    /// Merges sequences the way a type's records are merged when its shard count shrinks: ordinal
    /// <c>o</c> of source <c>i</c> becomes <c>(o * n) + i</c>.
    /// </summary>
    /// <remarks>Consumes the readers; call <see cref="Reset"/> on one to read it again.</remarks>
    /// <exception cref="ArgumentException">
    /// The number of sequences is not a positive power of two.
    /// </exception>
    public static GapEncodedVariableLengthIntegerReader Join(GapEncodedVariableLengthIntegerReader?[] from)
    {
        ArgumentNullException.ThrowIfNull(from);

        if (from.Length <= 0 || (from.Length & (from.Length - 1)) != 0)
        {
            throw new ArgumentException(
                "Sequences can only be joined a power of two at a time.", nameof(from));
        }

        HashSet<int>[] fromOrdinals = new HashSet<int>[from.Length];
        int joinedMaxOrdinal = -1;

        for (int i = 0; i < from.Length; i++)
        {
            fromOrdinals[i] = [];

            if (from[i] is not { } reader)
            {
                continue;
            }

            foreach (int ordinal in reader.EnumerateOrdinals())
            {
                fromOrdinals[i].Add(ordinal);
                joinedMaxOrdinal = Math.Max(joinedMaxOrdinal, (ordinal * from.Length) + i);
            }
        }

        int fromMask = from.Length - 1;
        int fromOrdinalShift = BitOperations.TrailingZeroCount((uint)from.Length);

        ByteDataArray? joined = null;
        int previousOrdinal = 0;

        for (int ordinal = 0; ordinal <= joinedMaxOrdinal; ordinal++)
        {
            if (!fromOrdinals[ordinal & fromMask].Contains(ordinal >> fromOrdinalShift))
            {
                continue;
            }

            joined ??= new ByteDataArray(WastefulRecycler.DefaultInstance);

            VarInt.WriteVInt(joined, ordinal - previousOrdinal);
            previousOrdinal = ordinal;
        }

        return FromOrdinals(joined);
    }

    private static GapEncodedVariableLengthIntegerReader FromOrdinals(ByteDataArray? ordinals) =>
        ordinals is null
            ? EmptyReader
            : new GapEncodedVariableLengthIntegerReader(ordinals.UnderlyingArray, (int)ordinals.Length);

    /// <summary>
    /// Writes this sequence, preceded by its length in bytes.
    /// </summary>
    public void WriteTo(HollowBlobOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);

        VarInt.WriteVInt(output, _numBytes);
        if (_numBytes > 0)
        {
            _data!.WriteTo(output.Stream, 0, _numBytes);
        }
    }

    /// <summary>
    /// Reads a gap-encoded ordinal sequence from a delta blob.
    /// </summary>
    public static GapEncodedVariableLengthIntegerReader ReadEncodedDeltaOrdinals(
        HollowBlobInput input, IArraySegmentRecycler memoryRecycler)
    {
        ArgumentNullException.ThrowIfNull(input);

        SegmentedByteArray data = new(memoryRecycler);
        long numBytes = VarInt.ReadVLong(input);
        data.LoadFrom(input, numBytes);

        return new GapEncodedVariableLengthIntegerReader(data, (int)numBytes);
    }

    /// <summary>
    /// Skips a gap-encoded ordinal sequence without materialising it.
    /// </summary>
    public static void DiscardEncodedDeltaOrdinals(HollowBlobInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        long numBytesToSkip = VarInt.ReadVLong(input);
        while (numBytesToSkip > 0)
        {
            long skipped = input.SkipBytes(numBytesToSkip);
            if (skipped <= 0)
            {
                throw new EndOfStreamException("unexpected end of gap-encoded ordinals");
            }

            numBytesToSkip -= skipped;
        }
    }

    /// <summary>
    /// The always-exhausted reader used where a delta has no removals.
    /// </summary>
    private sealed class EmptyGapEncodedReader() : GapEncodedVariableLengthIntegerReader(null, 0)
    {
        public override int NextElement() => int.MaxValue;

        public override void Advance()
        {
            // Already exhausted.
        }
    }
}
