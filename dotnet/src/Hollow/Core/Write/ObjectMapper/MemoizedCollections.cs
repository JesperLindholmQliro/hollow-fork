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

namespace Hollow.Core.Write.ObjectMapper;

/// <summary>
/// A collection that remembers the ordinal it was written to, so that meeting the same instance again
/// in one cycle costs a lookup rather than a serialisation.
/// </summary>
/// <remarks>
/// <para>
/// A model that shares one collection instance across many records — the same cast list on every
/// episode of a series, say — otherwise serialises it once per record and relies on the write engine's
/// deduplication to notice they were identical. That works, and it is the expensive way to find out.
/// </para>
/// <para>
/// Java has no interface here: it declares a package-visible <c>__assigned_ordinal</c> field on each of
/// three collection subclasses and type-tests for each of them separately. One interface says the same
/// thing once, and lets a model memoize a collection type of its own.
/// </para>
/// </remarks>
public interface IMemoizedRecord
{
    /// <summary>
    /// The ordinal this instance was last written to, with the cycle it was written in packed above it.
    /// </summary>
    /// <remarks>
    /// The cycle bits are what make a stale value safe: an ordinal remembered in an earlier cycle
    /// means nothing now, and the mapper can tell because the bits do not match.
    /// </remarks>
    long AssignedOrdinal { get; set; }
}

/// <summary>Memoization for the three collection kinds.</summary>
public static class MemoizedRecord
{
    /// <summary>The bits of <see cref="IMemoizedRecord.AssignedOrdinal"/> that identify the cycle.</summary>
    public const long AssignedOrdinalCycleMask = unchecked((long)0xFFFFFFFF00000000);

    /// <summary>An ordinal that has not been assigned in any cycle.</summary>
    public const long Unassigned = -1;

    /// <summary>
    /// The ordinal <paramref name="value"/> was written to in the cycle identified by
    /// <paramref name="cycleBits"/>, or -1 where it was not written in that cycle.
    /// </summary>
    public static int RememberedOrdinal(object value, long cycleBits) =>
        value is IMemoizedRecord memoized
        && (memoized.AssignedOrdinal & AssignedOrdinalCycleMask) == cycleBits
            ? (int)(memoized.AssignedOrdinal & int.MaxValue)
            : -1;

    /// <summary>Records the ordinal <paramref name="value"/> was written to this cycle.</summary>
    public static void Remember(object value, int ordinal, long cycleBits)
    {
        if (value is IMemoizedRecord memoized)
        {
            memoized.AssignedOrdinal = (long)(uint)ordinal | cycleBits;
        }
    }
}

/// <summary>A list whose ordinal is remembered for the cycle it was written in.</summary>
/// <typeparam name="T">The element type.</typeparam>
public sealed class MemoizedList<T> : List<T>, IMemoizedRecord
{
    /// <summary>Initialises an empty list.</summary>
    public MemoizedList()
    {
    }

    /// <summary>Initialises an empty list with room for <paramref name="capacity"/> elements.</summary>
    public MemoizedList(int capacity)
        : base(capacity)
    {
    }

    /// <summary>Initialises a list holding <paramref name="collection"/>'s elements.</summary>
    public MemoizedList(IEnumerable<T> collection)
        : base(collection)
    {
    }

    /// <inheritdoc />
    public long AssignedOrdinal { get; set; } = MemoizedRecord.Unassigned;
}

/// <summary>A set whose ordinal is remembered for the cycle it was written in.</summary>
/// <typeparam name="T">The element type.</typeparam>
public sealed class MemoizedSet<T> : HashSet<T>, IMemoizedRecord
{
    /// <summary>Initialises an empty set.</summary>
    public MemoizedSet()
    {
    }

    /// <summary>Initialises an empty set with room for <paramref name="capacity"/> elements.</summary>
    public MemoizedSet(int capacity)
        : base(capacity)
    {
    }

    /// <summary>Initialises a set holding <paramref name="collection"/>'s distinct elements.</summary>
    public MemoizedSet(IEnumerable<T> collection)
        : base(collection)
    {
    }

    /// <inheritdoc />
    public long AssignedOrdinal { get; set; } = MemoizedRecord.Unassigned;
}

/// <summary>A map whose ordinal is remembered for the cycle it was written in.</summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public sealed class MemoizedMap<TKey, TValue> : Dictionary<TKey, TValue>, IMemoizedRecord
    where TKey : notnull
{
    /// <summary>Initialises an empty map.</summary>
    public MemoizedMap()
    {
    }

    /// <summary>Initialises an empty map with room for <paramref name="capacity"/> entries.</summary>
    public MemoizedMap(int capacity)
        : base(capacity)
    {
    }

    /// <summary>Initialises a map holding <paramref name="dictionary"/>'s entries.</summary>
    public MemoizedMap(IDictionary<TKey, TValue> dictionary)
        : base(dictionary)
    {
    }

    /// <inheritdoc />
    public long AssignedOrdinal { get; set; } = MemoizedRecord.Unassigned;
}
