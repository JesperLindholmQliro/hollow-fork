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

using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Schema;
using Hollow.Core.Write;
using Hollow.Tools.Compact;

namespace Hollow.Tests.Tools;

/// <summary>
/// Reclaims the ordinal holes a churning dataset leaves behind.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>HollowCompactorTest</c>. Every test drives real cycles — a hundred films, then only
/// the last twenty — and asserts on what a consumer ends up holding, because the whole point of
/// compaction is the consumer's ordinal space and nothing else observes it.
/// </para>
/// <para>
/// The catalogue has no reference fields on purpose. A compaction cycle only targets types that do
/// not reference one another, so a second type would quietly take the single slot the tests are
/// asserting about.
/// </para>
/// </remarks>
public class HollowCompactorTests
{
    private const int Films = 100;
    private const int Survivors = 20;

    [Fact]
    public void ADenseStateNeedsNoCompaction()
    {
        Catalogue catalogue = new();
        catalogue.FirstCycle(Enumerable.Range(0, Films));

        Assert.False(Compactor(catalogue).NeedsCompaction());
    }

    [Fact]
    public void HolesAreReclaimed()
    {
        Catalogue catalogue = Churned();

        // Twenty records spread over a hundred ordinals: eighty slots a consumer pays for and cannot use.
        Assert.Equal(Films, Movies(catalogue).PopulatedOrdinals.Length);
        Assert.Equal(Survivors, Movies(catalogue).PopulatedOrdinals.Cardinality());

        HollowCompactor compactor = Compactor(catalogue);

        Assert.True(compactor.NeedsCompaction());

        catalogue.Compact(compactor);

        Assert.Equal(Survivors, Movies(catalogue).PopulatedOrdinals.Length);
        Assert.Equal(Survivors, Movies(catalogue).PopulatedOrdinals.Cardinality());
    }

    [Fact]
    public void CompactionChangesNoData()
    {
        Catalogue catalogue = Churned();

        List<(int Id, int Year, long Runtime)> before = Contents(catalogue);

        catalogue.Compact(Compactor(catalogue));

        // The delta consists of nothing but the same records at better ordinals.
        Assert.Equal(before, Contents(catalogue));
        Assert.Equal(Survivors, before.Count);
    }

    [Fact]
    public void ATypeBelowTheThresholdIsLeftAlone()
    {
        Catalogue catalogue = Churned();

        // Eighty percent of the ordinal space is holes, which is a lot, and still under the bar.
        HollowCompactor compactor = catalogue.Compactor(new CompactionConfig
        {
            MinCandidateHoleCostInBytes = 0,
            MinCandidateHolePercentage = 99,
        });

        Assert.False(compactor.NeedsCompaction());
    }

    [Fact]
    public void ABudgetSpreadsTheWorkOverSeveralCycles()
    {
        Catalogue catalogue = Churned();

        long recordBytes = Movies(catalogue).ApproxHeapFootprintInBytes / Survivors;

        Assert.True(recordBytes > 1, "the budget below is only meaningful if a record costs more than a byte");

        int cycles = 0;

        while (true)
        {
            HollowCompactor compactor = Compactor(catalogue, budget: recordBytes * 3);

            if (!compactor.NeedsCompaction())
            {
                break;
            }

            catalogue.Compact(compactor);

            Assert.True(++cycles < 20, "compaction did not converge");
        }

        Assert.True(cycles > 1, "a budget of three records cannot have moved twenty in one cycle");
        Assert.Equal(Survivors, Movies(catalogue).PopulatedOrdinals.Length);
    }

    [Fact]
    public void ABudgetTooSmallForOneRecordIsReported()
    {
        Catalogue catalogue = Churned();

        HollowCompactor compactor = Compactor(catalogue, budget: 1);

        // No number of cycles gets anywhere, so the answer is a reason rather than a plan.
        Assert.False(compactor.NeedsCompaction());
        Assert.Contains("Movie", compactor.SkippedTypes.Keys);
        Assert.Contains("Raise the budget", compactor.SkippedTypes["Movie"], StringComparison.Ordinal);
    }

