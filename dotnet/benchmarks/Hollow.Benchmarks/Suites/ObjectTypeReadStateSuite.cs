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
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Engine;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Benchmarks.Suites;

/// <summary>
/// The port of <c>HollowObjectTypeReadStateLongBenchmark</c>: reading a long field, at a width that
/// fits one word and at a width that does not.
/// </summary>
/// <remarks>
/// A field's width comes from the largest value written to it, so pinning the maximum is what decides
/// which read path runs. In Java the two paths are <c>getElementValue</c> and
/// <c>getLargeElementValue</c>; this port has only the second, so the two cases should measure the
/// same. That they do is the point — it is the evidence that dropping Java's unaligned read cost
/// nothing.
/// </remarks>
internal sealed class ObjectTypeReadLongSuite : BenchmarkSuite
{
    internal override string Name => "HollowObjectTypeReadStateLong";

    internal override IEnumerable<BenchmarkCase> Cases(double scale)
    {
        int records = Scaled(1_000_000, scale);
        int reads = Scaled(1_000_000, scale);

        foreach (int maxBits in (int[])[40, 62])
        {
            HollowWriteStateEngine writeEngine = new();
            HollowObjectMapper mapper = new(writeEngine);
            mapper.InitializeTypeState(typeof(LongHolder));

            Random random = new(42);

            // Zig-zag encoding roughly doubles the magnitude, so a bound of maxBits-1 raw lands at
            // about maxBits encoded. The largest value is written first and pinned, so the field's
            // width does not depend on how many records the run happened to generate.
            long bound = 1L << Math.Max(1, maxBits - 1);

            mapper.Add(new LongHolder { Value = bound - 1 });

            for (int i = 1; i < records; i++)
            {
                mapper.Add(new LongHolder { Value = random.NextInt64() & (bound - 1) });
            }

            int[] readOrder = new int[reads];

            for (int i = 0; i < reads; i++)
            {
                readOrder[i] = random.Next(records);
            }

            HollowReadStateEngine readEngine = RoundTripper.RoundTripSnapshot(writeEngine);
            IHollowObjectTypeDataAccess dataAccess =
                (IHollowObjectTypeDataAccess)readEngine.GetTypeDataAccess(nameof(LongHolder))!;

            yield return new BenchmarkCase(
                "ReadLong",
                string.Create(
                    CultureInfo.InvariantCulture, $"records={records}, reads={reads}, maxBits={maxBits}"),
                () =>
                {
                    long sum = 0;

                    foreach (int ordinal in readOrder)
                    {
                        sum += dataAccess.ReadLong(ordinal, 0);
                    }

                    Blackhole.Consume(sum);
                });
        }
    }

    internal sealed class LongHolder
    {
        public required long Value { get; init; }
    }
}

/// <summary>
/// The port of <c>HollowObjectTypeReadStateShardBenchmark</c>: reading a string back out of the
/// variable-length storage, across the lengths a real dataset holds.
/// </summary>
internal sealed class ObjectTypeReadStringSuite : BenchmarkSuite
{
    internal override string Name => "HollowObjectTypeReadStateShard";

    internal override IEnumerable<BenchmarkCase> Cases(double scale)
    {
        int stored = Scaled(100_000, scale);
        int reads = Scaled(500, scale);

        foreach (int maxStringLength in (int[])[5, 25, 50, 150, 1000])
        {
            const int probabilityUnicode = 10;

            // Seeded, where Java uses a bare Random: the string lengths decide what this measures, so
            // a run that cannot be repeated is not worth comparing against another.
            Random random = new(42);

            HollowWriteStateEngine writeEngine = new();
            HollowObjectMapper mapper = new(writeEngine);
            mapper.InitializeTypeState(typeof(string));

            for (int i = 0; i < stored; i++)
            {
                mapper.Add(RandomString(random, i, maxStringLength, probabilityUnicode));
            }

            int[] readOrder = new int[reads];

            for (int i = 0; i < reads; i++)
            {
                readOrder[i] = random.Next(stored);
            }

            HollowReadStateEngine readEngine = RoundTripper.RoundTripSnapshot(writeEngine);
            IHollowObjectTypeDataAccess dataAccess =
                (IHollowObjectTypeDataAccess)readEngine.GetTypeDataAccess("String")!;

            yield return new BenchmarkCase(
                "ReadString",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"stored={stored}, reads={reads}, maxStringLength={maxStringLength}"),
                () =>
                {
                    foreach (int ordinal in readOrder)
                    {
                        Blackhole.Consume(dataAccess.ReadString(ordinal, 0));
                    }
                });
        }
    }

    /// <summary>
    /// A string that starts with its own index, so that no two are equal and the mapper cannot
    /// deduplicate the dataset away, padded to a random length with a tenth of it outside ASCII.
    /// </summary>
    internal static string RandomString(
        Random random, int index, int maxStringLength, int probabilityUnicode)
    {
        StringBuilder builder = new();
        builder.Append(CultureInfo.InvariantCulture, $"string_{index}_");

        int remaining = random.Next(maxStringLength) - builder.Length + 1;

        for (int i = 0; i < remaining; i++)
        {
            builder.Append(
                random.Next(100) < probabilityUnicode ? 'ሾ' : (char)(random.Next(26) + 'a'));
        }

        return builder.ToString();
    }
}
