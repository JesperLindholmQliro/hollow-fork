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

using Hollow.Api.Custom;
using Hollow.Core;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Iterator;
using Hollow.Core.Schema;

namespace Hollow.Api.Objects.Delegate;

/// <summary>
/// Reads the elements of a list record.
/// </summary>
/// <typeparam name="T">The wrapper type an element is read as.</typeparam>
public interface IHollowListDelegate<T> : IHollowRecordDelegate
{
    /// <summary>The schema of the type this delegate reads.</summary>
    HollowListSchema Schema { get; }

    /// <summary>Read access to the type's records.</summary>
    IHollowListTypeDataAccess TypeDataAccess { get; }

    /// <summary>The type API this delegate belongs to, if it was built through one.</summary>
    HollowListTypeApi? TypeApi { get; }

    /// <summary>The number of elements in the given record.</summary>
    int Size(int ordinal);

    /// <summary>The element at <paramref name="index"/>.</summary>
    T GetElement(HollowList<T> list, int ordinal, int index);

    /// <summary>The index of the first element equal to <paramref name="item"/>, or -1.</summary>
    int IndexOf(HollowList<T> list, int ordinal, object? item);

    /// <summary>The index of the last element equal to <paramref name="item"/>, or -1.</summary>
    int LastIndexOf(HollowList<T> list, int ordinal, object? item);
}

/// <summary>
/// Reads a list record's elements straight out of the blob.
/// </summary>
public sealed class HollowListLookupDelegate<T> : IHollowListDelegate<T>
{
    /// <summary>Reads the records of <paramref name="typeDataAccess"/>.</summary>
    public HollowListLookupDelegate(IHollowListTypeDataAccess typeDataAccess)
    {
        ArgumentNullException.ThrowIfNull(typeDataAccess);

        TypeDataAccess = typeDataAccess;
    }

    /// <summary>Reads the records <paramref name="typeApi"/> covers.</summary>
    public HollowListLookupDelegate(HollowListTypeApi typeApi)
    {
        ArgumentNullException.ThrowIfNull(typeApi);

        TypeDataAccess = typeApi.TypeDataAccess;
        TypeApi = typeApi;
    }

    /// <inheritdoc />
    public HollowListSchema Schema => TypeDataAccess.Schema;

    /// <inheritdoc />
    public IHollowListTypeDataAccess TypeDataAccess { get; }

    /// <inheritdoc />
    public HollowListTypeApi? TypeApi { get; }

    /// <inheritdoc />
    public int Size(int ordinal) => TypeDataAccess.Size(ordinal);

    /// <inheritdoc />
    public T GetElement(HollowList<T> list, int ordinal, int index)
    {
        ArgumentNullException.ThrowIfNull(list);

        return list.InstantiateElement(TypeDataAccess.GetElementOrdinal(ordinal, index));
    }

    /// <inheritdoc />
    public int IndexOf(HollowList<T> list, int ordinal, object? item)
    {
        ArgumentNullException.ThrowIfNull(list);

        int size = Size(ordinal);
        for (int i = 0; i < size; i++)
        {
            if (list.EqualsElement(TypeDataAccess.GetElementOrdinal(ordinal, i), item))
            {
                return i;
            }
        }

        return -1;
    }

