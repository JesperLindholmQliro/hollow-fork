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
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace Hollow.Benchmarks;

/// <summary>
/// Writes results in the shape JMH's <c>-rf json</c> writes them.
/// </summary>
/// <remarks>
/// <para>
/// Not because anything here needs JSON, but because the point of comparing this port against Java is
/// to read both result files with one piece of code. Matching JMH's shape means
/// <c>tools/compare-benchmarks.py</c> has one parser rather than two, and means any tool that already
/// reads JMH output reads this too.
/// </para>
/// <para>
/// Only the fields that carry a measurement are written. JMH's file also records the JVM, its flags,
/// the fork count and the per-iteration raw data; none of that has a counterpart here, and inventing
/// values for it would be worse than leaving it out.
/// </para>
/// </remarks>
internal static class JmhJson
{
    /// <summary>Writes <paramref name="results"/> to <paramref name="path"/>.</summary>
    internal static void Write(string path, IEnumerable<BenchmarkResult> results)
    {
        JsonArray entries = [];

        foreach (BenchmarkResult result in results)
        {
            entries.Add(new JsonObject
            {
                ["benchmark"] = result.FullName,
                ["mode"] = "avgt",
                ["params"] = Parameters(result.Parameters),
                ["primaryMetric"] = new JsonObject
                {
                    // Nanoseconds throughout. The console report picks the unit the number reads best
                    // in; a file that is going to be compared against another file has no such luxury.
                    ["score"] = result.Measurement.NanosecondsPerOperation,
                    ["scoreError"] = result.Measurement.Error,
                    ["scoreUnit"] = "ns/op",
                },
            });
        }

        // A single measurement iteration leaves no degrees of freedom, so the error bar is NaN.
        // System.Text.Json refuses to write that by default and throws; the named literals write it
        // as the string "NaN", which is what JMH's own reader and every JSON parser expect. Reporting
        // it as zero instead would claim a precision the run does not have.
        File.WriteAllText(
            path,
            entries.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            }));
    }

    /// <summary>
    /// Splits <c>"size=1000, querySize=1"</c> into the object JMH writes for <c>@Param</c>s.
    /// </summary>
    /// <remarks>
    /// JMH's values are strings even where they are numbers, because a <c>@Param</c> is declared as
    /// text. These follow, so that a comparison can match on them without knowing which side wrote the
    /// file. Anything that is not <c>name=value</c> is kept whole under <c>"params"</c>, which loses
    /// nothing and says plainly that it was not understood.
    /// </remarks>
    private static JsonNode Parameters(string parameters)
    {
        if (parameters.Length == 0)
        {
            return new JsonObject();
        }

        JsonObject parsed = [];

        foreach (string part in parameters.Split(',', StringSplitOptions.TrimEntries))
        {
            int equals = part.IndexOf('=', StringComparison.Ordinal);

            if (equals <= 0)
            {
                return new JsonObject { ["params"] = parameters };
            }

            parsed[part[..equals]] = part[(equals + 1)..];
        }

        return parsed;
    }
}

/// <summary>One case's name and what it measured.</summary>
internal readonly record struct BenchmarkResult(
    string Suite, string Name, string Parameters, Measurement Measurement)
{
    /// <summary>The name a report prints, which is also the name a comparison matches on.</summary>
    internal string FullName => string.Create(CultureInfo.InvariantCulture, $"{Suite}.{Name}");
}
