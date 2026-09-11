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

namespace Hollow.Core.Util;

/// <summary>
/// Renders a value as text the same way on every machine.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every conversion between a number and text in this codebase goes through the invariant
/// culture</strong> — see "Culture-invariant formatting and parsing" in <c>PORTING.md</c>. These
/// extensions exist so that a number inside an interpolated string can say so without the call site
/// becoming unreadable: <c>$"ordinal {ordinal.Invariant()}"</c> rather than
/// <c>$"ordinal {ordinal.ToString(CultureInfo.InvariantCulture)}"</c>.
/// </para>
/// <para>
/// An interpolated string is the case that needs the help. A bare <c>value.ToString()</c> is caught by
/// the CA1305 analyzer, which is an error here, but interpolation is not analyzed at all — and neither
/// is <c>ToString(null, null)</c>, which satisfies CA1305 while still using the current culture.
/// </para>
/// </remarks>
internal static class InvariantFormatting
{
    /// <summary>Renders an <see cref="int"/> in the invariant culture.</summary>
    internal static string Invariant(this int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Renders a <see cref="long"/> in the invariant culture.</summary>
    internal static string Invariant(this long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Renders a <see cref="float"/> in the invariant culture.</summary>
    internal static string Invariant(this float value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Renders a <see cref="double"/> in the invariant culture.</summary>
    internal static string Invariant(this double value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Renders a <see cref="decimal"/> in the invariant culture.</summary>
    internal static string Invariant(this decimal value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Renders a boxed value in the invariant culture, which is what a key read back out of a record
    /// is.
    /// </summary>
    internal static string Invariant(this object? value) =>
        value switch
        {
            null => string.Empty,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

    /// <summary>
    /// Joins values into text, rendering each one in the invariant culture.
    /// </summary>
    internal static string JoinInvariant<T>(string separator, IEnumerable<T> values) =>
        string.Join(separator, values.Select(value => ((object?)value).Invariant()));
}
