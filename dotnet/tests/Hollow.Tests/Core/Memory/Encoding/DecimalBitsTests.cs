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

using Hollow.Core.Memory.Encoding;

namespace Hollow.Tests.Core.Memory.Encoding;

/// <summary>
/// The sixteen bytes a decimal field stores. This encoding is a format extension, so its layout is
/// pinned rather than merely round-tripped: the stored bytes are a contract.
/// </summary>
public class DecimalBitsTests
{
    public static TheoryData<decimal> Values =>
    [
        0m,
        1m,
        -1m,
        0.1m,
        -0.1m,
        1.5m,
        1.50m,
        decimal.MaxValue,
        decimal.MinValue,
        0.0000000000000000000000000001m,
        123456789.123456789m,
        -0.0000000000000000000000000001m,
    ];

    [Theory]
    [MemberData(nameof(Values))]
    public void ValuesSurviveTheRoundTrip(decimal value)
    {
        (long low, long high) = DecimalBits.Pack(value);

        Assert.Equal(value, DecimalBits.Unpack(low, high));
    }

    /// <summary>
    /// Scale is part of the stored form, so a value and the same value with trailing zeros are stored
    /// differently even though they compare equal. That is what makes the encoding lossless.
    /// </summary>
    [Fact]
    public void ScaleIsPreserved()
    {
        Assert.Equal(1.5m, 1.50m);
        Assert.NotEqual(DecimalBits.Pack(1.5m), DecimalBits.Pack(1.50m));

        Assert.Equal("1.50", DecimalBits.Unpack(DecimalBits.Pack(1.50m).Low, DecimalBits.Pack(1.50m).High)
            .ToString(null, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The layout is the four integers <c>decimal.GetBits</c> returns: the low, middle and high words
    /// of the mantissa, then the flags word carrying scale and sign.
    /// </summary>
    [Theory]
    [MemberData(nameof(Values))]
    public void TheStoredWordsAreTheGetBitsIntegers(decimal value)
    {
        int[] bits = decimal.GetBits(value);
        (long low, long high) = DecimalBits.Pack(value);

        Assert.Equal(bits[0], (int)(low >> 32));
        Assert.Equal(bits[1], (int)low);
        Assert.Equal(bits[2], (int)(high >> 32));
        Assert.Equal(bits[3], (int)high);
    }

    /// <summary>
    /// The null sentinel has to be a pattern no decimal can produce, or a real value would read back as
    /// null.
    /// </summary>
    [Theory]
    [MemberData(nameof(Values))]
    public void NoRealValueCollidesWithTheNullSentinel(decimal value)
    {
        (long low, long high) = DecimalBits.Pack(value);

        Assert.False(DecimalBits.IsNull(low, high));
        Assert.True(DecimalBits.IsNull(DecimalBits.NullLow, DecimalBits.NullHigh));
    }

    /// <summary>
    /// The sentinel is all-ones, which is invalid because the flags word may only carry a scale of 0 to
    /// 28 and a sign bit.
    /// </summary>
    [Fact]
    public void TheNullSentinelIsNotADecimal() =>
        Assert.Throws<ArgumentException>(
            () => DecimalBits.Unpack(DecimalBits.NullLow, DecimalBits.NullHigh));

    /// <summary>
    /// Equal decimals must hash equally, whatever their scale, or a hash table keyed on one breaks.
    /// </summary>
    [Theory]
    [InlineData("1.5", "1.50")]
    [InlineData("1.5", "1.500000000000000000000")]
    [InlineData("0", "0.000")]
    [InlineData("0", "-0")]
    [InlineData("-2", "-2.00")]
    [InlineData("100", "100.0")]
    public void EqualValuesHashEquallyWhateverTheirScale(string first, string second)
    {
        decimal a = decimal.Parse(first, System.Globalization.CultureInfo.InvariantCulture);
        decimal b = decimal.Parse(second, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(a, b);
        Assert.Equal(DecimalBits.CanonicalHashCode(a), DecimalBits.CanonicalHashCode(b));
    }

    /// <summary>
    /// A hash that never distinguishes anything would satisfy the test above, so check that distinct
    /// values mostly do get distinct hashes.
    /// </summary>
    [Fact]
    public void DistinctValuesMostlyHashDistinctly()
    {
        HashSet<int> hashes = [];

        for (int i = -500; i < 500; i++)
        {
            hashes.Add(DecimalBits.CanonicalHashCode(i / 7m));
        }

        Assert.True(hashes.Count > 990, $"expected nearly 1000 distinct hashes, got {hashes.Count}");
    }

    [Fact]
    public void SignIsPartOfTheHash() =>
        Assert.NotEqual(DecimalBits.CanonicalHashCode(1.25m), DecimalBits.CanonicalHashCode(-1.25m));
}
