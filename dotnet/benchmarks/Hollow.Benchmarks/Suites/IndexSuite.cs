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
/// The dataset Java's <c>AbstractHollowIndexBenchmark</c> builds: records of eight integer fields and
/// a nested record of eight more, so that an index can be built over one field or several without the
/// cost of hashing a string getting into the measurement.
/// </summary>
internal sealed class IndexDataset
{
    private readonly int[] _keys;
    private readonly Random _random = new(42);

    internal IndexDataset(int size, int querySize, bool nested, int cardinality)
    {
        Size = size;
        QuerySize = querySize;
        Cardinality = cardinality;

        HollowWriteStateEngine writeEngine = new();
        HollowObjectMapper mapper = new(writeEngine);

        for (int i = 0; i < size; i++)
        {
            // Spaced eight apart so that one record's fields are unique to it: overlapping values
            // make the index build look far worse than it is.
            mapper.Add(new IntType(Key(8 * i)));
        }

        ReadEngine = RoundTripper.RoundTripSnapshot(writeEngine);

        MatchFields = new string[querySize];

        for (int i = 0; i < querySize; i++)
        {
            // Java writes field1..fieldN; the .NET mapper writes what the members are called, and
            // .NET members are PascalCase.
            MatchFields[i] = nested
                ? string.Create(CultureInfo.InvariantCulture, $"Nested.Field{i + 1}")
                : string.Create(CultureInfo.InvariantCulture, $"Field{i + 1}");
        }

        _keys = new int[size];

        for (int i = 0; i < size; i++)
        {
            _keys[i] = Key(i);
        }
    }

    internal HollowReadStateEngine ReadEngine { get; }

    internal string[] MatchFields { get; }

    internal int Size { get; }

    internal int QuerySize { get; }

    internal int Cardinality { get; }

    /// <summary>A key that is in the dataset.</summary>
    internal object?[] NextKeys()
    {
        int key = _keys[_random.Next(_keys.Length)];
        object?[] keys = new object?[QuerySize];

        for (int i = 0; i < QuerySize; i++)
        {
            keys[i] = key + i;
        }

        return keys;
    }

    /// <summary>A key that is not, so that the miss path is measured too.</summary>
    internal object?[] MissingKeys()
    {
        object?[] keys = new object?[QuerySize];
        Array.Fill(keys, -1);

        return keys;
    }

    /// <summary>
    /// One of <paramref name="indexes"/>, so that a run does not sit in one index's caches.
    /// </summary>
    internal T NextIndex<T>(T[] indexes) => indexes[_random.Next(indexes.Length)];

    /// <summary>
    /// As many indexes as Java builds: enough to fill about thirty megabytes, so the lookup case
    /// touches cold memory rather than one warm index.
    /// </summary>
    internal T[] BuildIndexes<T>(Func<T> create, double scale)
    {
        const int targetBytes = 30 * 1024 * 1024;
        const int entryOverheadBytes = 16;

        int count = Math.Max(1, (int)(targetBytes / (entryOverheadBytes * (double)Size) * scale));
        T[] indexes = new T[count];

        for (int i = 0; i < count; i++)
        {
            indexes[i] = create();
        }

        return indexes;
    }

    private int Key(int key) => key - (key % Cardinality);

    internal sealed class IntType
    {
        internal IntType(int value)
        {
            Field1 = value;
            Field2 = value + 1;
            Field3 = value + 2;
            Field4 = value + 3;
            Field5 = value + 4;
            Field6 = value + 5;
            Field7 = value + 6;
            Field8 = value + 7;
            Nested = new NestedIntType(value);
        }

        public int Field1 { get; }

        public int Field2 { get; }

        public int Field3 { get; }

        public int Field4 { get; }

        public int Field5 { get; }

        public int Field6 { get; }

        public int Field7 { get; }

        public int Field8 { get; }

        public NestedIntType Nested { get; }
    }

    internal sealed class NestedIntType(int value)
    {
        public int Field1 { get; } = value;

        public int Field2 { get; } = value + 1;

        public int Field3 { get; } = value + 2;

        public int Field4 { get; } = value + 3;

        public int Field5 { get; } = value + 4;

        public int Field6 { get; } = value + 5;

        public int Field7 { get; } = value + 6;

        public int Field8 { get; } = value + 7;
    }
}

/// <summary>
/// The port of <c>HollowPrimaryKeyIndexBenchmark</c>: building the index that answers "which record
/// has this key", and asking it.
/// </summary>
internal sealed class PrimaryKeyIndexSuite : BenchmarkSuite
{
    internal override string Name => "HollowPrimaryKeyIndex";

    internal override IEnumerable<BenchmarkCase> Cases(double scale)
    {
        int size = Scaled(1000, scale);
        IndexDataset dataset = new(size, querySize: 1, nested: false, cardinality: 1);

        string parameters = string.Create(CultureInfo.InvariantCulture, $"size={size}, querySize=1");

        HollowPrimaryKeyIndex Create() =>
            new(dataset.ReadEngine, nameof(IndexDataset.IntType), dataset.MatchFields);

        yield return new BenchmarkCase(
            "BuildIndex", parameters, () => Blackhole.Consume(Create()));

        HollowPrimaryKeyIndex[] indexes = [];

        yield return new BenchmarkCase(
            "GetMatchingOrdinal",
            parameters,
            () => Blackhole.Consume(dataset.NextIndex(indexes).GetMatchingOrdinal(dataset.NextKeys())),
            Setup: () => indexes = dataset.BuildIndexes(Create, scale),
            TearDown: () => indexes = []);

        yield return new BenchmarkCase(
            "GetMatchingOrdinalMissing",
            parameters,
            () => Blackhole.Consume(dataset.NextIndex(indexes).GetMatchingOrdinal(dataset.MissingKeys())),
            Setup: () => indexes = dataset.BuildIndexes(Create, scale),
            TearDown: () => indexes = []);
    }
}

/// <summary>
/// The port of <c>HollowHashIndexBenchmark</c>: the same two questions of the index that answers
/// "which records have this key", where several may.
/// </summary>
internal sealed class HashIndexSuite : BenchmarkSuite
{
    internal override string Name => "HollowHashIndex";

    internal override IEnumerable<BenchmarkCase> Cases(double scale)
    {
        int size = Scaled(1000, scale);

        // Java's cardinality of 1000 against a size of 1000 puts every record in one group, which is
        // what makes this the hash index's benchmark rather than the primary key index's again.
        IndexDataset dataset = new(size, querySize: 1, nested: false, cardinality: Math.Max(1, size));

        string parameters = string.Create(
            CultureInfo.InvariantCulture, $"size={size}, querySize=1, cardinality={size}");

        HollowHashIndex Create() =>
            new(dataset.ReadEngine, nameof(IndexDataset.IntType), "", dataset.MatchFields);

        yield return new BenchmarkCase(
            "BuildIndex", parameters, () => Blackhole.Consume(Create()));

        HollowHashIndex[] indexes = [];

        yield return new BenchmarkCase(
            "FindMatches",
            parameters,
            () => Blackhole.Consume(dataset.NextIndex(indexes).FindMatches(dataset.NextKeys())),
            Setup: () => indexes = dataset.BuildIndexes(Create, scale),
            TearDown: () => indexes = []);

        yield return new BenchmarkCase(
            "FindMatchesMissing",
            parameters,
            () => Blackhole.Consume(dataset.NextIndex(indexes).FindMatches(dataset.MissingKeys())),
            Setup: () => indexes = dataset.BuildIndexes(Create, scale),
            TearDown: () => indexes = []);
    }
}
