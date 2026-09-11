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
using System.Runtime.CompilerServices;

namespace Hollow.Tests;

/// <summary>
/// Runs the whole test suite under a culture that formats numbers differently from the invariant one.
/// </summary>
/// <remarks>
/// <para>
/// Culture-dependent formatting is the kind of defect that passes on the machine that wrote it and
/// fails somewhere else: a decimal point becomes a comma, a minus sign becomes U+2212, and a test that
/// compares against a literal starts failing on a colleague's laptop. Running every test under a
/// culture that differs from the invariant one in all of those ways turns that into a failure here
/// rather than there.
/// </para>
/// <para>
/// The culture is built by hand rather than by name (<c>sv-SE</c> would do) because this suite also has
/// to run where <c>DOTNET_SYSTEM_GLOBALIZATION_INVARIANT</c> is set, and a named culture collapses to
/// the invariant one there — which would quietly turn this guard off.
/// </para>
/// <para>
/// See "Culture-invariant formatting and parsing" in <c>PORTING.md</c>.
/// </para>
/// </remarks>
internal static class HostileCulture
{
    /// <summary>
    /// Switches the process to the hostile culture before any test runs.
    /// </summary>
    [ModuleInitializer]
    internal static void Install()
    {
        CultureInfo culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();

        culture.NumberFormat.NumberDecimalSeparator = ",";
        culture.NumberFormat.NumberGroupSeparator = " ";
        culture.NumberFormat.PercentDecimalSeparator = ",";
        culture.NumberFormat.CurrencyDecimalSeparator = ",";

        // U+2212 MINUS SIGN, which is what several real cultures use and what catches code that assumes
        // a negative number always starts with an ASCII hyphen.
        culture.NumberFormat.NegativeSign = "−";

        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