    [Fact]
    public void ABudgetOfZeroIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new CompactionConfig
        {
            MinCandidateHoleCostInBytes = 0,
            MinCandidateHolePercentage = 0,
            ApproximateDeltaBytesPerCycle = 0,
        });

    /// <summary>A hundred films, of which only the last twenty survive the second cycle.</summary>
    private static Catalogue Churned()
    {
        Catalogue catalogue = new();

        catalogue.FirstCycle(Enumerable.Range(0, Films));
        catalogue.NextCycle(Enumerable.Range(Films - Survivors, Survivors));

        return catalogue;
    }

    private static HollowCompactor Compactor(Catalogue catalogue, long budget = long.MaxValue) =>
        catalogue.Compactor(new CompactionConfig
        {
            MinCandidateHoleCostInBytes = 0,
            MinCandidateHolePercentage = 0,
            ApproximateDeltaBytesPerCycle = budget,
        });

    private static HollowObjectTypeReadState Movies(Catalogue catalogue) =>
        (HollowObjectTypeReadState)catalogue.ReadEngine.GetTypeState("Movie")!;

    private static List<(int Id, int Year, long Runtime)> Contents(Catalogue catalogue)
    {
        HollowObjectTypeReadState movies = Movies(catalogue);

        int id = movies.Schema.GetPosition("Id");
        int year = movies.Schema.GetPosition("Year");
        int runtime = movies.Schema.GetPosition("Runtime");

        return [.. movies.PopulatedOrdinals.EnumerateSetBits()
            .Select(ordinal => (
                Id: movies.ReadInt(ordinal, id),
                Year: movies.ReadInt(ordinal, year),
                Runtime: movies.ReadLong(ordinal, runtime)))
            .OrderBy(film => film.Id)];
    }

    /// <summary>A single-type catalogue, and the cycles that carry it to a consumer.</summary>
    private sealed class Catalogue
    {
        private readonly HollowWriteStateEngine _writeEngine = new();
        private readonly HollowObjectSchema _movie;

        internal Catalogue()
        {
            _movie = new HollowObjectSchema("Movie", 3, "Id");
            _movie.AddField("Id", FieldType.Int);
            _movie.AddField("Year", FieldType.Int);
            _movie.AddField("Runtime", FieldType.Long);

            _writeEngine.AddTypeState(new HollowObjectTypeWriteState(_movie));
        }

        internal HollowReadStateEngine ReadEngine { get; } = new();

        internal void FirstCycle(IEnumerable<int> ids)
        {
            Add(ids);
            _writeEngine.PrepareForWrite();

            StateEngineRoundTripper.RoundTripSnapshot(_writeEngine, ReadEngine);
        }

        internal void NextCycle(IEnumerable<int> ids)
        {
            Add(ids);
            _writeEngine.PrepareForWrite();

            StateEngineRoundTripper.RoundTripDelta(_writeEngine, ReadEngine);
        }

        internal HollowCompactor Compactor(CompactionConfig config) =>
            new(_writeEngine, ReadEngine, config);

        /// <summary>Compacts, and produces the delta that carries the relocations to the consumer.</summary>
        internal void Compact(HollowCompactor compactor)
        {
            compactor.Compact();
            _writeEngine.PrepareForWrite();

            StateEngineRoundTripper.RoundTripDelta(_writeEngine, ReadEngine);
        }

        private void Add(IEnumerable<int> ids)
        {
            HollowObjectWriteRecord record = new(_movie);

            foreach (int id in ids)
            {
                record.Reset();
                record.SetInt("Id", id);
                record.SetInt("Year", 1900 + id);
                record.SetLong("Runtime", 5_000_000_000L + id);

                _writeEngine.Add("Movie", record);
            }
        }
    }
}
