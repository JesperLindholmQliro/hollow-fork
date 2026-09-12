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
using Hollow.Api.Objects.Delegate;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Engine;
using Hollow.Core.Util;

namespace Hollow.Api.Objects.Provider;

/// <summary>
/// Turns an ordinal into a record wrapper.
/// </summary>
/// <remarks>
/// A generated API holds one of these per type and routes every accessor through it, which is what
/// makes caching a whole type a configuration choice rather than a code change: swap
/// <see cref="HollowObjectFactoryProvider{T}"/> for <see cref="HollowObjectCacheProvider{T}"/> and
/// nothing above notices.
/// </remarks>
/// <typeparam name="T">The wrapper type a record is read as.</typeparam>
public abstract class HollowObjectProvider<T>
{
    /// <summary>The wrapper for the record at <paramref name="ordinal"/>.</summary>
    public abstract T? GetHollowObject(int ordinal);
}

/// <summary>
/// Builds the wrapper for one record of a type.
/// </summary>
/// <remarks>
/// A generated API writes one of these per type; it is the only place that knows which wrapper class
/// belongs to which type.
/// </remarks>
/// <typeparam name="T">The wrapper type a record is read as.</typeparam>
public abstract class HollowFactory<T>
{
    /// <summary>
    /// Builds a wrapper that reads straight out of the blob.
    /// </summary>
    public abstract T NewHollowObject(
        IHollowTypeDataAccess typeDataAccess, HollowTypeApi? typeApi, int ordinal);

    /// <summary>
    /// Builds a wrapper that holds a copy of its record.
    /// </summary>
    /// <remarks>
    /// Defaults to the uncached wrapper, so a factory that has no cached form still works behind a
    /// cache provider — it simply does not save anything.
    /// </remarks>
    public virtual T NewCachedHollowObject(
        IHollowTypeDataAccess typeDataAccess, HollowTypeApi? typeApi, int ordinal) =>
        NewHollowObject(typeDataAccess, typeApi, ordinal);
}

/// <summary>
/// Builds a fresh wrapper on every read.
/// </summary>
/// <remarks>
/// The right default: the wrapper is an ordinal and a reference, so making one is cheap, and nothing
/// is held on to.
/// </remarks>
/// <typeparam name="T">The wrapper type a record is read as.</typeparam>
public sealed class HollowObjectFactoryProvider<T>(
    IHollowTypeDataAccess typeDataAccess, HollowTypeApi? typeApi, HollowFactory<T> factory)
    : HollowObjectProvider<T>
{
    /// <inheritdoc />
    public override T GetHollowObject(int ordinal) =>
        factory.NewHollowObject(typeDataAccess, typeApi, ordinal);
}

/// <summary>
/// Holds a wrapper per record, built once and handed out thereafter.
/// </summary>
/// <remarks>
/// <para>
/// Worth it for a type with few records read very often, where the repeated blob reads cost more than
/// the memory. It follows deltas: a record the next version still holds keeps its wrapper, repointed
/// at the new type API, and only the records that changed are rebuilt.
/// </para>
/// <para>
/// A cache pins whatever it holds, so a provider that outlives its usefulness has to be
/// <see cref="Detach"/>ed for the state behind it to be collected.
/// </para>
/// </remarks>
/// <typeparam name="T">The wrapper type a record is read as.</typeparam>
public sealed class HollowObjectCacheProvider<T> : HollowObjectProvider<T>, IHollowTypeStateListener
{
    private readonly HollowFactory<T>? _factory;
    private readonly HollowTypeApi? _typeApi;
    private readonly HollowTypeReadState? _typeReadState;

    private List<T?>? _cachedItems;

    /// <summary>
    /// Caches every record of <paramref name="typeDataAccess"/>, reusing what
    /// <paramref name="previous"/> already built where the record has not changed.
    /// </summary>
    public HollowObjectCacheProvider(
        IHollowTypeDataAccess? typeDataAccess,
        HollowTypeApi? typeApi,
        HollowFactory<T> factory,
        HollowObjectCacheProvider<T>? previous = null)
    {
        ArgumentNullException.ThrowIfNull(factory);

        if (typeDataAccess is null)
        {
            _cachedItems = [];
            return;
        }

        BitSet populatedOrdinals = typeDataAccess.TypeState.PopulatedOrdinals;
        BitSet previousOrdinals = typeDataAccess.TypeState.PreviousOrdinals;

        int length = Math.Max(populatedOrdinals.Length, previousOrdinals.Length);
        List<T?> cached = new(length);

        for (int ordinal = 0; ordinal < length; ordinal++)
        {
            if (previous is not null && previousOrdinals.Get(ordinal) && populatedOrdinals.Get(ordinal))
            {
                // The record survived the delta unchanged, so its wrapper does too — it only has to be
                // repointed at the type API built over the new state.
                T? carriedOver = previous.GetHollowObject(ordinal);
                cached.Add(carriedOver);

                if (carriedOver is IHollowRecord { Delegate: IHollowCachedDelegate cachedDelegate }
                    && typeApi is not null)
                {
                    cachedDelegate.UpdateTypeApi(typeApi);
                }
            }
            else
            {
                cached.Add(
                    populatedOrdinals.Get(ordinal)
                        ? factory.NewCachedHollowObject(typeDataAccess, typeApi, ordinal)
                        : default);
            }
        }

        _cachedItems = cached;

        if (typeDataAccess.TypeState is { } typeReadState)
        {
            _factory = factory;
            _typeApi = typeApi;
            _typeReadState = typeReadState;
            _typeReadState.AddListener(this);
        }
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">This provider has been detached.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="ordinal"/> is past the last record this cache was built over.
    /// </exception>
    public override T? GetHollowObject(int ordinal)
    {
        List<T?> cached = _cachedItems
            ?? throw new InvalidOperationException(
                $"the cache for type {_typeReadState?.TypeName} has been detached");

        if (ordinal >= cached.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ordinal),
                ordinal,
                $"this cache holds {cached.Count.Invariant()} records");
        }

        return cached[ordinal];
    }

    /// <summary>
    /// Releases everything this cache holds, so that the state behind it can be collected.
    /// </summary>
    public void Detach()
    {
        _typeReadState?.RemoveListener(this);
        _cachedItems = null;
    }

    /// <inheritdoc />
    public void BeginUpdate()
    {
        // Nothing to do until the delta says which records arrived.
    }

    /// <inheritdoc />
    public void AddedOrdinal(int ordinal)
    {
        if (_factory is null || _cachedItems is not { } cached || _typeReadState is null)
        {
            return;
        }

        while (cached.Count <= ordinal)
        {
            cached.Add(default);
        }

        cached[ordinal] = _factory.NewCachedHollowObject(_typeReadState, _typeApi, ordinal);
    }

    /// <inheritdoc />
    public void RemovedOrdinal(int ordinal)
    {
        // The wrapper is left in place: an ordinal is only reused a cycle after the record went, and
        // reading an unpopulated ordinal was never meaningful.
    }

    /// <inheritdoc />
    public void EndUpdate()
    {
        // Each addition was handled as it arrived.
    }
}
