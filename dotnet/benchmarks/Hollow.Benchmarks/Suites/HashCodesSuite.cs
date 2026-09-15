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
using System.Text;
using Hollow.Core.Memory.Encoding;

namespace Hollow.Benchmarks.Suites;

/// <summary>
/// The port of <c>HashCodesBenchmark</c>: what each of the hash functions the format depends on
/// costs, over one byte, ten and a thousand.
/// </summary>
/// <remarks>
/// Java names three of these <c>hashCode</c>; this port calls all of them <c>HashCodes.Compute</c>,
/// because a static method named after <c>object.GetHashCode</c> reads as an override.
/// </remarks>
internal sealed class HashCodesSuite : BenchmarkSuite
{
    internal override string Name => "HashCodes";

    internal override IEnumerable<BenchmarkCase> Cases(double scale)
    {
        foreach (int length in (int[])[1, 10, 1000])
        {
            // A fixed seed rather than Java's ThreadLocalRandom: the bytes being hashed do not change
            // the cost, but a run that cannot be repeated is not worth reporting.
            Random random = new(42);

            byte[] asciiData = new byte[length];
            byte[] multibyteData = new byte[length];
            StringBuilder ascii = new(length);
            StringBuilder multibyte = new(length);

            for (int i = 0; i < length; i++)
            {
                asciiData[i] = (byte)random.Next(0x80);
                multibyteData[i] = (byte)random.Next(char.MaxValue);
                ascii.Append((char)asciiData[i]);
                multibyte.Append((char)random.Next(char.MaxValue));
            }

            string asciiKey = ascii.ToString();
            string multibyteKey = multibyte.ToString();

            int intKey = random.Next();
            long longKey = random.NextInt64();

            string parameters = string.Create(CultureInfo.InvariantCulture, $"length={length}");

            yield return new BenchmarkCase(
                "HashInt", parameters, () => Blackhole.Consume(HashCodes.HashInt(intKey)));

            yield return new BenchmarkCase(
                "HashLong", parameters, () => Blackhole.Consume(HashCodes.HashLong(longKey)));

            yield return new BenchmarkCase(
                "HashString", parameters, () => Blackhole.Consume(HashCodes.Compute(asciiKey)));

            yield return new BenchmarkCase(
                "HashStringMultibyte",
                parameters,
                () => Blackhole.Consume(HashCodes.Compute(multibyteKey)));

            yield return new BenchmarkCase(
                "HashBytes", parameters, () => Blackhole.Consume(HashCodes.Compute(asciiData)));
        }
    }
}
