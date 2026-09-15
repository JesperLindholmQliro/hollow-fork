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
using Hollow.Core.Memory.Encoding;
using Hollow.Core.Memory.Pool;

namespace Hollow.Benchmarks.Suites;

/// <summary>
/// The port of <c>ReadWriteFixedLengthElementArrayTest</c>: reading and writing the bit string every
/// record's fixed-length fields live in.
/// </summary>
/// <remarks>
/// <para>
/// Java's <c>writePlain</c> case is not here. It exists to price the release store in
/// <c>setElementValue</c>, which it does by benchmarking a copy of the class —
/// <c>FixedLengthElementArrayPlainPut</c>, one of the two classes in <c>hollow-perf/src/main</c> —
/// with <c>Unsafe.putOrderedLong</c> swapped for <c>Unsafe.putLong</c>. This port has no release
/// store there to price: <c>SegmentedLongArray.Set</c> is an ordinary array store, and publication is
/// ordered once, by the fence before the ordinal is written, rather than per word. So both of those
/// classes and the benchmark that compares them fall away together.
/// </para>
/// <para>
/// Java's <c>read</c> and <c>readLarge</c> are still two cases, though in this port they run the same
/// code: <c>GetElementValue</c> forwards to <c>GetLargeElementValue</c>, because the port dropped
/// Java's unaligned single-read path. Keeping both says so — they should measure the same, and a run
/// where they do not is worth looking into.
/// </para>
/// </remarks>
internal sealed class FixedLengthElementArraySuite : BenchmarkSuite
{
    internal override string Name => "FixedLengthElementArray";

    internal override IEnumerable<BenchmarkCase> Cases(double scale)
    {
        const int bitSize = 4096;

        foreach (int bitsPerElement in (int[])[1, 8, 32, 60])
        {
            FixedLengthElementArray array = new(WastefulRecycler.DefaultInstance, bitSize);

            Random random = new(42);
            long value = bitsPerElement >= 63 ? random.NextInt64() : random.NextInt64(1L << bitsPerElement);

            string parameters = string.Create(
                CultureInfo.InvariantCulture, $"bitSize={bitSize}, bitsPerElement={bitsPerElement}");

            yield return new BenchmarkCase(
                "Read",
                parameters,
                () =>
                {
                    long sum = 0;

                    for (int i = 0; i < bitSize - bitsPerElement; i++)
                    {
                        sum += array.GetElementValue(i, bitsPerElement);
                    }

                    Blackhole.Consume(sum);
                });

            yield return new BenchmarkCase(
                "ReadLarge",
                parameters,
                () =>
                {
                    long sum = 0;

                    for (int i = 0; i < bitSize - bitsPerElement; i++)
                    {
                        sum += array.GetLargeElementValue(i, bitsPerElement);
                    }

                    Blackhole.Consume(sum);
                });

            yield return new BenchmarkCase(
                "Write",
                parameters,
                () =>
                {
                    // Cleared each time: SetElementValue ORs into the word it lands in, so writing
                    // over a bit string that is already full of ones measures nothing.
                    FixedLengthElementArray target = new(WastefulRecycler.DefaultInstance, bitSize);

                    for (int i = 0; i < bitSize - bitsPerElement; i++)
                    {
                        target.SetElementValue(i, bitsPerElement, value);
                    }

                    Blackhole.Consume(target);
                });
        }
    }
}