    /// <inheritdoc />
    public int LastIndexOf(HollowList<T> list, int ordinal, object? item)
    {
        ArgumentNullException.ThrowIfNull(list);

        for (int i = Size(ordinal) - 1; i >= 0; i--)
        {
            if (list.EqualsElement(TypeDataAccess.GetElementOrdinal(ordinal, i), item))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>
/// Holds one list record's element ordinals, so that reading them again costs nothing.
/// </summary>
public sealed class HollowListCachedDelegate<T> : IHollowListDelegate<T>, IHollowCachedDelegate
{
    private readonly int[] _elementOrdinals;

    /// <summary>Caches the record at <paramref name="ordinal"/>.</summary>
    public HollowListCachedDelegate(IHollowListTypeDataAccess typeDataAccess, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(typeDataAccess);

        TypeDataAccess = typeDataAccess;
        _elementOrdinals = ReadOrdinals(typeDataAccess, ordinal);
    }

    /// <summary>Caches the record at <paramref name="ordinal"/>.</summary>
    public HollowListCachedDelegate(HollowListTypeApi typeApi, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(typeApi);

        TypeDataAccess = typeApi.TypeDataAccess;
        TypeApi = typeApi;
        _elementOrdinals = ReadOrdinals(TypeDataAccess, ordinal);
    }

    /// <inheritdoc />
    public HollowListSchema Schema => TypeDataAccess.Schema;

    /// <inheritdoc />
    public IHollowListTypeDataAccess TypeDataAccess { get; private set; }

    /// <inheritdoc />
    public HollowListTypeApi? TypeApi { get; private set; }

    /// <inheritdoc />
    public int Size(int ordinal) => _elementOrdinals.Length;

    /// <inheritdoc />
    public T GetElement(HollowList<T> list, int ordinal, int index)
    {
        ArgumentNullException.ThrowIfNull(list);

        return list.InstantiateElement(_elementOrdinals[index]);
    }

    /// <inheritdoc />
    public int IndexOf(HollowList<T> list, int ordinal, object? item)
    {
        ArgumentNullException.ThrowIfNull(list);

        for (int i = 0; i < _elementOrdinals.Length; i++)
        {
            if (list.EqualsElement(_elementOrdinals[i], item))
            {
                return i;
            }
        }

        return -1;
    }

    /// <inheritdoc />
    public int LastIndexOf(HollowList<T> list, int ordinal, object? item)
    {
        ArgumentNullException.ThrowIfNull(list);

        for (int i = _elementOrdinals.Length - 1; i >= 0; i--)
        {
            if (list.EqualsElement(_elementOrdinals[i], item))
            {
                return i;
            }
        }

        return -1;
    }

    /// <inheritdoc />
    public void UpdateTypeApi(HollowTypeApi typeApi)
    {
        ArgumentNullException.ThrowIfNull(typeApi);

        TypeApi = (HollowListTypeApi)typeApi;
        TypeDataAccess = TypeApi.TypeDataAccess;
    }

    private static int[] ReadOrdinals(IHollowListTypeDataAccess typeDataAccess, int ordinal)
    {
        int[] ordinals = new int[typeDataAccess.Size(ordinal)];

        for (int i = 0; i < ordinals.Length; i++)
        {
            ordinals[i] = typeDataAccess.GetElementOrdinal(ordinal, i);
        }

        return ordinals;
    }
}

/// <summary>
/// Reads the elements of a set record.
/// </summary>
/// <typeparam name="T">The wrapper type an element is read as.</typeparam>
public interface IHollowSetDelegate<T> : IHollowRecordDelegate
{
    /// <summary>The schema of the type this delegate reads.</summary>
    HollowSetSchema Schema { get; }

    /// <summary>Read access to the type's records.</summary>
    IHollowSetTypeDataAccess TypeDataAccess { get; }

    /// <summary>The type API this delegate belongs to, if it was built through one.</summary>
    HollowSetTypeApi? TypeApi { get; }

    /// <summary>The number of elements in the given record.</summary>
    int Size(int ordinal);

    /// <summary>Whether the given record holds an element equal to <paramref name="item"/>.</summary>
    bool Contains(HollowSet<T> set, int ordinal, object? item);

    /// <summary>
    /// The element matching the type's declared hash key, or <see langword="default"/> when there is
    /// none.
    /// </summary>
    T? FindElement(HollowSet<T> set, int ordinal, params object?[] hashKey);

    /// <summary>The element ordinals of the given record.</summary>
    IEnumerable<int> ElementOrdinals(int ordinal);
}

/// <summary>
/// Reads a set record's elements straight out of the blob.
/// </summary>
public sealed class HollowSetLookupDelegate<T> : IHollowSetDelegate<T>
{
    /// <summary>Reads the records of <paramref name="typeDataAccess"/>.</summary>
    public HollowSetLookupDelegate(IHollowSetTypeDataAccess typeDataAccess)
    {
        ArgumentNullException.ThrowIfNull(typeDataAccess);

        TypeDataAccess = typeDataAccess;
    }

    /// <summary>Reads the records <paramref name="typeApi"/> covers.</summary>
    public HollowSetLookupDelegate(HollowSetTypeApi typeApi)
    {
        ArgumentNullException.ThrowIfNull(typeApi);

        TypeDataAccess = typeApi.TypeDataAccess;
        TypeApi = typeApi;
    }

    /// <inheritdoc />
    public HollowSetSchema Schema => TypeDataAccess.Schema;

    /// <inheritdoc />
    public IHollowSetTypeDataAccess TypeDataAccess { get; }

    /// <inheritdoc />
    public HollowSetTypeApi? TypeApi { get; }

    /// <inheritdoc />
    public int Size(int ordinal) => TypeDataAccess.Size(ordinal);

    /// <inheritdoc />
    public bool Contains(HollowSet<T> set, int ordinal, object? item) =>
        SetDelegateHelper.Contains(TypeDataAccess, set, ordinal, item, TypeDataAccess.ElementOrdinals(ordinal));

    /// <inheritdoc />
    public T? FindElement(HollowSet<T> set, int ordinal, params object?[] hashKey)
    {
        ArgumentNullException.ThrowIfNull(set);

        int elementOrdinal = TypeDataAccess.FindElement(ordinal, hashKey);

        return elementOrdinal == HollowConstants.OrdinalNone ? default : set.InstantiateElement(elementOrdinal);
    }

    /// <inheritdoc />
    public IEnumerable<int> ElementOrdinals(int ordinal) => TypeDataAccess.ElementOrdinals(ordinal);
}

/// <summary>
/// Holds one set record's element ordinals, so that reading them again costs nothing.
/// </summary>
public sealed class HollowSetCachedDelegate<T> : IHollowSetDelegate<T>, IHollowCachedDelegate
{
    private readonly int[] _elementOrdinals;

    /// <summary>Caches the record at <paramref name="ordinal"/>.</summary>
    public HollowSetCachedDelegate(IHollowSetTypeDataAccess typeDataAccess, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(typeDataAccess);

        TypeDataAccess = typeDataAccess;
        _elementOrdinals = [.. typeDataAccess.ElementOrdinals(ordinal)];
    }

    /// <summary>Caches the record at <paramref name="ordinal"/>.</summary>
    public HollowSetCachedDelegate(HollowSetTypeApi typeApi, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(typeApi);

        TypeDataAccess = typeApi.TypeDataAccess;
        TypeApi = typeApi;
        _elementOrdinals = [.. TypeDataAccess.ElementOrdinals(ordinal)];
    }

    /// <inheritdoc />
    public HollowSetSchema Schema => TypeDataAccess.Schema;

    /// <inheritdoc />
    public IHollowSetTypeDataAccess TypeDataAccess { get; private set; }

    /// <inheritdoc />
    public HollowSetTypeApi? TypeApi { get; private set; }

    /// <inheritdoc />
    public int Size(int ordinal) => _elementOrdinals.Length;

    /// <inheritdoc />
    public bool Contains(HollowSet<T> set, int ordinal, object? item)
    {
        ArgumentNullException.ThrowIfNull(set);

        foreach (int elementOrdinal in _elementOrdinals)
        {
            if (set.EqualsElement(elementOrdinal, item))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public T? FindElement(HollowSet<T> set, int ordinal, params object?[] hashKey)
    {
        ArgumentNullException.ThrowIfNull(set);

        int elementOrdinal = TypeDataAccess.FindElement(ordinal, hashKey);

        return elementOrdinal == HollowConstants.OrdinalNone ? default : set.InstantiateElement(elementOrdinal);
    }

    /// <inheritdoc />
    public IEnumerable<int> ElementOrdinals(int ordinal) => _elementOrdinals;

    /// <inheritdoc />
    public void UpdateTypeApi(HollowTypeApi typeApi)
    {
        ArgumentNullException.ThrowIfNull(typeApi);

        TypeApi = (HollowSetTypeApi)typeApi;
        TypeDataAccess = TypeApi.TypeDataAccess;
    }
}

/// <summary>
/// Reads the entries of a map record.
/// </summary>
/// <typeparam name="TKey">The wrapper type a key is read as.</typeparam>
/// <typeparam name="TValue">The wrapper type a value is read as.</typeparam>
public interface IHollowMapDelegate<TKey, TValue> : IHollowRecordDelegate
{
    /// <summary>The schema of the type this delegate reads.</summary>
    HollowMapSchema Schema { get; }

    /// <summary>Read access to the type's records.</summary>
    IHollowMapTypeDataAccess TypeDataAccess { get; }

    /// <summary>The type API this delegate belongs to, if it was built through one.</summary>
    HollowMapTypeApi? TypeApi { get; }

    /// <summary>The number of entries in the given record.</summary>
    int Size(int ordinal);

    /// <summary>The value mapped to <paramref name="key"/>, or <see langword="default"/>.</summary>
    TValue? Get(HollowMap<TKey, TValue> map, int ordinal, object? key);

    /// <summary>Whether the given record holds an entry with that key.</summary>
    bool ContainsKey(HollowMap<TKey, TValue> map, int ordinal, object? key);

    /// <summary>Whether the given record holds an entry with that value.</summary>
    bool ContainsValue(HollowMap<TKey, TValue> map, int ordinal, object? value);

    /// <summary>The key matching the type's declared hash key, or <see langword="default"/>.</summary>
    TKey? FindKey(HollowMap<TKey, TValue> map, int ordinal, params object?[] hashKey);

    /// <summary>The value matching the type's declared hash key, or <see langword="default"/>.</summary>
    TValue? FindValue(HollowMap<TKey, TValue> map, int ordinal, params object?[] hashKey);

    /// <summary>The entry matching the type's declared hash key, or <see langword="null"/>.</summary>
    KeyValuePair<TKey, TValue>? FindEntry(HollowMap<TKey, TValue> map, int ordinal, params object?[] hashKey);

    /// <summary>The entries of the given record.</summary>
    IEnumerable<HollowMapEntry> Entries(int ordinal);
}

/// <summary>
/// Reads a map record's entries straight out of the blob.
/// </summary>
public sealed class HollowMapLookupDelegate<TKey, TValue> : IHollowMapDelegate<TKey, TValue>
{
    /// <summary>Reads the records of <paramref name="typeDataAccess"/>.</summary>
    public HollowMapLookupDelegate(IHollowMapTypeDataAccess typeDataAccess)
    {
        ArgumentNullException.ThrowIfNull(typeDataAccess);

        TypeDataAccess = typeDataAccess;
    }

    /// <summary>Reads the records <paramref name="typeApi"/> covers.</summary>
    public HollowMapLookupDelegate(HollowMapTypeApi typeApi)
    {
        ArgumentNullException.ThrowIfNull(typeApi);

        TypeDataAccess = typeApi.TypeDataAccess;
        TypeApi = typeApi;
    }

    /// <inheritdoc />
    public HollowMapSchema Schema => TypeDataAccess.Schema;

    /// <inheritdoc />
    public IHollowMapTypeDataAccess TypeDataAccess { get; }

    /// <inheritdoc />
    public HollowMapTypeApi? TypeApi { get; }

    /// <inheritdoc />
    public int Size(int ordinal) => TypeDataAccess.Size(ordinal);

    /// <inheritdoc />
    public TValue? Get(HollowMap<TKey, TValue> map, int ordinal, object? key) =>
        MapDelegateHelper.Get(TypeDataAccess, map, ordinal, key);

    /// <inheritdoc />
    public bool ContainsKey(HollowMap<TKey, TValue> map, int ordinal, object? key) =>
        MapDelegateHelper.ContainsKey(TypeDataAccess, map, ordinal, key);

    /// <inheritdoc />
    public bool ContainsValue(HollowMap<TKey, TValue> map, int ordinal, object? value) =>
        MapDelegateHelper.ContainsValue(TypeDataAccess, map, ordinal, value);

    /// <inheritdoc />
    public TKey? FindKey(HollowMap<TKey, TValue> map, int ordinal, params object?[] hashKey)
    {
        ArgumentNullException.ThrowIfNull(map);

        int keyOrdinal = TypeDataAccess.FindKey(ordinal, hashKey);

        return keyOrdinal == HollowConstants.OrdinalNone ? default : map.InstantiateKey(keyOrdinal);
    }

    /// <inheritdoc />
    public TValue? FindValue(HollowMap<TKey, TValue> map, int ordinal, params object?[] hashKey)
    {
        ArgumentNullException.ThrowIfNull(map);

        int valueOrdinal = TypeDataAccess.FindValue(ordinal, hashKey);

        return valueOrdinal == HollowConstants.OrdinalNone ? default : map.InstantiateValue(valueOrdinal);
    }

    /// <inheritdoc />
    public KeyValuePair<TKey, TValue>? FindEntry(
        HollowMap<TKey, TValue> map, int ordinal, params object?[] hashKey)
    {
        ArgumentNullException.ThrowIfNull(map);

        long entry = TypeDataAccess.FindEntry(ordinal, hashKey);

        return entry == -1L
            ? null
            : new KeyValuePair<TKey, TValue>(
                map.InstantiateKey((int)(entry >> 32)), map.InstantiateValue((int)entry));
    }

    /// <inheritdoc />
    public IEnumerable<HollowMapEntry> Entries(int ordinal) => TypeDataAccess.Entries(ordinal);
}

/// <summary>
/// Holds one map record's key and value ordinals, so that reading them again costs nothing.
/// </summary>
public sealed class HollowMapCachedDelegate<TKey, TValue> : IHollowMapDelegate<TKey, TValue>, IHollowCachedDelegate
{
    private readonly HollowMapEntry[] _entries;

    /// <summary>Caches the record at <paramref name="ordinal"/>.</summary>
    public HollowMapCachedDelegate(IHollowMapTypeDataAccess typeDataAccess, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(typeDataAccess);

        TypeDataAccess = typeDataAccess;
        _entries = ReadEntries(typeDataAccess, ordinal);
    }

    /// <summary>Caches the record at <paramref name="ordinal"/>.</summary>
    public HollowMapCachedDelegate(HollowMapTypeApi typeApi, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(typeApi);

        TypeDataAccess = typeApi.TypeDataAccess;
        TypeApi = typeApi;
        _entries = ReadEntries(TypeDataAccess, ordinal);
    }

    /// <inheritdoc />
    public HollowMapSchema Schema => TypeDataAccess.Schema;

    /// <inheritdoc />
    public IHollowMapTypeDataAccess TypeDataAccess { get; private set; }

    /// <inheritdoc />
    public HollowMapTypeApi? TypeApi { get; private set; }

    /// <inheritdoc />
    public int Size(int ordinal) => _entries.Length;

    /// <inheritdoc />
    public TValue? Get(HollowMap<TKey, TValue> map, int ordinal, object? key)
    {
        ArgumentNullException.ThrowIfNull(map);

        foreach (HollowMapEntry entry in _entries)
        {
            if (map.EqualsKey(entry.KeyOrdinal, key))
            {
                return map.InstantiateValue(entry.ValueOrdinal);
            }
        }

        return default;
    }

    /// <inheritdoc />
    public bool ContainsKey(HollowMap<TKey, TValue> map, int ordinal, object? key)
    {
        ArgumentNullException.ThrowIfNull(map);

        return _entries.Any(entry => map.EqualsKey(entry.KeyOrdinal, key));
    }

    /// <inheritdoc />
    public bool ContainsValue(HollowMap<TKey, TValue> map, int ordinal, object? value)
    {
        ArgumentNullException.ThrowIfNull(map);

        return _entries.Any(entry => map.EqualsValue(entry.ValueOrdinal, value));
    }

    /// <inheritdoc />
    public TKey? FindKey(HollowMap<TKey, TValue> map, int ordinal, params object?[] hashKey)
    {
        ArgumentNullException.ThrowIfNull(map);

        int keyOrdinal = TypeDataAccess.FindKey(ordinal, hashKey);

        return keyOrdinal == HollowConstants.OrdinalNone ? default : map.InstantiateKey(keyOrdinal);
    }

    /// <inheritdoc />
    public TValue? FindValue(HollowMap<TKey, TValue> map, int ordinal, params object?[] hashKey)
    {
        ArgumentNullException.ThrowIfNull(map);

        int valueOrdinal = TypeDataAccess.FindValue(ordinal, hashKey);

        return valueOrdinal == HollowConstants.OrdinalNone ? default : map.InstantiateValue(valueOrdinal);
    }

    /// <inheritdoc />
    public KeyValuePair<TKey, TValue>? FindEntry(
        HollowMap<TKey, TValue> map, int ordinal, params object?[] hashKey)
    {
        ArgumentNullException.ThrowIfNull(map);

        long entry = TypeDataAccess.FindEntry(ordinal, hashKey);

        return entry == -1L
            ? null
            : new KeyValuePair<TKey, TValue>(
                map.InstantiateKey((int)(entry >> 32)), map.InstantiateValue((int)entry));
    }

    /// <inheritdoc />
    public IEnumerable<HollowMapEntry> Entries(int ordinal) => _entries;

    /// <inheritdoc />
    public void UpdateTypeApi(HollowTypeApi typeApi)
    {
        ArgumentNullException.ThrowIfNull(typeApi);

        TypeApi = (HollowMapTypeApi)typeApi;
        TypeDataAccess = TypeApi.TypeDataAccess;
    }

    private static HollowMapEntry[] ReadEntries(IHollowMapTypeDataAccess typeDataAccess, int ordinal) =>
        [.. typeDataAccess.Entries(ordinal)];
}

/// <summary>
/// The membership probe the set delegates share.
/// </summary>
internal static class SetDelegateHelper
{
    /// <summary>
    /// Whether <paramref name="item"/> is in the given record.
    /// </summary>
    /// <remarks>
    /// Where the type declares no hash key its table is laid out by element ordinal, so an item that
    /// is itself a Hollow record of the element type can be probed for directly. Anything else — and
    /// any type with a hash key, whose table is laid out by that key instead — is found by walking the
    /// record's elements.
    /// </remarks>
    internal static bool Contains<T>(
        IHollowSetTypeDataAccess typeDataAccess,
        HollowSet<T> set,
        int ordinal,
        object? item,
        IEnumerable<int> elementOrdinals)
    {
        ArgumentNullException.ThrowIfNull(set);

        if (typeDataAccess.Schema.HashKey is null
            && item is IHollowRecord record
            && string.Equals(
                record.Schema.Name, typeDataAccess.Schema.ElementType, StringComparison.Ordinal))
        {
            return typeDataAccess.Contains(ordinal, record.Ordinal);
        }

        foreach (int elementOrdinal in elementOrdinals)
        {
            if (set.EqualsElement(elementOrdinal, item))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// The lookups the map delegates share.
/// </summary>
internal static class MapDelegateHelper
{
    /// <summary>The value mapped to <paramref name="key"/>, or <see langword="default"/>.</summary>
    /// <remarks>
    /// As with a set, a key that is itself a Hollow record of the key type can be probed for directly
    /// where the type declares no hash key; otherwise the entries are walked.
    /// </remarks>
    internal static TValue? Get<TKey, TValue>(
        IHollowMapTypeDataAccess typeDataAccess, HollowMap<TKey, TValue> map, int ordinal, object? key)
    {
        ArgumentNullException.ThrowIfNull(map);

        if (typeDataAccess.Schema.HashKey is null
            && key is IHollowRecord record
            && string.Equals(record.Schema.Name, typeDataAccess.Schema.KeyType, StringComparison.Ordinal))
        {
            int valueOrdinal = typeDataAccess.Get(ordinal, record.Ordinal);

            return valueOrdinal == HollowConstants.OrdinalNone ? default : map.InstantiateValue(valueOrdinal);
        }

        foreach (HollowMapEntry entry in typeDataAccess.Entries(ordinal))
        {
            if (map.EqualsKey(entry.KeyOrdinal, key))
            {
                return map.InstantiateValue(entry.ValueOrdinal);
            }
        }

        return default;
    }

    /// <summary>Whether the given record holds an entry with that key.</summary>
    internal static bool ContainsKey<TKey, TValue>(
        IHollowMapTypeDataAccess typeDataAccess, HollowMap<TKey, TValue> map, int ordinal, object? key)
    {
        ArgumentNullException.ThrowIfNull(map);

        if (typeDataAccess.Schema.HashKey is null
            && key is IHollowRecord record
            && string.Equals(record.Schema.Name, typeDataAccess.Schema.KeyType, StringComparison.Ordinal))
        {
            return typeDataAccess.Get(ordinal, record.Ordinal) != HollowConstants.OrdinalNone;
        }

        foreach (HollowMapEntry entry in typeDataAccess.Entries(ordinal))
        {
            if (map.EqualsKey(entry.KeyOrdinal, key))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the given record holds an entry with that value.</summary>
    internal static bool ContainsValue<TKey, TValue>(
        IHollowMapTypeDataAccess typeDataAccess, HollowMap<TKey, TValue> map, int ordinal, object? value)
    {
        ArgumentNullException.ThrowIfNull(map);

        foreach (HollowMapEntry entry in typeDataAccess.Entries(ordinal))
        {
            if (map.EqualsValue(entry.ValueOrdinal, value))
            {
                return true;
            }
        }

        return false;
    }
}

