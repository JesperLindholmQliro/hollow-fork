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
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Tests.Core.Read.Engine;

/// <summary>
/// What a type's removed records still cost.
/// </summary>
/// <remarks>
/// An ordinal is a position in a flat run, so a record that goes away does not give its space back —
/// it leaves a hole that stays allocated until something reuses it. This is what says how much of a
/// type is holes, which is the figure that decides whether it is worth compacting.
/// </remarks>
public class HoleCostTests
{
    private sealed record Film(int Id, string Title);

    private sealed record Cast(int FilmId, List<string> Actors);

    /// <summary>
    /// A dataset nothing has been removed from has no holes, whatever it holds.
    /// </summary>
    [Fact]
    public void ADatasetNothingWasRemovedFromHasNoHoles()
    {
        HollowWriteStateEngine writeEngine = new();
        HollowObjectMapper mapper = new(writeEngine);

        for (int i = 0; i < 20; i++)
        {
            mapper.Add(new Film(i, $"film-{i}"));
        }

        HollowReadStateEngine readEngine = StateEngineRoundTripper.RoundTripSnapshot(writeEngine);

        foreach (HollowTypeReadState typeState in readEngine.TypeStates.Values)
        {
            Assert.Equal(0, typeState.ApproxHoleCostInBytes);
        }
    }

    /// <summary>
    /// Removing records leaves holes costing what those records' fixed-length storage did, which is a
    /// share of the type's footprint rather than a figure of its own.
    /// </summary>
    [Fact]
    public void RemovingRecordsLeavesHolesCostingWhatTheyTookUp()
    {
        HollowWriteStateEngine writeEngine = new();
        HollowObjectMapper mapper = new(writeEngine);

        for (int i = 0; i < 20; i++)
        {
            mapper.Add(new Film(i, $"film-{i}"));
        }

        HollowReadStateEngine readEngine = new();
        StateEngineRoundTripper.RoundTripSnapshot(writeEngine, readEngine);

        // Half of them go, from the front, so the ordinals they held stay below the highest populated
        // one and are therefore holes rather than storage that was never reached.
        for (int i = 10; i < 20; i++)
        {
            mapper.Add(new Film(i, $"film-{i}"));
        }

        StateEngineRoundTripper.RoundTripDelta(writeEngine, readEngine);

        HollowTypeReadState film = readEngine.GetTypeState("Film")!;

        Assert.Equal(10, film.PopulatedOrdinals.Cardinality());
        Assert.Equal(10, film.PopulatedOrdinals.Length - film.PopulatedOrdinals.Cardinality());

        // Every record of this type is the same width, so half of them being holes costs half the
        // type's fixed-length storage — which is all of its footprint bar the titles' bytes.
        Assert.True(film.ApproxHoleCostInBytes > 0);
        Assert.True(film.ApproxHoleCostInBytes < film.ApproxHeapFootprintInBytes);
    }

    /// <summary>
    /// A collection's holes cost too, since what its fixed-length portion holds is where its elements
    /// end — one pointer per ordinal, whether or not the ordinal holds anything.
    /// </summary>
    [Fact]
    public void ACollectionsHolesCostAPointerEach()
    {
        HollowWriteStateEngine writeEngine = new();
        HollowObjectMapper mapper = new(writeEngine);

        for (int i = 0; i < 20; i++)
        {
            mapper.Add(new Cast(i, [$"actor-{i}", $"actor-{i}-again"]));
        }

        HollowReadStateEngine readEngine = new();
        StateEngineRoundTripper.RoundTripSnapshot(writeEngine, readEngine);

        for (int i = 10; i < 20; i++)
        {
            mapper.Add(new Cast(i, [$"actor-{i}", $"actor-{i}-again"]));
        }

        StateEngineRoundTripper.RoundTripDelta(writeEngine, readEngine);

        HollowTypeReadState listOfString = readEngine.GetTypeState("ListOfString")!;

        Assert.Equal(10, listOfString.PopulatedOrdinals.Length - listOfString.PopulatedOrdinals.Cardinality());
        Assert.True(listOfString.ApproxHoleCostInBytes > 0);
    }

    /// <summary>
    /// A type that has never read a blob has no shards to charge a hole against, and says so rather
    /// than dividing by a shard count of zero.
    /// </summary>
    [Fact]
    public void ATypeWithNoShardsCostsNothing()
    {
        HollowWriteStateEngine writeEngine = new();
        HollowObjectMapper mapper = new(writeEngine);
        mapper.InitializeTypeState(typeof(Film));

        HollowReadStateEngine readEngine = StateEngineRoundTripper.RoundTripSnapshot(writeEngine);

        Assert.Equal(0, readEngine.GetTypeState("Film")!.ApproxHoleCostInBytes);
    }
}
