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
using Hollow.Core.Index;
using Hollow.Core.Read.Engine;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Benchmarks.Suites;

/// <summary>
/// The port of <c>DuplicateDataDetectionValidatorBenchmark</c>: what it costs a producer's validator
/// to look for two records sharing a key.
/// </summary>
/// <remarks>
/// Java has a third case, <c>deltaLaggedIndex</c>, which prices
/// <c>DuplicateDataDetectionValidator.findDuplicateKeysInDelta</c> — probing only the ordinals a
/// delta added, against an index built before it. That method does not exist in this port: the
/// validator has only the full-scan path, so there is nothing here to measure against. The two
/// snapshot cases are what the delta path would be compared to, and they are here.
/// </remarks>
internal sealed class DuplicateDataDetectionSuite : BenchmarkSuite
{
    internal override string Name => "DuplicateDataDetection";

    internal override IEnumerable<BenchmarkCase> Cases(double scale)
    {
        int records = Scaled(1_000_000, scale);

        HollowWriteStateEngine writeEngine = new();
        HollowObjectMapper mapper = new(writeEngine);

        for (int i = 0; i < records; i++)
        {
            mapper.Add(new Movie
            {
                Id = i,
                Title = string.Create(CultureInfo.InvariantCulture, $"Title{i}"),
                Rating = i % 100,
            });
        }

        HollowReadStateEngine readEngine = RoundTripper.RoundTripSnapshot(writeEngine);
        HollowPrimaryKeyIndex index = new(readEngine, nameof(Movie), "Id");

        string parameters = string.Create(CultureInfo.InvariantCulture, $"records={records}");

        yield return new BenchmarkCase(
            "SnapshotFullScan", parameters, () => Blackhole.Consume(index.GetDuplicateKeys()));

        yield return new BenchmarkCase(
            "SnapshotBounded", parameters, () => Blackhole.Consume(index.GetDuplicateKeys(100)));
    }

    [HollowPrimaryKey("Id")]
    internal sealed class Movie
    {
        public required int Id { get; init; }

        public required string Title { get; init; }

        public required int Rating { get; init; }
    }
}
