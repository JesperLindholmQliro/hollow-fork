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

namespace Hollow.Core.Memory.Encoding;

/// <summary>
/// Converts between a <see cref="decimal"/> and the sixteen bytes a
/// <see cref="Schema.FieldType.Decimal"/> field stores.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is part of a format extension, not of Netflix Hollow.</strong> See the "Format
/// extension: the Decimal field type" section of <c>PORTING.md</c>.
/// </para>
/// <para>
/// The stored form is the four integers <see cref="decimal.GetBits(decimal)"/> returns — low, middle
/// and high words of the 96-bit mantissa, then the flags word carrying the scale and sign — each
/// big-endian, in that order. Those sixteen bytes are held as two 64-bit words, so that the
/// fixed-length bit string can carry them as two elements:
/// </para>
/// <list type="bullet">
/// <item><description><c>Low</c> holds <c>bits[0]</c> in its high 32 bits and <c>bits[1]</c> in its low 32.</description></item>
/// <item><description><c>High</c> holds <c>bits[2]</c> in its high 32 bits and <c>bits[3]</c> in its low 32.</description></item>
/// </list>
/// <para>
/// This is a lossless, round-trippable encoding: unlike converting through <see cref="double"/>, it
/// preserves both the exact value and its scale, so <c>1.50m</c> and <c>1.5m</c> stay distinguishable.
/// </para>
/// </remarks>
public static class DecimalBits
{
    /// <summary>
    /// The low word of the pattern marking a decimal field as null.
    /// </summary>
    /// <remarks>
    /// All sixteen bytes set is not a representable decimal — the flags word may only carry a scale of
    /// 0 to 28 in bits 16 to 23 and a sign in bit 31, so an all-ones flags word is invalid — which is
    /// what makes it usable as a sentinel. It is also the convention Hollow already uses for its other
    /// fixed-length fields, where an all-ones value of the field's width means null.
    /// </remarks>
    public const long NullLow = -1L;

    /// <summary>The high word of the pattern marking a decimal field as null.</summary>
    /// <remarks>See <see cref="NullLow"/>.</remarks>
    public const long NullHigh = -1L;

    /// <summary>The width of a decimal field, in bits.</summary>
    public const int BitsPerDecimal = 128;

    /// <summary>The width of a decimal field, in bytes.</summary>
    public const int BytesPerDecimal = 16;

    /// <summary>
    /// Encodes <paramref name="value"/> as the two 64-bit words a decimal field stores.
    /// </summary>
    public static (long Low, long High) Pack(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);

        return (Combine(bits[0], bits[1]), Combine(bits[2], bits[3]));
    }

    /// <summary>
    /// Decodes a decimal from the two 64-bit words a decimal field stores.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The words are not a valid decimal. A well-formed blob never produces this: it means the field
    /// holds something other than an encoded decimal.
    /// </exception>
    public static decimal Unpack(long low, long high)
    {
        ReadOnlySpan<int> bits =
        [
            (int)(low >> 32),
            (int)low,
            (int)(high >> 32),
            (int)high,
        ];

        return new decimal(bits);
    }

    /// <summary>
    /// Whether the two words are the null sentinel rather than an encoded decimal.
    /// </summary>
    public static bool IsNull(long low, long high) => low == NullLow && high == NullHigh;

    /// <summary>
    /// A hash code for <paramref name="value"/> that does not depend on its scale, and that is stable
    /// across processes and runtime versions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stored form keeps a decimal's scale, so <c>1.50m</c> and <c>1.5m</c> are stored differently.
    /// They compare equal under .NET's own <c>==</c>, though, so anything that hashes them — a primary
    /// key index, or a set laid out by a declared hash key — has to give them the same hash or the
    /// table's invariant breaks. Trailing zeros are therefore stripped before hashing.
    /// </para>
    /// <para>
    /// <see cref="decimal.GetHashCode"/> would also be scale-invariant, but it is an implementation
    /// detail of the runtime, and a hash that decides where a record lands in a blob has to keep
    /// producing the same answer across runtime versions.
    /// </para>
    /// </remarks>
    public static int CanonicalHashCode(decimal value)
    {
        (UInt128 mantissa, int scale, bool isNegative) = Canonicalize(value);

        int hash = (int)(uint)mantissa;
        hash = (hash * 31) ^ (int)(uint)(mantissa >> 32);
        hash = (hash * 31) ^ (int)(uint)(mantissa >> 64);
        hash = (hash * 31) ^ scale;

        return isNegative ? ~hash : hash;
    }

    /// <summary>
    /// Strips the trailing zeros from a decimal's mantissa, so that every representation of one value
    /// reduces to the same triple.
    /// </summary>
    private static (UInt128 Mantissa, int Scale, bool IsNegative) Canonicalize(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);

        UInt128 mantissa = ((UInt128)(uint)bits[2] << 64) | ((UInt128)(uint)bits[1] << 32) | (uint)bits[0];
        int scale = (bits[3] >> 16) & 0xFF;
        bool isNegative = bits[3] < 0;

        while (scale > 0 && mantissa % 10 == 0)
        {
            mantissa /= 10;
            scale--;
        }

        // Negative zero equals zero, so it has to reduce to the same triple.
        return mantissa == 0 ? (UInt128.Zero, 0, false) : (mantissa, scale, isNegative);
    }

    private static long Combine(int high32, int low32) => ((long)high32 << 32) | (uint)low32;
}
