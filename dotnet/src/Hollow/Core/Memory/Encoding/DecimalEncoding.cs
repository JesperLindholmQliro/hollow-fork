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

using System.Buffers.Binary;

namespace Hollow.Core.Memory.Encoding;

/// <summary>
/// Encodes a decimal as the fewest bytes that can carry it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Format extension.</strong> <c>Decimal</c> is not one of Netflix Hollow's field types — see
/// <c>PORTING.md</c>. A decimal field is variable-length here, like a string or a byte array.
/// </para>
/// <para>
/// The first byte says which of four forms follows, so a small value costs one byte and only an awkward
/// one costs seventeen:
/// </para>
/// <list type="table">
///   <item>
///     <term><c>0000 aaaa</c></term>
///     <description>Form A. The value is the low nibble itself: a whole number from 0 to 15.</description>
///   </item>
///   <item>
///     <term><c>010Z ssss</c></term>
///     <description>
///       Form B. <c>ssss</c> is the scale and <c>Z</c> the sign — set for positive — followed by the
///       mantissa as a variable-length 32-bit integer.
///     </description>
///   </item>
///   <item>
///     <term><c>011Z ssss</c></term>
///     <description>Form C. As B, with the mantissa as a variable-length 64-bit integer.</description>
///   </item>
///   <item>
///     <term><c>1111 1110</c></term>
///     <description>
///       Form D. Sixteen bytes holding the four words of <see cref="decimal.GetBits(decimal)"/>, which
///       carries anything the others cannot: a mantissa past 64 bits, or a scale past 15.
///     </description>
///   </item>
///   <item>
///     <term><c>1111 1111</c></term>
///     <description>Null.</description>
///   </item>
/// </list>
/// <para>
/// A value is normalised before it is written: <c>1.000m</c>, <c>1.00m</c> and <c>1m</c> all encode as
/// the single byte <c>0x01</c>. The encoding therefore preserves the value and not the scale it happened
/// to arrive with, so a decimal read back compares equal to the one written but need not return the same
/// <see cref="decimal.GetBits(decimal)"/>.
/// </para>
/// <para>
/// Every form is self-describing: <see cref="EncodedLength(ReadOnlySpan{byte})"/> can say how long a
/// value is from its own bytes alone.
/// </para>
/// </remarks>
public static class DecimalEncoding
{
    /// <summary>The byte a null decimal encodes as.</summary>
    /// <remarks>
    /// The format defines a null form, and <see cref="Encode(decimal?)"/> writes it. Hollow's own
    /// containers never reach for it: they mark a null variable-length field themselves — the high bit of
    /// the range pointer in a blob, a null variable-length integer in a flat record — exactly as they do
    /// for a string.
    /// </remarks>
    public const byte NullMarker = 0xFF;

    /// <summary>The most bytes any value can take: the marker and the four words of form D.</summary>
    public const int MaxEncodedLength = 17;

    private const byte FormDMarker = 0xFE;

    /// <summary>The largest whole number form A can carry.</summary>
    private const int MaxFormAValue = 15;

    /// <summary>The largest scale forms B and C can carry, being four bits of it.</summary>
    private const int MaxCompactScale = 15;

    /// <summary>
    /// Encodes <paramref name="value"/> into <paramref name="destination"/>, returning how many bytes it
    /// took.
    /// </summary>
    /// <param name="value">The value to encode.</param>
    /// <param name="destination">
    /// Where to write, which must be at least <see cref="MaxEncodedLength"/> bytes long.
    /// </param>
    public static int Encode(decimal value, Span<byte> destination)
    {
        (UInt128 mantissa, int scale, bool isNegative) = Normalize(value);

        // Form A: a small whole number, which is most of what a model actually holds.
        if (!isNegative && scale == 0 && mantissa <= MaxFormAValue)
        {
            destination[0] = (byte)mantissa;

            return 1;
        }

        if (scale <= MaxCompactScale)
        {
            byte signAndScale = (byte)((isNegative ? 0 : 1 << 4) | scale);

            if (mantissa <= int.MaxValue)
            {
                destination[0] = (byte)(0x40 | signAndScale);

                return 1 + VarInt.WriteVInt(destination[1..], (int)mantissa);
            }

            if (mantissa <= long.MaxValue)
            {
                destination[0] = (byte)(0x60 | signAndScale);

                return 1 + VarInt.WriteVLong(destination[1..], (long)mantissa);
            }
        }

        // Form D: a mantissa past 64 bits, or a scale past 15. The words go out in the order
        // decimal.GetBits returns them, so decoding is a straight handover to the decimal constructor.
        // They are rebuilt from the normalised triple rather than taken from the value as it arrived, so
        // that two spellings of one value still encode as the same bytes.
        destination[0] = FormDMarker;

        ReadOnlySpan<int> bits =
        [
            (int)(uint)mantissa,
            (int)(uint)(mantissa >> 32),
            (int)(uint)(mantissa >> 64),
            (scale << 16) | (isNegative ? int.MinValue : 0),
        ];

        for (int i = 0; i < bits.Length; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(destination[(1 + (i * sizeof(int)))..], bits[i]);
        }

        return MaxEncodedLength;
    }

