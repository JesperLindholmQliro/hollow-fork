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

using System.Globalization;
using Hollow.Core.Memory.Encoding;

namespace Hollow.Tests.Core.Memory.Encoding;

/// <summary>
/// The variable-length decimal encoding. There is no Java counterpart: <c>Decimal</c> is this port's own
/// field type.
/// </summary>
public sealed class DecimalEncodingTests
{
    public static TheoryData<string> Roundtrips =>
    [
        "0", "1", "15", "16", "-1", "-0.0", "0.5", "1.25", "-1.25", "3.141592653",
        "2147483647", "2147483648", "-2147483648",
        "9223372036854775807", "9223372036854775808", "-9223372036854775808",
        "79228162514264337593543950335", "-79228162514264337593543950335",
        "0.0000000000000000000000000001", "0.1234567890123456789012345678",
        "12345678901234567890.12345678",
    ];

    [Theory]
    [MemberData(nameof(Roundtrips))]
    public void AValueSurvivesEncodingAndDecoding(string text)
    {
        decimal value = decimal.Parse(text, CultureInfo.InvariantCulture);

        Span<byte> buffer = stackalloc byte[DecimalEncoding.MaxEncodedLength];
        int length = DecimalEncoding.Encode(value, buffer);

        decimal? decoded = DecimalEncoding.Decode(buffer[..length], out int consumed);

        Assert.Equal(value, decoded);
        Assert.Equal(length, consumed);
        Assert.Equal(length, DecimalEncoding.EncodedLength(buffer));
    }

    [Theory]
    [InlineData("0", 0x00)]
    [InlineData("1", 0x01)]
    [InlineData("9", 0x09)]
    [InlineData("15", 0x0F)]
    public void AWholeNumberUpToFifteenIsOneByte(string text, byte expected)
    {
        Span<byte> buffer = stackalloc byte[DecimalEncoding.MaxEncodedLength];
        int length = DecimalEncoding.Encode(decimal.Parse(text, CultureInfo.InvariantCulture), buffer);

        Assert.Equal(1, length);
        Assert.Equal(expected, buffer[0]);
    }

    [Fact]
    public void AScaleThatOnlyTrailingZerosCarryIsNotPreserved()
    {
        // 1.000m, 1.00m and 1m are one value spelled three ways, and encode identically.
        Assert.Equal([0x01], DecimalEncoding.Encode(1.000m));
        Assert.Equal([0x01], DecimalEncoding.Encode(1.00m));
        Assert.Equal([0x01], DecimalEncoding.Encode(1m));
    }

    [Fact]
    public void AValueThatFitsThirtyTwoBitsTakesFormB()
    {
        Span<byte> buffer = stackalloc byte[DecimalEncoding.MaxEncodedLength];
        int length = DecimalEncoding.Encode(1.25m, buffer);

        // 010Z ssss, with Z set for positive and a scale of two, then the mantissa 125 in one byte.
        Assert.Equal(0x52, buffer[0]);
        Assert.Equal(2, length);
    }

    [Fact]
    public void ASignIsTheZBitAndNothingElse()
    {
        Span<byte> positive = stackalloc byte[DecimalEncoding.MaxEncodedLength];
        Span<byte> negative = stackalloc byte[DecimalEncoding.MaxEncodedLength];

        int positiveLength = DecimalEncoding.Encode(1.25m, positive);
        int negativeLength = DecimalEncoding.Encode(-1.25m, negative);

        Assert.Equal(positiveLength, negativeLength);
        Assert.Equal(positive[0] & ~0x10, negative[0]);
        Assert.Equal(positive[1..positiveLength].ToArray(), negative[1..negativeLength].ToArray());
    }

    [Fact]
    public void AValuePastThirtyTwoBitsTakesFormC()
    {
        Span<byte> buffer = stackalloc byte[DecimalEncoding.MaxEncodedLength];
        int length = DecimalEncoding.Encode(9223372036854775.807m, buffer);

        // 011Z ssss, with Z set and a scale of three.
        Assert.Equal(0x73, buffer[0]);
        Assert.True(length is > 3 and < DecimalEncoding.MaxEncodedLength);
    }

    [Theory]
    [InlineData("79228162514264337593543950335")]
    [InlineData("0.0000000000000000000000000001")]
    public void AMantissaPastSixtyFourBitsOrAScalePastFifteenTakesFormD(string text)
    {
        Span<byte> buffer = stackalloc byte[DecimalEncoding.MaxEncodedLength];
        int length = DecimalEncoding.Encode(decimal.Parse(text, CultureInfo.InvariantCulture), buffer);

        Assert.Equal(0xFE, buffer[0]);
        Assert.Equal(DecimalEncoding.MaxEncodedLength, length);
    }

    [Fact]
    public void FormDAlsoNormalises()
    {
        // A mantissa past 64 bits that still has a trailing zero to shed. Both spellings are one value,
        // so both have to produce one set of bytes.
        decimal scaled = decimal.Parse("1234567890123456789012345.0", CultureInfo.InvariantCulture);
        decimal plain = decimal.Parse("1234567890123456789012345", CultureInfo.InvariantCulture);

        Assert.Equal(DecimalEncoding.Encode(plain), DecimalEncoding.Encode(scaled));
    }

    [Fact]
    public void ANullIsTheOneByteMarker()
    {
        byte[] encoded = DecimalEncoding.Encode(null);

        Assert.Equal([DecimalEncoding.NullMarker], encoded);
        Assert.Null(DecimalEncoding.Decode(encoded, out int length));
        Assert.Equal(1, length);
    }

    [Fact]
    public void AMarkerThatBeginsNoFormIsRejected()
    {
        // 100x xxxx is not assigned, and VarInt's own null marker sits in that range.
        Assert.Throws<InvalidDataException>(() => DecimalEncoding.Decode([0x80, 0x01], out _));
    }

    [Fact]
    public void ValuesRunTogetherCanStillBeToldApart()
    {
        decimal[] values = [0m, -1.25m, 9223372036854775.807m, 79228162514264337593543950335m];
        byte[] encoded = [.. values.SelectMany(value => DecimalEncoding.Encode(value))];

        int offset = 0;

        foreach (decimal expected in values)
        {
            Assert.Equal(expected, DecimalEncoding.Decode(encoded.AsSpan(offset), out int length));
            Assert.Equal(length, DecimalEncoding.EncodedLength(encoded.AsSpan(offset)));
            offset += length;
        }

        Assert.Equal(encoded.Length, offset);
    }

    [Theory]
    [InlineData("1.000", "1")]
    [InlineData("-0.0", "0")]
    [InlineData("2.50", "2.5")]
    public void EqualValuesHashEqually(string left, string right)
    {
        Assert.Equal(
            DecimalEncoding.CanonicalHashCode(decimal.Parse(left, CultureInfo.InvariantCulture)),
            DecimalEncoding.CanonicalHashCode(decimal.Parse(right, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void DifferentValuesDoNotUsuallyHashEqually()
    {
        Assert.NotEqual(DecimalEncoding.CanonicalHashCode(1m), DecimalEncoding.CanonicalHashCode(-1m));
        Assert.NotEqual(DecimalEncoding.CanonicalHashCode(1m), DecimalEncoding.CanonicalHashCode(0.1m));
    }
}
