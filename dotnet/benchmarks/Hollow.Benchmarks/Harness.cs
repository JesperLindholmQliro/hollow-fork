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

using System.Diagnostics;
using System.Globalization;

namespace Hollow.Benchmarks;

/// <summary>
/// Somewhere for a result to go that the compiler cannot prove nobody reads.
/// </summary>
/// <remarks>
/// JMH's <c>Blackhole</c>, minus the parts that defeat a JIT this port does not run on. A volatile
/// store is enough to stop RyuJIT folding away the work that produced the value.
/// </remarks>
internal static class Blackhole
{
    private static object? _reference;
    private static long _value;

    internal static void Consume(object? value) => Volatile.Write(ref _reference, value);

    internal static void Consume(long value) => Volatile.Write(ref _value, value);

    internal static void Consume(int value) => Consume((long)value);
}

/// <summary>One thing to measure, with whatever has to happen before and after it.</summary>
/// <param name="Name">What it is called in the report — the benchmark method's name in Java.</param>
/// <param name="Parameters">The parameter values this case was built for, for the report.</param>
/// <param name="Run">One operation. Whatever it produces goes to the <see cref="Blackhole"/>.</param>
/// <param name="Setup">Run once before the case, outside the measurement.</param>
/// <param name="TearDown">Run once after the case, whatever happened.</param>
internal sealed record BenchmarkCase(
    string Name,
    string Parameters,
    Action Run,
    Action? Setup = null,
    Action? TearDown = null);

/// <summary>A group of cases that belong to one Java benchmark class.</summary>
internal abstract class BenchmarkSuite
{
    /// <summary>What the suite is called, which is what <c>--only</c> matches against.</summary>
    internal abstract string Name { get; }

    /// <summary>
    /// The cases, built at the scale asked for. Building a case may be expensive — a million-record
    /// state engine, say — so this is called once, when the suite is about to run.
    /// </summary>
    internal abstract IEnumerable<BenchmarkCase> Cases(double scale);

    /// <summary>
    /// <paramref name="count"/> scaled down for a quick run, never below one.
    /// </summary>
    private protected static int Scaled(int count, double scale) =>
        Math.Max(1, (int)(count * scale));
}

/// <summary>What one case measured.</summary>
/// <param name="NanosecondsPerOperation">The mean across the measurement iterations.</param>
/// <param name="Error">Half the 99.9% confidence interval, as JMH reports it.</param>
/// <param name="OperationsPerIteration">How many operations each iteration timed.</param>
internal readonly record struct Measurement(
    double NanosecondsPerOperation, double Error, long OperationsPerIteration);

/// <summary>How long to spend on each case.</summary>
internal sealed record RunOptions
{
    internal int WarmupIterations { get; init; } = 3;

    internal int MeasurementIterations { get; init; } = 5;

