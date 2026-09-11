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

namespace Hollow.Core;

/// <summary>
/// Sentinel constants used across Hollow.
/// </summary>
/// <remarks>
/// Ported from the Java <c>HollowConstants</c> interface. Java uses an interface purely as a
/// constant holder; in C# a static class is the idiomatic equivalent.
/// </remarks>
public static class HollowConstants
{
    /// <summary>A version of <see cref="VersionLatest"/> signifies "latest version".</summary>
    public const long VersionLatest = long.MaxValue;

    /// <summary>A version of <see cref="VersionNone"/> signifies "no version".</summary>
    public const long VersionNone = long.MinValue;

    /// <summary>An ordinal of <see cref="OrdinalNone"/> signifies "null reference" or "no ordinal".</summary>
    public const int OrdinalNone = -1;

    /// <summary>
    /// The maximum number of buckets allowed in a Hollow hash table. Empty space is reserved (based on a
    /// 70% load factor), otherwise performance approaches O(n).
    /// <para>
    /// This value is part of the blob layout contract for SET and MAP types: producers and consumers both
    /// derive a collection's bucket count from its size, so it must not change.
    /// </para>
    /// </summary>
    public const int HashTableMaxSize = (int)((1L << 30) * 7 / 10);

    /// <summary>
    /// The maximum number of elements allowed in an in-memory index hash table, i.e. 70% of 2^31 buckets.
    /// </summary>
    public const long IndexHashTableMaxSize = (1L << 31) * 7 / 10;
}
