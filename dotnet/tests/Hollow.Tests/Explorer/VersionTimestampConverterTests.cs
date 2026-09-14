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
using Hollow.Explorer.History;

namespace Hollow.Tests.Explorer;

/// <summary>
/// A version is only a number, but a producer that stamps it with the clock leaves something readable
/// behind. These check that it is read when it is there and left alone when it is not.
/// </summary>
public class VersionTimestampConverterTests
{
    [Fact]
    public void AClockStampedVersionReadsAsTheMomentItNames()
    {
        // 2024-01-15 09:30:00.123 UTC
        Assert.Equal(
            "[01/15 09:30 UTC] ", VersionTimestampConverter.GetTimestamp(20240115093000123L, TimeZoneInfo.Utc));
    }

    [Fact]
    public void AnotherZoneShiftsTheMomentAndSaysSo()
    {
        TimeZoneInfo minusSeven = TimeZoneInfo.CreateCustomTimeZone(
            "Test/MinusSeven", TimeSpan.FromHours(-7), "Test/MinusSeven", "Test/MinusSeven");

        // Java names the zone from the JDK's abbreviation table; .NET has none, so the offset stands in.
        Assert.Equal(
            "[01/15 02:30 UTC-07:00] ",
            VersionTimestampConverter.GetTimestamp(20240115093000123L, minusSeven));
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(20240115093000L)]
    [InlineData(99999999999999999L)]
    [InlineData(20241315093000123L)]
    public void AVersionThatIsNotATimestampIsShownUnchanged(long version)
    {
        Assert.Equal(
            version.ToString(CultureInfo.InvariantCulture),
            VersionTimestampConverter.GetTimestamp(version, TimeZoneInfo.Utc));
    }
}
