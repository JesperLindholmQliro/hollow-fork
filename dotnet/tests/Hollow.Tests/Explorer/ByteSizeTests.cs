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
using Hollow.Explorer;

namespace Hollow.Tests.Explorer;

/// <summary>
/// Writing a byte count for a person, ported from Java's <c>HollowDiffUtilTest</c>.
/// </summary>
public class ByteSizeTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(-10, "-10 B")]
    [InlineData(2, "2 B")]
    [InlineData(10, "10 B")]
    public void ASmallCountIsJustItself(long bytes, string expected) =>
        Assert.Equal(expected, ByteSize.Format(bytes));

    /// <summary>
    /// A whole number of some unit is written as that number of that unit, for each unit in turn.
    /// </summary>
    [Theory]
    [InlineData(10, "KiB")]
    [InlineData(20, "MiB")]
    [InlineData(30, "GiB")]
    [InlineData(40, "TiB")]
    [InlineData(50, "PiB")]
    public void AWholeNumberOfAUnitIsWrittenInThatUnit(int shift, string unit)
    {
        foreach (long multiple in (long[])[-100, 50, 30, 100])
        {
            // Named explicitly, because the ambient culture writes a negative with a minus sign rather
            // than the hyphen a byte count is written with.
            Assert.Equal(
                $"{multiple.ToString(CultureInfo.InvariantCulture)} {unit}",
                ByteSize.Format(multiple * (1L << shift)));
        }
    }

    /// <summary>
    /// A count stays in the largest unit it fills whole, so a thousand of one unit is not yet the next.
    /// </summary>
    [Theory]
    [InlineData(-1023, "-1,023 B")]
    [InlineData(-1024, "-1 KiB")]
    [InlineData(1000L << 40, "1,000 TiB")]
    [InlineData(1024L << 40, "1 PiB")]
    public void AUnitIsOnlyLeftOnceTheNextIsFull(long bytes, string expected) =>
        Assert.Equal(expected, ByteSize.Format(bytes));

    /// <summary>
    /// Anything that does not divide evenly gets two decimal places, and no more.
    /// </summary>
    [Theory]
    [InlineData(100_000_000, "95.37 MiB")]
    [InlineData(-10_000_000, "-9.54 MiB")]
    [InlineData(2001, "1.95 KiB")]
    [InlineData(20_000, "19.53 KiB")]
    [InlineData(200_000_000_000, "186.26 GiB")]
    public void ARemainderIsWrittenToTwoPlaces(long bytes, string expected) =>
        Assert.Equal(expected, ByteSize.Format(bytes));

    /// <summary>
    /// The extremes of the range are the ones a byte count is least likely to have been tried on, and
    /// negating the lowest one is where the Java it was ported from would have overflowed.
    /// </summary>
    [Theory]
    [InlineData(long.MaxValue, "8 EiB")]
    [InlineData(long.MinValue, "-8 EiB")]
    public void TheEndsOfTheRangeAreWrittenRatherThanOverflowing(long bytes, string expected) =>
        Assert.Equal(expected, ByteSize.Format(bytes));
}
