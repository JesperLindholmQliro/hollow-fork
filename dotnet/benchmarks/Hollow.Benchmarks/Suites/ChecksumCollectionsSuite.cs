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
using Hollow.Core.Read.Engine;
using Hollow.Core.Schema;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Benchmarks.Suites;

/// <summary>
/// The port of <c>CheckSumCollections</c>: what it costs to checksum a list, a set and a map type,
/// which is what every validator that compares two states pays.
/// </summary>
/// <remarks>
/// Java pre-registers the collection's write state so it can fix the shard count at eight. This port
/// finds the type by the kind of schema it has instead, and leaves the shard count to the write
/// engine — registering by name would mean guessing what the mapper calls <c>List&lt;int&gt;</c>, and
/// the shard count is not what the benchmark is about.
/// </remarks>
internal sealed class ChecksumCollectionsSuite : BenchmarkSuite
{
    internal override string Name => "ChecksumCollections";

    internal override IEnumerable<BenchmarkCase> Cases(double scale)
    {
        int records = Scaled(100, scale);
        int size = Scaled(100, scale);

        foreach (SchemaType kind in (SchemaType[])[SchemaType.List, SchemaType.Set, SchemaType.Map])
        {
            HollowWriteStateEngine writeEngine = new();
            HollowObjectMapper mapper = new(writeEngine);

            for (int i = 0; i < records; i++)
            {
                mapper.Add(Model(kind, i, size));
            }

            HollowReadStateEngine readEngine = RoundTripper.RoundTripSnapshot(writeEngine);

            HollowTypeReadState typeState = readEngine.TypeStates.Values
                .First(state => state.Schema.SchemaType == kind);

            yield return new BenchmarkCase(
                "GetChecksum",
                string.Create(
                    CultureInfo.InvariantCulture, $"type={kind}, n={records}, size={size}"),
                () => Blackhole.Consume(typeState.GetChecksum(typeState.Schema)));
        }
    }

    private static object Model(SchemaType kind, int start, int size) =>
        kind switch
        {
            SchemaType.List => new ListModel { Values = [.. Enumerable.Range(start, size)] },
            SchemaType.Set => new SetModel { Values = [.. Enumerable.Range(start, size)] },
            SchemaType.Map => new MapModel
            {
                Values = Enumerable.Range(start, size).ToDictionary(value => value, value => value),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    // Java puts all three collections on one Model class and leaves two of them null each time. Three
    // classes say the same thing without three null fields in every record.
    private sealed class ListModel
    {
        public required List<int> Values { get; init; }
    }

    private sealed class SetModel
    {
        public required HashSet<int> Values { get; init; }
    }

    private sealed class MapModel
    {
        public required Dictionary<int, int> Values { get; init; }
    }
}
