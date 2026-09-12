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

namespace Hollow.Explorer;

/// <summary>
/// Writing a byte count as something a person reads at a glance.
/// </summary>
/// <remarks>
/// Java calls this <c>HollowDiffUtil.formatBytes</c>, which is a name from where it happened to live
/// rather than what it does.
/// </remarks>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];

    /// <summary>
    /// <paramref name="sizeInBytes"/> in the largest unit it fills, to two decimal places.
    /// </summary>
    /// <remarks>
    /// The arithmetic runs in <see cref="double"/> throughout, which is what Java's does as soon as it
    /// takes a logarithm — so the rounding matches, and <see cref="long.MinValue"/> comes out as -8 EiB
    /// rather than overflowing the way negating it would.
    /// </remarks>
    public static string Format(long sizeInBytes)
    {
        if (sizeInBytes == 0)
        {
            return "0 B";
        }

        string sign = sizeInBytes < 0 ? "-" : "";
        double magnitude = Math.Abs((double)sizeInBytes);

        int unit = (int)(Math.Log10(magnitude) / Math.Log10(1024));
        double scaled = magnitude / Math.Pow(1024, unit);

        return sign + scaled.ToString("#,##0.##", CultureInfo.InvariantCulture) + " " + Units[unit];
    }
}