    /// <summary>Encodes <paramref name="value"/>, or a null, as a new array.</summary>
    public static byte[] Encode(decimal? value)
    {
        if (value is null)
        {
            return [NullMarker];
        }

        Span<byte> buffer = stackalloc byte[MaxEncodedLength];

        return buffer[..Encode(value.Value, buffer)].ToArray();
    }

    /// <summary>
    /// How many bytes the value beginning at <paramref name="source"/> takes, without decoding it.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// <paramref name="source"/> is empty, does not begin a form this format defines, or holds a value
    /// that runs past its end or past the length the format allows. None of these can come out of a
    /// well-formed blob.
    /// </exception>
    /// <remarks>
    /// Every bound here is one the format already guarantees, enforced rather than assumed. A reader
    /// that followed the continuation bits alone would walk the whole of whatever it was handed — a
    /// gigabyte of it, given a gigabyte — to answer a question whose answer is at most seventeen.
    /// </remarks>
    public static int EncodedLength(ReadOnlySpan<byte> source)
    {
        ThrowIfEmpty(source);

        byte marker = source[0];

        if (marker == FormDMarker)
        {
            ThrowIfFormDIsTruncated(source);

            return MaxEncodedLength;
        }

        if (marker == NullMarker || (marker & 0xF0) == 0x00)
        {
            return 1;
        }

        int maxMantissaLength = MantissaLimit(marker);

        // Forms B and C: the marker, then a variable-length integer, every byte of which but the last
        // carries a set high bit.
        int trailing = 0;

        while (true)
        {
            if (1 + trailing >= source.Length)
            {
                throw new InvalidDataException(
                    $"an encoded decimal continues past the end of the {source.Length} bytes holding it");
            }

            if ((source[1 + trailing] & 0x80) == 0)
            {
                return trailing + 2;
            }

            if (++trailing == maxMantissaLength)
            {
                throw new InvalidDataException(
                    $"the mantissa of an encoded decimal cannot be longer than {maxMantissaLength} bytes,"
                    + " and this one is");
            }
        }
    }

    /// <summary>
    /// Decodes the value <paramref name="source"/> begins with, and says how many bytes it took.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// <paramref name="source"/> is empty, the first byte is not one this format defines, or the value
    /// runs past the end of the bytes holding it. A well-formed blob never produces any of these: they
    /// mean the field holds something other than an encoded decimal.
    /// </exception>
    public static decimal? Decode(ReadOnlySpan<byte> source, out int length)
    {
        ThrowIfEmpty(source);

        byte marker = source[0];

        if (marker == NullMarker)
        {
            length = 1;

            return null;
        }

        if (marker == FormDMarker)
        {
            ThrowIfFormDIsTruncated(source);

            length = MaxEncodedLength;

            ReadOnlySpan<int> bits =
            [
                BinaryPrimitives.ReadInt32BigEndian(source[1..]),
                BinaryPrimitives.ReadInt32BigEndian(source[5..]),
                BinaryPrimitives.ReadInt32BigEndian(source[9..]),
                BinaryPrimitives.ReadInt32BigEndian(source[13..]),
            ];

            try
            {
                return new decimal(bits);
            }
            catch (ArgumentException e)
            {
                // Only some of the 2^32 flag words name a decimal: a scale past 28, or a reserved bit
                // set, is not one. Rather than restate that rule here and risk restating it wrongly, the
                // constructor is asked and its refusal is reported as what it means — bytes that were
                // never written by the encoder.
                throw new InvalidDataException(
                    "the sixteen bytes of this decimal do not describe one", e);
            }
        }

        if ((marker & 0xF0) == 0x00)
        {
            length = 1;

            return marker;
        }

        int scale = marker & 0x0F;
        bool isNegative = (marker & 0x10) == 0;

        switch (marker & 0xE0)
        {
            case 0x40:
                int small = VarInt.ReadVInt(source[1..], out int smallLength);
                length = 1 + smallLength;

                return Compose((uint)small, scale, isNegative);

            case 0x60:
                long large = VarInt.ReadVLong(source[1..], out int largeLength);
                length = 1 + largeLength;

                return Compose((ulong)large, scale, isNegative);

            default:
                throw new InvalidDataException(
                    $"0x{marker:X2} does not begin any form of an encoded decimal.");
        }
    }

