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
using Hollow.Benchmarks;

// The port of hollow-perf: what the parts of Hollow that sit on a hot path cost.
//
// Java runs these under JMH. There is no JMH here and no BenchmarkDotNet either — see Harness.cs for
// why — so this is a small harness that does the parts that matter and says plainly what it leaves
// out. Read the numbers as ratios between cases in one run.

BenchmarkSuite[] suites = [.. typeof(BenchmarkSuite).Assembly.GetTypes()
    .Where(type => !type.IsAbstract && typeof(BenchmarkSuite).IsAssignableFrom(type))
    .Select(type => (BenchmarkSuite)Activator.CreateInstance(type)!)
    .OrderBy(suite => suite.Name, StringComparer.Ordinal)];

string? only = null;
string? jsonPath = null;
double scale = 1.0;
RunOptions options = new();

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--list":
            foreach (BenchmarkSuite suite in suites)
            {
                Console.WriteLine(suite.Name);
            }

            return 0;

        case "--only":
            only = Next();
            break;

        case "--json":
            jsonPath = Next();
            break;

        case "--scale":
            scale = double.Parse(Next(), CultureInfo.InvariantCulture);
            break;

        case "--warmup":
            options = options with { WarmupIterations = int.Parse(Next(), CultureInfo.InvariantCulture) };
            break;

        case "--iterations":
            options = options with
            {
                MeasurementIterations = int.Parse(Next(), CultureInfo.InvariantCulture),
            };
            break;

        case "--time":
            options = options with
            {
                IterationTime = TimeSpan.FromSeconds(double.Parse(Next(), CultureInfo.InvariantCulture)),
            };
            break;

        default:
            Console.Error.WriteLine($"unknown option {args[i]}");

            return 1;
    }

    string Next() =>
        i + 1 < args.Length
            ? args[++i]
            : throw new ArgumentException($"{args[i]} was given without a value", nameof(args));
}

BenchmarkSuite[] selected = only is null
    ? suites
    : [.. suites.Where(suite => suite.Name.Contains(only, StringComparison.OrdinalIgnoreCase))];

if (selected.Length == 0)
{
    Console.Error.WriteLine($"nothing matched {only}");

    return 1;
}

Console.WriteLine(string.Create(
    CultureInfo.InvariantCulture,
    $"# {options.WarmupIterations} warmup and {options.MeasurementIterations} measurement iterations "
    + $"of {options.IterationTime.TotalSeconds:N1}s, scale {scale:N3}"));
Console.WriteLine($"# {Environment.ProcessorCount} processors, {Environment.OSVersion}");
Console.WriteLine();

BenchmarkRunner.ReportHeader();

List<BenchmarkResult> results = [];

foreach (BenchmarkSuite suite in selected)
{
    foreach (BenchmarkCase benchmark in suite.Cases(scale))
    {
        Measurement measurement = BenchmarkRunner.Run(benchmark, options);

        BenchmarkRunner.Report(suite.Name, benchmark.Name, benchmark.Parameters, measurement);

        results.Add(new BenchmarkResult(suite.Name, benchmark.Name, benchmark.Parameters, measurement));
    }
}

if (jsonPath is not null)
{
    JmhJson.Write(jsonPath, results);
    Console.WriteLine();
    Console.WriteLine($"# {results.Count} result(s) written to {jsonPath}");
}

return 0;
