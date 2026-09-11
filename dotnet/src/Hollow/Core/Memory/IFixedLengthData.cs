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

using System.Numerics;
using Hollow.Core.Memory.Encoding;
using Hollow.Core.Read;

namespace Hollow.Core.Memory;

/// <summary>
/// A bit string that stores fixed-width elements at bit offsets.
/// </summary>
/// <remarks>
/// <para>
/// Named <c>FixedLengthData</c> in Java; the <c>I</c> prefix follows the .NET interface naming
/// convention.
/// </para>
/// <para>
/// The bit string is little-endian within each underlying 64-bit word: an element of
/// <c>bitsPerElement</c> bits at bit index <c>i</c> occupies the bits <c>[i, i + bitsPerElement)</c>,
/// counting from the least significant bit of word <c>i / 64</c>.
/// </para>
/// <para>
/// The .NET port keeps the Java split between <see cref="GetElementValue(long, int)"/> and
/// <see cref="GetLargeElementValue(long, int)"/> for source compatibility, but the distinction no
/// longer reflects an implementation difference — see <see cref="Encoding.FixedLengthElementArray"/>.
/// </para>
/// </remarks>
public interface IFixedLengthData
{
    /// <summary>
    /// Gets an element value of <paramref name="bitsPerElement"/> bits at the given bit index.
    /// </summary>
    /// <param name="index">The bit index.</param>
    /// <param name="bitsPerElement">Bits per element; must be 58 or fewer.</param>
    long GetElementValue(long index, int bitsPerElement);

    /// <summary>
    /// Gets a masked element value of <paramref name="bitsPerElement"/> bits at the given bit index.
    /// </summary>
    /// <param name="index">The bit index.</param>
    /// <param name="bitsPerElement">Bits per element; must be 58 or fewer.</param>
    /// <param name="mask">
    /// The mask to apply before the value is returned. It should be less than or equal to
    /// <c>(1L &lt;&lt; bitsPerElement) - 1</c> to guarantee that partial neighbouring element values
    /// are not included in the result.
    /// </param>
    long GetElementValue(long index, int bitsPerElement, long mask);

    /// <summary>
    /// Gets an element value of up to 64 bits at the given bit index.
    /// </summary>
    /// <param name="index">The bit index.</param>
    /// <param name="bitsPerElement">Bits per element; may exceed 58.</param>
    long GetLargeElementValue(long index, int bitsPerElement);

    /// <summary>
    /// Gets a masked element value of up to 64 bits at the given bit index.
    /// </summary>
    /// <param name="index">The bit index.</param>
    /// <param name="bitsPerElement">Bits per element; may exceed 58.</param>
    /// <param name="mask">
    /// The mask to apply before the value is returned. It should be less than or equal to
    /// <c>(1L &lt;&lt; bitsPerElement) - 1</c> to guarantee that partial neighbouring element values
    /// are not included in the result.
    /// </param>
    long GetLargeElementValue(long index, int bitsPerElement, long mask);

    /// <summary>
    /// Ors <paramref name="value"/> into the <paramref name="bitsPerElement"/> bits at the given bit
    /// index. The target bits are assumed to be zero.
    /// </summary>
    void SetElementValue(long index, int bitsPerElement, long value);

    /// <summary>
    /// Zeroes the <paramref name="bitsPerElement"/> bits at the given bit index.
    /// </summary>
    void ClearElementValue(long index, int bitsPerElement);

    /// <summary>
    /// Copies <paramref name="numBits"/> bits out of <paramref name="copyFrom"/> into this bit string.
    /// </summary>
    void CopyBits(IFixedLengthData copyFrom, long sourceStartBit, long destinationStartBit, long numBits);

    /// <summary>
    /// Adds <paramref name="increment"/> to each of <paramref name="numIncrements"/> elements spaced
    /// <paramref name="bitsBetweenIncrements"/> bits apart, starting at <paramref name="startBit"/>.
    /// </summary>
    void IncrementMany(long startBit, long increment, long bitsBetweenIncrements, int numIncrements);

    /// <summary>
    /// Discards fixed-length data from <paramref name="input"/>, which begins with the number of
    /// 64-bit words to discard.
    /// </summary>
    static void DiscardFrom(HollowBlobInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        long numLongs = VarInt.ReadVLong(input);
        long bytesToSkip = numLongs * 8;

        while (bytesToSkip > 0)
        {
            long skipped = input.SkipBytes(bytesToSkip);
            if (skipped <= 0)
            {
                throw new EndOfStreamException("unexpected end of fixed-length data");
            }

            bytesToSkip -= skipped;
        }
    }

    /// <summary>
    /// Returns the number of bits required to represent <paramref name="value"/>, at least 1.
    /// </summary>
    static int BitsRequiredToRepresentValue(long value) =>
        value == 0 ? 1 : 64 - BitOperations.LeadingZeroCount((ulong)value);
}
