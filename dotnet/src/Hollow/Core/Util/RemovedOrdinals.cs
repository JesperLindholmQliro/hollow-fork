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

using System.Collections;

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
/// Named <c>RemovedOrdinalIterator</c> in Java, where it is a cursor: a <c>next()</c> returning
/// <see cref="HollowConstants.OrdinalNone"/> at the end, a <c>reset()</c> to walk it again, and a
/// <c>countTotal()</c> that walks it and puts the cursor back. A sequence needs none of those —
/// enumerating it a second time starts a second walk, and <see cref="Enumerable.Count{T}(IEnumerable{T})"/>
/// counts it.
/// </para>
/// </remarks>
public sealed class RemovedOrdinals : IEnumerable<int>
{
    private readonly BitSet _previousOrdinals;
    private readonly BitSet _populatedOrdinals;
    private readonly int _previousOrdinalsLength;

    /// <summary>Walks what <paramref name="listener"/> saw removed in the last transition.</summary>
    /// <param name="listener">Where the two bit sets come from.</param>
    /// <param name="flip">
    /// Swaps the two, so the walk yields what was <em>added</em> rather than removed.
    /// </param>
    public RemovedOrdinals(PopulatedOrdinalListener listener, bool flip = false)
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
    public RemovedOrdinals(BitSet previousOrdinals, BitSet populatedOrdinals, bool flip = false)
    {
        ArgumentNullException.ThrowIfNull(previousOrdinals);
        ArgumentNullException.ThrowIfNull(populatedOrdinals);

        (_previousOrdinals, _populatedOrdinals) =
            flip ? (populatedOrdinals, previousOrdinals) : (previousOrdinals, populatedOrdinals);

        // Java fixes the end of the walk when the cursor is made, not when it runs. Keep that: the
        // two bit sets belong to a listener that the next transition will write to.
        _previousOrdinalsLength = _previousOrdinals.Length;
    }

    /// <inheritdoc />
    public IEnumerator<int> GetEnumerator()
    {
        int ordinal = HollowConstants.OrdinalNone;

        while (ordinal < _previousOrdinalsLength)
        {
            ordinal = _populatedOrdinals.NextClearBit(ordinal + 1);

            if (_previousOrdinals.Get(ordinal))
            {
                yield return ordinal;
            }
        }
    }

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
