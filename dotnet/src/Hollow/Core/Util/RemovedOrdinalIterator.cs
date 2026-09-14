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

using Hollow.Core.Read.Engine;

namespace Hollow.Core.Util;

/// <summary>
/// The ordinals a delta transition removed: populated before, and not after.
/// </summary>
/// <remarks>
/// <para>
/// Hollow does not record removals anywhere; it records which ordinals are populated now and which
/// were populated before, and the difference is the removals. This walks that difference without
/// materialising it.
/// </para>
/// <para>
/// Named <c>RemovedOrdinalIterator</c> in Java, with a <c>next()</c> returning
/// <see cref="HollowConstants.OrdinalNone"/> at the end. This keeps that shape — the history's
/// callers are written around it — and adds <see cref="Enumerate"/> for everything else.
/// </para>
/// </remarks>
public sealed class RemovedOrdinalIterator
{
    private readonly BitSet _previousOrdinals;
    private readonly BitSet _populatedOrdinals;
    private readonly int _previousOrdinalsLength;

    private int _ordinal = HollowConstants.OrdinalNone;

    /// <summary>Walks what <paramref name="listener"/> saw removed in the last transition.</summary>
    /// <param name="listener">Where the two bit sets come from.</param>
    /// <param name="flip">
    /// Swaps the two, so the walk yields what was <em>added</em> rather than removed.
    /// </param>
    public RemovedOrdinalIterator(PopulatedOrdinalListener listener, bool flip = false)
        : this(
            (listener ?? throw new ArgumentNullException(nameof(listener))).PreviousOrdinals,
            listener.PopulatedOrdinals,
            flip)
    {
    }

    /// <summary>
    /// Walks the ordinals set in <paramref name="previousOrdinals"/> and clear in
    /// <paramref name="populatedOrdinals"/>.
    /// </summary>
    public RemovedOrdinalIterator(BitSet previousOrdinals, BitSet populatedOrdinals, bool flip = false)
    {
        ArgumentNullException.ThrowIfNull(previousOrdinals);
        ArgumentNullException.ThrowIfNull(populatedOrdinals);

        (_previousOrdinals, _populatedOrdinals) =
            flip ? (populatedOrdinals, previousOrdinals) : (previousOrdinals, populatedOrdinals);

        _previousOrdinalsLength = _previousOrdinals.Length;
    }

    /// <summary>
    /// The next removed ordinal, or <see cref="HollowConstants.OrdinalNone"/> once there are none
    /// left.
    /// </summary>
    public int Next()
    {
        while (_ordinal < _previousOrdinalsLength)
        {
            _ordinal = _populatedOrdinals.NextClearBit(_ordinal + 1);

            if (_previousOrdinals.Get(_ordinal))
            {
                return _ordinal;
            }
        }

        return HollowConstants.OrdinalNone;
    }

    /// <summary>Starts the walk again from the beginning.</summary>
    public void Reset() => _ordinal = HollowConstants.OrdinalNone;

    /// <summary>
    /// How many ordinals the walk would yield, leaving the position where it found it.
    /// </summary>
    public int CountTotal()
    {
        int bookmark = _ordinal;

        Reset();

        int count = 0;

        while (Next() != HollowConstants.OrdinalNone)
        {
            count++;
        }

        _ordinal = bookmark;

        return count;
    }

    /// <summary>
    /// The removed ordinals as a sequence, for the callers that would rather write a
    /// <c>foreach</c> than a sentinel loop.
    /// </summary>
    /// <remarks>Starts from wherever the walk currently is, and leaves it at the end.</remarks>
    public IEnumerable<int> Enumerate()
    {
        for (int ordinal = Next(); ordinal != HollowConstants.OrdinalNone; ordinal = Next())
        {
            yield return ordinal;
        }
    }
}