    internal TimeSpan IterationTime { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Times a case the way JMH's average-time mode does: calibrate, warm up, then measure.
/// </summary>
/// <remarks>
/// BenchmarkDotNet is what this would be if it could be restored. It cannot be on every machine this
/// port is built on, and a benchmark suite that will not build is worth less than a rough one that
/// will, so the parts that matter are here: a calibrated operation count so the timer's resolution
/// does not dominate, warmup iterations that are thrown away, and an error bar rather than a single
/// number. What is <em>not</em> here is everything BenchmarkDotNet does beyond that — process
/// isolation per case, the pilot/overhead subtraction, outlier removal, memory diagnostics. Read the
/// numbers as ratios between cases in one run, not as absolutes to quote.
/// </remarks>
internal static class BenchmarkRunner
{
    private const double NanosecondsPerSecond = 1_000_000_000.0;

    // Student's t at 99.9%, two-sided, for the small sample counts a benchmark run uses. JMH reports
    // the same interval; taking the value for n-1 degrees of freedom keeps a five-iteration run from
    // claiming a normal distribution's error bar.
    private static readonly double[] TwoSidedT999 =
    [
        0.0, 636.619, 31.599, 12.924, 8.610, 6.869, 5.959, 5.408, 5.041, 4.781, 4.587,
    ];

    internal static Measurement Run(BenchmarkCase benchmark, RunOptions options)
    {
        benchmark.Setup?.Invoke();

        try
        {
            long operations = Calibrate(benchmark.Run, options.IterationTime);

            for (int i = 0; i < options.WarmupIterations; i++)
            {
                TimeIteration(benchmark.Run, operations);
            }

            double[] samples = new double[options.MeasurementIterations];

            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = TimeIteration(benchmark.Run, operations) / operations;
            }

            return new Measurement(Mean(samples), Error(samples), operations);
        }
        finally
        {
            benchmark.TearDown?.Invoke();
        }
    }

    /// <summary>
    /// How many operations fill <paramref name="target"/>, found by doubling until they do.
    /// </summary>
    /// <remarks>
    /// The point is the timer, not the schedule: one operation of a hundred nanoseconds measured on
    /// its own is mostly <see cref="Stopwatch"/>. Timing a batch of them amortises that away.
    /// </remarks>
    private static long Calibrate(Action run, TimeSpan target)
    {
        double targetNanoseconds = target.TotalMilliseconds * 1_000_000.0;
        long operations = 1;

        while (true)
        {
            double elapsed = TimeIteration(run, operations);

            if (elapsed >= targetNanoseconds || operations >= 1L << 40)
            {
                return operations;
            }

            // Jump straight to the count the measurement suggests rather than crawling up to it, but
            // never more than an order of magnitude at a time, so one fast outlier cannot overshoot
            // into an iteration that takes minutes.
            double factor = elapsed <= 0 ? 10.0 : Math.Min(10.0, (targetNanoseconds / elapsed) + 1.0);
            operations = Math.Max(operations + 1, (long)(operations * factor));
        }
    }

    /// <summary>Nanoseconds taken by <paramref name="operations"/> calls of <paramref name="run"/>.</summary>
    private static double TimeIteration(Action run, long operations)
    {
        long start = Stopwatch.GetTimestamp();

        for (long i = 0; i < operations; i++)
        {
            run();
        }

        long ticks = Stopwatch.GetTimestamp() - start;

        return ticks * (NanosecondsPerSecond / Stopwatch.Frequency);
    }

    private static double Mean(double[] samples) => samples.Sum() / samples.Length;

    private static double Error(double[] samples)
    {
        if (samples.Length < 2)
        {
            return double.NaN;
        }

        double mean = Mean(samples);
        double variance = samples.Sum(sample => (sample - mean) * (sample - mean)) / (samples.Length - 1);
        double standardError = Math.Sqrt(variance / samples.Length);

        double t = samples.Length - 1 < TwoSidedT999.Length
            ? TwoSidedT999[samples.Length - 1]
            : 3.291;

        return t * standardError;
    }

    /// <summary>Prints a report in the shape JMH's is, so the two can be read side by side.</summary>
    internal static void Report(string suite, string name, string parameters, Measurement measurement)
    {
        (double score, double error, string unit) = Scale(measurement);

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{suite + "." + name,-58} {parameters,-30} {score,14:N3} {"± " + error.ToString("N3", CultureInfo.InvariantCulture),14}  {unit}"));
    }

    internal static void ReportHeader() =>
        Console.WriteLine($"{"Benchmark",-58} {"(params)",-30} {"Score",14} {"Error",14}  Units");

    /// <summary>
    /// Picks the unit the number reads best in, rather than reporting everything in nanoseconds.
    /// </summary>
    private static (double Score, double Error, string Unit) Scale(Measurement measurement) =>
        measurement.NanosecondsPerOperation switch
        {
            >= 1_000_000_000 => (
                measurement.NanosecondsPerOperation / 1_000_000_000, measurement.Error / 1_000_000_000, "s/op"),
            >= 1_000_000 => (
                measurement.NanosecondsPerOperation / 1_000_000, measurement.Error / 1_000_000, "ms/op"),
            >= 1_000 => (
                measurement.NanosecondsPerOperation / 1_000, measurement.Error / 1_000, "us/op"),
            _ => (measurement.NanosecondsPerOperation, measurement.Error, "ns/op"),
        };
}
