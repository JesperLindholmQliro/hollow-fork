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

namespace Hollow.Explorer.History;

/// <summary>
/// Reads a version number as the moment it was produced, when it happens to be one.
/// </summary>
/// <remarks>
/// <para>
/// A version is just a long as far as Hollow is concerned, but a producer that stamps versions with the
/// clock leaves something readable behind: <c>20240115093000123</c> is a timestamp in disguise. The
/// pages try to read one, and show the number unchanged when it is not.
/// </para>
/// <para>
/// Named <c>VersionTimestampConverter</c> in Java, which hardcodes a Pacific time zone constant and a
/// mutable static millisecond offset. Neither survives here: the time zone is the caller's to choose,
/// and a process-wide mutable offset in a display helper is a trap.
/// </para>
/// </remarks>
public static class VersionTimestampConverter
{
    private const string VersionFormat = "yyyyMMddHHmmssfff";

    /// <summary>
    /// <paramref name="version"/> as a short date and time in <paramref name="timeZone"/>, or the
    /// version unchanged when it does not read as a timestamp.
    /// </summary>
    public static string GetTimestamp(long version, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        string text = version.ToString(CultureInfo.InvariantCulture);

        if (!DateTime.TryParseExact(
                text,
                VersionFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTime utc))
        {
            return text;
        }

        DateTime local = TimeZoneInfo.ConvertTimeFromUtc(utc, timeZone);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"[{local:MM/dd HH:mm} {Abbreviate(timeZone, utc)}] ");
    }

    /// <summary>
    /// A short name for the zone, since <see cref="TimeZoneInfo"/> has no abbreviation of its own.
    /// </summary>
    /// <remarks>
    /// Java gets <c>PST</c> or <c>PDT</c> from the JDK's own table. .NET has no such table, so UTC is
    /// named and anything else is given its offset, which says the same thing unambiguously.
    /// </remarks>
    private static string Abbreviate(TimeZoneInfo timeZone, DateTime utc)
    {
        TimeSpan offset = timeZone.GetUtcOffset(utc);

        return offset == TimeSpan.Zero
            ? "UTC"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"UTC{(offset < TimeSpan.Zero ? '-' : '+')}{offset.Duration():hh\\:mm}");
    }
}
