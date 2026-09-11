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

namespace Hollow.Core.Memory;

/// <summary>
/// Hides the underlying implementation of a range of bytes, which is useful because Hollow often backs
/// such ranges with pooled arrays.
/// </summary>
/// <remarks>
/// Named <c>ByteData</c> in Java; the <c>I</c> prefix follows the .NET interface naming convention.
/// </remarks>
/// <seealso cref="SegmentedByteArray" />
public interface IByteData
{
    /// <summary>
    /// Gets the value of the byte at the specified position.
    /// </summary>
    /// <param name="index">The position, in bytes.</param>
    byte Get(long index);

    /// <summary>The length of this range, in bytes.</summary>
    /// <exception cref="NotSupportedException">The implementation does not track a length.</exception>
    long Length => throw new NotSupportedException();
}

/// <summary>
/// Big-endian primitive reads over any <see cref="IByteData"/>.
/// </summary>
/// <remarks>
/// Java declares these as <c>default</c> methods on the <c>ByteData</c> interface. They are extension
/// methods here so that they remain callable on concrete types: a C# default interface member is only
/// reachable through the interface, which would force a cast at most call sites.
/// </remarks>
public static class ByteDataExtensions
{
    /// <summary>
    /// Reads a big-endian 64-bit integer starting at <paramref name="position"/>.
    /// </summary>
    public static long ReadInt64Bits(this IByteData data, long position)
    {
        ArgumentNullException.ThrowIfNull(data);

        long bits = (long)(data.Get(position++) & 0xFF) << 56;
        bits |= (long)(data.Get(position++) & 0xFF) << 48;
        bits |= (long)(data.Get(position++) & 0xFF) << 40;
        bits |= (long)(data.Get(position++) & 0xFF) << 32;
        bits |= (long)(data.Get(position++) & 0xFF) << 24;
        bits |= (long)(data.Get(position++) & 0xFF) << 16;
        bits |= (long)(data.Get(position++) & 0xFF) << 8;
        bits |= data.Get(position) & 0xFFL;
        return bits;
    }

    /// <summary>
    /// Reads a big-endian 32-bit integer starting at <paramref name="position"/>.
    /// </summary>
    public static int ReadInt32Bits(this IByteData data, long position)
    {
        ArgumentNullException.ThrowIfNull(data);

        int bits = (data.Get(position++) & 0xFF) << 24;
        bits |= (data.Get(position++) & 0xFF) << 16;
        bits |= (data.Get(position++) & 0xFF) << 8;
        bits |= data.Get(position) & 0xFF;
        return bits;
    }
}