    /// <summary>
    /// Decodes the <paramref name="length"/> bytes at <paramref name="start"/> of
    /// <paramref name="data"/>. Every container that holds a decimal writes its length ahead of it, as it
    /// does for a string, so the extent is known before the value is read.
    /// </summary>
    /// <remarks>
    /// The byte store is not a span, so the encoded bytes are copied to the stack first. There are at
    /// most seventeen of them.
    /// </remarks>
    public static decimal Decode(IByteData data, long start, int length)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (length <= 0)
        {
            throw new InvalidDataException(
                $"a decimal field cannot be {length} bytes long, and the blob says this one is");
        }

        Span<byte> buffer = stackalloc byte[MaxEncodedLength];
        int available = Math.Min(length, MaxEncodedLength);

        for (int i = 0; i < available; i++)
        {
            buffer[i] = data.Get(start + i);
        }

        return DecodeValue(buffer[..available]);
    }

    /// <summary>Decodes a value that is known not to be null.</summary>
    public static decimal DecodeValue(ReadOnlySpan<byte> source) =>
        Decode(source, out _)
        ?? throw new InvalidDataException("the decimal was null where a value was required");

    /// <summary>
    /// A hash code for <paramref name="value"/> that does not depend on its scale, and that is stable
    /// across processes and runtime versions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Values that compare equal must hash equal, or a primary key index — or a set laid out by a
    /// declared hash key — breaks its own invariant. Normalising first is what guarantees that.
    /// </para>
    /// <para>
    /// <see cref="decimal.GetHashCode"/> is also scale-invariant, but it is an implementation detail of
    /// the runtime, and a hash that decides where a record lands in a blob has to keep producing the same
    /// answer across runtime versions.
    /// </para>
    /// </remarks>
    public static int CanonicalHashCode(decimal value)
    {
        (UInt128 mantissa, int scale, bool isNegative) = Normalize(value);

        int hash = (int)(uint)mantissa;
        hash = (hash * 31) ^ (int)(uint)(mantissa >> 32);
        hash = (hash * 31) ^ (int)(uint)(mantissa >> 64);
        hash = (hash * 31) ^ scale;

        return isNegative ? ~hash : hash;
    }

    /// <summary>
    /// The most bytes the mantissa of <paramref name="marker"/>'s form can take, which is the limit of
    /// the variable-length integer it is written as.
    /// </summary>
    /// <exception cref="InvalidDataException"><paramref name="marker"/> begins no form.</exception>
    private static int MantissaLimit(byte marker) => (marker & 0xE0) switch
    {
        0x40 => VarInt.MaxVIntSize,
        0x60 => VarInt.MaxVLongSize,
        _ => throw new InvalidDataException(
            $"0x{marker:X2} does not begin any form of an encoded decimal."),
    };

    /// <summary>
    /// Refuses a sixteen-byte form that has fewer than sixteen bytes after its marker.
    /// </summary>
    /// <exception cref="InvalidDataException">The value is cut short.</exception>
    /// <remarks>
    /// Checked by the length reader as well as the decoder: a caller that skipped forward by a length
    /// longer than the bytes it was given would be reading the next field from somewhere past the end.
    /// </remarks>
    private static void ThrowIfFormDIsTruncated(ReadOnlySpan<byte> source)
    {
        if (source.Length < MaxEncodedLength)
        {
            throw new InvalidDataException(
                $"a decimal in the sixteen-byte form needs {MaxEncodedLength} bytes and has "
                + $"{source.Length}");
        }
    }

    /// <summary>
    /// Refuses a read from no bytes at all, which is a length somewhere saying zero rather than a value.
    /// </summary>
    /// <exception cref="InvalidDataException">There is nothing to read.</exception>
    private static void ThrowIfEmpty(ReadOnlySpan<byte> source)
    {
        if (source.IsEmpty)
        {
            throw new InvalidDataException("there are no bytes to read an encoded decimal from");
        }
    }

    /// <summary>
    /// Strips the trailing zeros from a decimal's mantissa, so that every representation of one value
    /// reduces to the same triple.
    /// </summary>
    private static (UInt128 Mantissa, int Scale, bool IsNegative) Normalize(decimal value)
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

        // -0m equals 0m, so it has to encode and hash the way 0m does.
        return (mantissa, scale, isNegative && mantissa != 0);
    }

    /// <summary>Rebuilds a decimal from a mantissa that fits in 64 bits, a scale and a sign.</summary>
    private static decimal Compose(ulong mantissa, int scale, bool isNegative) =>
        new((int)(uint)mantissa, (int)(uint)(mantissa >> 32), 0, isNegative, (byte)scale);
}
