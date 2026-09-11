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

namespace Hollow.Core.Memory.Pool;

/// <summary>
/// A memory pool of fixed-length array segments.
/// </summary>
/// <remarks>
/// <para>
/// Named <c>ArraySegmentRecycler</c> in Java; the <c>I</c> prefix follows the .NET interface
/// naming convention.
/// </para>
/// <para>
/// Hollow pools and reuses memory to minimise GC effects while updating data. Every array in the pool
/// has a fixed length. When a long array or a byte array is required, Hollow stitches together pooled
/// segments as a <see cref="SegmentedByteArray"/> or <see cref="SegmentedLongArray"/>, which
/// encapsulate the details of treating segmented arrays as contiguous ranges of values.
/// </para>
/// </remarks>
public interface IArraySegmentRecycler
{
    /// <summary>Default base-2 logarithm of the byte segment length.</summary>
    const int DefaultLog2ByteArraySize = 11;

    /// <summary>Default base-2 logarithm of the long segment length.</summary>
    const int DefaultLog2LongArraySize = 8;

    /// <summary>The base-2 logarithm of the length of the byte arrays this recycler hands out.</summary>
    int Log2OfByteSegmentSize { get; }

    /// <summary>The base-2 logarithm of the length of the long arrays this recycler hands out.</summary>
    int Log2OfLongSegmentSize { get; }

    /// <summary>Obtains a zeroed long array of length <c>1 &lt;&lt; Log2OfLongSegmentSize</c>.</summary>
    long[] GetLongArray();

    /// <summary>Returns a long array to the pool.</summary>
    void RecycleLongArray(long[] array);

    /// <summary>Obtains a byte array of length <c>1 &lt;&lt; Log2OfByteSegmentSize</c>.</summary>
    byte[] GetByteArray();

    /// <summary>Returns a byte array to the pool.</summary>
    void RecycleByteArray(byte[] array);

    /// <summary>
    /// Makes everything recycled since the last swap available to subsequent <c>Get</c> calls.
    /// </summary>
    void Swap();
}
