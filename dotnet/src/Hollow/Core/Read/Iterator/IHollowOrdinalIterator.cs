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

namespace Hollow.Core.Read.Iterator;

/// <summary>
/// Iterates the ordinals of the elements of a collection record.
/// </summary>
/// <remarks>
/// Named <c>HollowOrdinalIterator</c> in Java; the <c>I</c> prefix follows the .NET interface naming
/// convention. This is deliberately not <see cref="IEnumerator{T}"/>: the sentinel-terminated
/// <see cref="Next"/> avoids boxing an ordinal and allocating an enumerator on hot read paths. Use
/// <see cref="OrdinalIteratorExtensions.AsEnumerable"/> when a sequence is more convenient.
/// </remarks>
public interface IHollowOrdinalIterator
{
    /// <summary>The value <see cref="Next"/> returns once the iteration is exhausted.</summary>
    const int NoMoreOrdinals = int.MaxValue;

    /// <summary>
    /// Returns the next ordinal, or <see cref="NoMoreOrdinals"/> when the iteration is exhausted.
    /// </summary>
    int Next();
}

/// <summary>
/// Bridges <see cref="IHollowOrdinalIterator"/> to LINQ and <c>foreach</c>.
/// </summary>
public static class OrdinalIteratorExtensions
{
    /// <summary>
    /// Enumerates the remaining ordinals of <paramref name="iterator"/>.
    /// </summary>
    public static IEnumerable<int> AsEnumerable(this IHollowOrdinalIterator iterator)
    {
        ArgumentNullException.ThrowIfNull(iterator);

        for (int ordinal = iterator.Next();
            ordinal != IHollowOrdinalIterator.NoMoreOrdinals;
            ordinal = iterator.Next())
        {
            yield return ordinal;
        }
    }
}

/// <summary>
/// An iterator over no ordinals at all.
/// </summary>
public sealed class EmptyOrdinalIterator : IHollowOrdinalIterator
{
    /// <summary>The shared instance.</summary>
    public static readonly EmptyOrdinalIterator Instance = new();

    private EmptyOrdinalIterator()
    {
    }

    /// <inheritdoc />
    public int Next() => IHollowOrdinalIterator.NoMoreOrdinals;
}

/// <summary>
/// Iterates the key and value ordinals of the entries of a map record.
/// </summary>
/// <remarks>
/// Named <c>HollowMapEntryOrdinalIterator</c> in Java; the <c>I</c> prefix follows the .NET interface
/// naming convention.
/// </remarks>
public interface IHollowMapEntryOrdinalIterator
{
    /// <summary>
    /// Advances to the next entry, returning <see langword="false"/> when the iteration is exhausted.
    /// </summary>
    bool Next();

    /// <summary>The ordinal of the current entry's key.</summary>
    int Key { get; }

    /// <summary>The ordinal of the current entry's value.</summary>
    int Value { get; }
}

/// <summary>
/// An iterator over no map entries at all.
/// </summary>
public sealed class EmptyMapOrdinalIterator : IHollowMapEntryOrdinalIterator
{
    /// <summary>The shared instance.</summary>
    public static readonly EmptyMapOrdinalIterator Instance = new();

    private EmptyMapOrdinalIterator()
    {
    }

    /// <inheritdoc />
    public int Key => -1;

    /// <inheritdoc />
    public int Value => -1;

    /// <inheritdoc />
    public bool Next() => false;
}
