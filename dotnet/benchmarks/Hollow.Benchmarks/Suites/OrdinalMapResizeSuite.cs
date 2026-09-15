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
using Hollow.Core.Memory;

namespace Hollow.Benchmarks.Suites;

/// <summary>
/// The port of <c>OrdinalMapResize</c>: what it costs to fill an ordinal map that has to grow,
/// against one sized for what it is about to be given.
/// </summary>
/// <remarks>
/// The answer Java records is between two and three times, which is the argument for
/// <c>HollowWriteStateEngine</c> sizing its maps from the previous cycle.
/// </remarks>
internal sealed class OrdinalMapResizeSuite : BenchmarkSuite
{
    internal override string Name => "OrdinalMapResize";

    internal override IEnumerable<BenchmarkCase> Cases(double scale)
    {
        foreach (int contentSize in (int[])[8, 32, 128])
        {
            int count = Scaled(2048, scale);

            ByteDataArray[] content = new ByteDataArray[count];

            // Java seeds a SplittableRandom with 0 to get the same bytes every run; the same idea.
            Random random = new(0);

            for (int i = 0; i < count; i++)
            {
                ByteDataArray buffer = new();

                for (int j = 0; j < contentSize; j++)
                {
                    buffer.Write((byte)random.Next(256));
                }

                content[i] = buffer;
            }

            string parameters = string.Create(
                CultureInfo.InvariantCulture, $"contentSize={contentSize}, n={count}");

            yield return new BenchmarkCase(
                "DefaultGet",
                parameters,
                () =>
                {
                    ByteArrayOrdinalMap map = new();

                    foreach (ByteDataArray entry in content)
                    {
                        map.GetOrAssignOrdinal(entry);
                    }

                    Blackhole.Consume(map);
                });

            yield return new BenchmarkCase(
                "SizedGet",
                parameters,
                () =>
                {
                    ByteArrayOrdinalMap map = new(count << 1);

                    foreach (ByteDataArray entry in content)
                    {
                        map.GetOrAssignOrdinal(entry);
                    }

                    Blackhole.Consume(map);
                });
        }
    }
}
