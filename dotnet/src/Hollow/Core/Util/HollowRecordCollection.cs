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

namespace Hollow.Core.Util;

/// <summary>
/// The records at a set of ordinals, read one at a time as they are asked for.
/// </summary>
/// <typeparam name="T">The record type.</typeparam>
/// <remarks>
/// <para>
/// Nothing is materialised: a set of a million ordinals costs a bit set until something enumerates it,
/// and enumerating it twice reads the records twice rather than holding them.
/// </para>
/// <para>
/// Java's <c>HollowRecordCollection</c> is abstract, with a <c>getForOrdinal</c> its subclasses
/// implement, because subclassing is how Java passes a function. Taking one is the same thing said
/// directly, and it means the type can be sealed.
/// </para>
/// </remarks>
/// <param name="ordinals">The ordinals to read, which the collection does not copy.</param>
/// <param name="getRecord">Reads the record at one ordinal.</param>
public sealed class HollowRecordCollection<T>(BitSet ordinals, Func<int, T> getRecord)
    : IReadOnlyCollection<T>
{
    private readonly BitSet _ordinals = ordinals ?? throw new ArgumentNullException(nameof(ordinals));

    private readonly Func<int, T> _getRecord =
        getRecord ?? throw new ArgumentNullException(nameof(getRecord));

    /// <inheritdoc />
    public int Count => _ordinals.Cardinality();

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator()
    {
        for (int ordinal = _ordinals.NextSetBit(0);
            ordinal != -1;
            ordinal = _ordinals.NextSetBit(ordinal + 1))
        {
            yield return _getRecord(ordinal);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
