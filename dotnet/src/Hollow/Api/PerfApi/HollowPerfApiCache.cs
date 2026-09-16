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

using Hollow.Core.Util;

namespace Hollow.Api.PerfApi;

/// <summary>
/// One wrapper per record of a type, built once and kept across transitions.
/// </summary>
/// <remarks>
/// <para>
/// The performance API's point is that reads cost nothing, and building a wrapper per read is exactly
/// what it avoids. Where a caller does want objects — because it holds them, compares them, or hands
/// them to something that cannot take a reference — this builds them all once per transition.
/// </para>
/// <para>
/// What makes it worth having rather than a plain array is the delta case: a record whose ordinal was
/// populated before the transition and still is has not changed, because Hollow assigns a new ordinal
/// to a changed record. So its wrapper is carried over, and only the ordinals that actually moved are
/// rebuilt. A cycle that changes a thousand records out of a million rebuilds a thousand.
/// </para>
/// <para>
/// A record the last transition <em>removed</em> stays readable: its wrapper is kept, and the array is
/// sized to the longer of the current and previous populated sets. That is deliberate — a caller
/// working out what a transition did has to read what went away as well as what arrived, and the
/// storage behind it has not been reused yet. It survives one transition, not two: the next cache
/// built on top of this one clears the slot.
/// </para>
/// </remarks>
/// <typeparam name="T">The wrapper type.</typeparam>
public sealed class HollowPerfApiCache<T>
    where T : class
{
    private readonly HollowTypePerfApi _typeApi;
    private readonly T?[] _cached;

    /// <summary>
    /// Builds the cache for <paramref name="typeApi"/>, carrying over what <paramref name="previous"/>
    /// already built.
    /// </summary>
    /// <param name="typeApi">The type to cache.</param>
    /// <param name="instantiate">Builds a wrapper from a reference.</param>
    /// <param name="previous">
    /// The cache from before the last transition, or <see langword="null"/> to build every wrapper.
    /// </param>
    public HollowPerfApiCache(
        HollowTypePerfApi typeApi, Func<HollowRef, T> instantiate, HollowPerfApiCache<T>? previous = null)
    {
        ArgumentNullException.ThrowIfNull(typeApi);
        ArgumentNullException.ThrowIfNull(instantiate);

        _typeApi = typeApi;

        if (typeApi.IsMissingType)
        {
            _cached = [];

            return;
        }

        BitSet populated = typeApi.TypeAccess.TypeState.PopulatedOrdinals;
        BitSet previouslyPopulated = typeApi.TypeAccess.TypeState.PreviousOrdinals;

        int length = Math.Max(populated.Length, previouslyPopulated.Length);

        _cached = previous is null ? new T?[length] : ResizedCopy(previous._cached, length);

        for (int ordinal = 0; ordinal < length; ordinal++)
        {
            if (previous is not null && previouslyPopulated.Get(ordinal))
            {
                // Populated before the transition: keep what was built for it, whether or not it is
                // still populated now. Keeping it for a record the transition removed is deliberate,
                // not an oversight — see the remarks on this class.
                continue;
            }

            // Never populated, or freed by an earlier transition than the last one. Build a wrapper
            // where there is now a record, and clear the slot where there is not, so that nothing from
            // two transitions ago is readable.
            _cached[ordinal] = populated.Get(ordinal) ? instantiate(typeApi.RefForOrdinal(ordinal)) : null;
        }
    }

    /// <summary>How many ordinals the cache covers.</summary>
    public int Count => _cached.Length;

    /// <summary>
    /// The wrapper for <paramref name="reference"/>, or <see langword="null"/> where no record is
    /// there.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="reference"/> is of another type.</exception>
    public T? Get(HollowRef reference)
    {
        int ordinal = _typeApi.Ordinal(reference);

        return ordinal < _cached.Length ? _cached[ordinal] : null;
    }

    /// <summary>
    /// Everything cached, by ordinal, with a hole where no record is.
    /// </summary>
    /// <remarks>
    /// A copy, as Java's is: the array is the cache's own, and handing it out would let a caller
    /// invalidate it.
    /// </remarks>
    public T?[] ToArray() => [.. _cached];

    private static T?[] ResizedCopy(T?[] cached, int length)
    {
        T?[] resized = new T?[length];

        Array.Copy(cached, resized, Math.Min(cached.Length, length));

        return resized;
    }
}
