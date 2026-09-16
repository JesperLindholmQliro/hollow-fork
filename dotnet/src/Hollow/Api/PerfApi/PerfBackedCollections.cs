/*
 *  Copyright 2021 Netflix, Inc.
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
using System.Diagnostics.CodeAnalysis;
using Hollow.Core;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Iterator;

namespace Hollow.Api.PerfApi;

/// <summary>
/// Shared between the collection views a performance API hands out.
/// </summary>
/// <remarks>
/// <para>
/// Java's views are <c>AbstractList</c>, <c>AbstractSet</c> and <c>AbstractMap</c> subclasses, whose
/// mutating methods throw <c>UnsupportedOperationException</c>. .NET has read-only collection
/// interfaces, so these implement those instead and a whole class of runtime failure goes away: there
/// is no <c>Add</c> to call.
/// </para>
/// <para>
/// Nothing is cached. Reading the same element twice builds the wrapper twice, which is the trade the
/// performance API exists to offer — a caller that wants caching has
/// <see cref="HollowPerfApiCache{T}"/>.
/// </para>
/// </remarks>
internal static class PerfBacked
{
    /// <summary>
    /// A hash key as the array a lookup wants.
    /// </summary>
    /// <remarks>
    /// Java's <c>HashKeyExtractor.extractArray</c>: a key over several fields is already an array, and
    /// a key over one is the value itself, because making every caller wrap a single field would be
    /// noise.
    /// </remarks>
    internal static object?[] AsHashKey(object? key) => key as object?[] ?? [key];
}

/// <summary>One list record, as a list.</summary>
internal sealed class PerfBackedList<T>(
    HollowListTypePerfApi typeApi, int ordinal, Func<HollowRef, T> instantiate) : IReadOnlyList<T>
{
    private readonly IHollowListTypeDataAccess _typeAccess = typeApi.TypeAccess;
    private readonly long _elementMaskedTypeIdentifier = typeApi.ElementMaskedTypeIdentifier;

    public int Count => _typeAccess.Size(ordinal);

    public T this[int index] =>
        instantiate(HollowRef.Create(
            _elementMaskedTypeIdentifier, _typeAccess.GetElementOrdinal(ordinal, index)));

    public IEnumerator<T> GetEnumerator()
    {
        foreach (int element in _typeAccess.ElementOrdinals(ordinal))
        {
            yield return instantiate(HollowRef.Create(_elementMaskedTypeIdentifier, element));
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>One set record, as a set.</summary>
/// <remarks>
/// The set-algebra methods are the straightforward implementations over enumeration, and are here
/// because <see cref="IReadOnlySet{T}"/> asks for them. The interesting one is <see cref="Contains"/>,
/// which probes the record's own hash table where the type declares a hash key.
/// </remarks>
internal sealed class PerfBackedSet<T>(
    HollowSetTypePerfApi typeApi,
    int ordinal,
    Func<HollowRef, T> instantiate,
    Func<T, object?>? extractHashKey) : IReadOnlySet<T>
{
    private readonly IHollowSetTypeDataAccess _typeAccess = typeApi.TypeAccess;
    private readonly long _elementMaskedTypeIdentifier = typeApi.ElementMaskedTypeIdentifier;

    public int Count => _typeAccess.Size(ordinal);

    public bool Contains(T item)
    {
        if (extractHashKey is null)
        {
            // No hash key declared, so there is nothing to probe the record's table with. Java throws
            // here; scanning answers the question correctly, only slower.
            return this.Any(element => EqualityComparer<T>.Default.Equals(element, item));
        }

        return item is not null
            && _typeAccess.FindElement(ordinal, PerfBacked.AsHashKey(extractHashKey(item)))
                != HollowConstants.OrdinalNone;
    }

    public IEnumerator<T> GetEnumerator()
    {
        foreach (int element in _typeAccess.ElementOrdinals(ordinal))
        {
            yield return instantiate(HollowRef.Create(_elementMaskedTypeIdentifier, element));
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool IsProperSubsetOf(IEnumerable<T> other) => AsSet().IsProperSubsetOf(other);

    public bool IsProperSupersetOf(IEnumerable<T> other) => AsSet().IsProperSupersetOf(other);

    public bool IsSubsetOf(IEnumerable<T> other) => AsSet().IsSubsetOf(other);

    public bool IsSupersetOf(IEnumerable<T> other) => AsSet().IsSupersetOf(other);

    public bool Overlaps(IEnumerable<T> other) => AsSet().Overlaps(other);

    public bool SetEquals(IEnumerable<T> other) => AsSet().SetEquals(other);

    private HashSet<T> AsSet() => [.. this];
}

/// <summary>One map record, as a dictionary.</summary>
internal sealed class PerfBackedMap<TKey, TValue>(
    HollowMapTypePerfApi typeApi,
    int ordinal,
    Func<HollowRef, TKey> instantiateKey,
    Func<HollowRef, TValue> instantiateValue,
    Func<TKey, object?>? extractHashKey) : IReadOnlyDictionary<TKey, TValue>
    where TKey : notnull
{
    private readonly IHollowMapTypeDataAccess _typeAccess = typeApi.TypeAccess;
    private readonly long _keyMaskedTypeIdentifier = typeApi.KeyMaskedTypeIdentifier;
    private readonly long _valueMaskedTypeIdentifier = typeApi.ValueMaskedTypeIdentifier;

    public int Count => _typeAccess.Size(ordinal);

    public IEnumerable<TKey> Keys => this.Select(entry => entry.Key);

    public IEnumerable<TValue> Values => this.Select(entry => entry.Value);

    public TValue this[TKey key] =>
        TryGetValue(key, out TValue? value)
            ? value
            : throw new KeyNotFoundException($"the map holds no entry for {key}");

    public bool ContainsKey(TKey key) => TryGetValue(key, out _);

    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (extractHashKey is null)
        {
            // As with the set: no hash key means no table to probe, so this scans rather than refusing.
            foreach (KeyValuePair<TKey, TValue> entry in this)
            {
                if (EqualityComparer<TKey>.Default.Equals(entry.Key, key))
                {
                    value = entry.Value;

                    return true;
                }
            }

            value = default;

            return false;
        }

        int valueOrdinal = _typeAccess.FindValue(ordinal, PerfBacked.AsHashKey(extractHashKey(key)));

        if (valueOrdinal == HollowConstants.OrdinalNone)
        {
            value = default;

            return false;
        }

        value = instantiateValue(HollowRef.Create(_valueMaskedTypeIdentifier, valueOrdinal));

        return true;
    }

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
    {
        foreach (HollowMapEntry entry in _typeAccess.Entries(ordinal))
        {
            yield return KeyValuePair.Create(
                instantiateKey(HollowRef.Create(_keyMaskedTypeIdentifier, entry.KeyOrdinal)),
                instantiateValue(HollowRef.Create(_valueMaskedTypeIdentifier, entry.ValueOrdinal)));
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
