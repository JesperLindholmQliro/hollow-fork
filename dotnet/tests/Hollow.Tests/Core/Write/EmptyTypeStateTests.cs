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

using Hollow.Core.Index.Key;
using Hollow.Core.Read.Engine;
using Hollow.Core.Schema;
using Hollow.Core.Util;
using Hollow.Core.Write;

namespace Hollow.Tests.Core.Write;

/// <summary>
/// A type that is declared and never populated, which every cycle of a real dataset has some of.
/// </summary>
/// <remarks>
/// Ported from <c>EmptyTypeSnapshotTest</c> and <c>MissingHashKeyInWriteStateTest</c>. Declaring the
/// schema rather than inferring it from the records is what keeps a dataset's shape the same from one
/// cycle to the next, and the price is that every write path has to cope with having nothing to write:
/// a collection's hash table is laid out from its widest record, and there is no widest record.
/// </remarks>
public sealed class EmptyTypeStateTests
{
    [Fact]
    public void ASnapshotOfFourEmptyTypesRoundTrips()
    {
        HollowWriteStateEngine writeEngine = EmptyOfEveryKind();

        HollowReadStateEngine readEngine = StateEngineRoundTripper.RoundTripSnapshot(writeEngine);

        // Present, and empty. A type that vanished from the blob would read as absent instead, which
        // is a different thing to a consumer.
        foreach (string type in (string[])["TestObject", "TestList", "TestSet", "TestMap"])
        {
            HollowTypeReadState? typeState = readEngine.GetTypeState(type);

            Assert.NotNull(typeState);
            Assert.Equal(0, typeState.PopulatedOrdinals.Cardinality());
        }
    }

    [Fact]
    public void ADeltaOffFourEmptyTypesRoundTripsToo()
    {
        HollowWriteStateEngine writeEngine = EmptyOfEveryKind();

        HollowReadStateEngine readEngine = StateEngineRoundTripper.RoundTripSnapshot(writeEngine);

        StateEngineRoundTripper.RoundTripDelta(writeEngine, readEngine);

        foreach (string type in (string[])["TestObject", "TestList", "TestSet", "TestMap"])
        {
            Assert.Equal(0, readEngine.GetTypeState(type)!.PopulatedOrdinals.Cardinality());
        }
    }

    [Fact]
    public void ASetWithADeclaredHashKeyAndNoRecordsStillWrites()
    {
        // The hash key decides how a set's records lay their buckets out, and the layout is computed
        // from the widest record there is. With no records at all there is nothing to compute it from,
        // which is the case Java's MissingHashKeyInWriteStateTest exists for: a producer that declared
        // a type its cycle never populated should publish, not fail.
        HollowObjectSchema actor = new("Actor", 2);
        actor.AddField("id", FieldType.Int);
        actor.AddField("name", FieldType.String);

        HollowSetSchema actors = new("SetOfActor", "Actor", "id");

        HollowObjectSchema movie = new("Movie", 2);
        movie.AddField("id", FieldType.Int);
        movie.AddField("actors", FieldType.Reference, "SetOfActor");

        HollowWriteStateEngine writeEngine = new();
        writeEngine.AddTypeState(new HollowObjectTypeWriteState(actor));
        writeEngine.AddTypeState(new HollowSetTypeWriteState(actors));
        writeEngine.AddTypeState(new HollowObjectTypeWriteState(movie));

        HollowObjectWriteRecord record = new(movie);
        record.SetInt("id", 1);
        record.SetNull("actors");
        writeEngine.Add("Movie", record);

        HollowReadStateEngine readEngine = StateEngineRoundTripper.RoundTripSnapshot(writeEngine);

        Assert.Equal(1, readEngine.GetTypeState("Movie")!.PopulatedOrdinals.Cardinality());
        Assert.Equal(0, readEngine.GetTypeState("SetOfActor")!.PopulatedOrdinals.Cardinality());
        Assert.Equal(0, readEngine.GetTypeState("Actor")!.PopulatedOrdinals.Cardinality());

        // And the hash key survives the round trip, so a consumer that does get records later can
        // still look them up by it.
        Assert.Equal(
            new PrimaryKey("Actor", "id"),
            ((HollowSetSchema)readEngine.GetTypeState("SetOfActor")!.Schema).HashKey);
    }

    [Fact]
    public void AMapWithADeclaredHashKeyAndNoRecordsStillWrites()
    {
        HollowObjectSchema actor = new("Actor", 2);
        actor.AddField("id", FieldType.Int);
        actor.AddField("name", FieldType.String);

        HollowObjectSchema role = new("Role", 1);
        role.AddField("billing", FieldType.Int);

        HollowMapSchema cast = new("MapOfActorToRole", "Actor", "Role", "id");

        HollowObjectSchema movie = new("Movie", 2);
        movie.AddField("id", FieldType.Int);
        movie.AddField("cast", FieldType.Reference, "MapOfActorToRole");

        HollowWriteStateEngine writeEngine = new();
        writeEngine.AddTypeState(new HollowObjectTypeWriteState(actor));
        writeEngine.AddTypeState(new HollowObjectTypeWriteState(role));
        writeEngine.AddTypeState(new HollowMapTypeWriteState(cast));
        writeEngine.AddTypeState(new HollowObjectTypeWriteState(movie));

        HollowObjectWriteRecord record = new(movie);
        record.SetInt("id", 1);
        record.SetNull("cast");
        writeEngine.Add("Movie", record);

        HollowReadStateEngine readEngine = StateEngineRoundTripper.RoundTripSnapshot(writeEngine);

        Assert.Equal(1, readEngine.GetTypeState("Movie")!.PopulatedOrdinals.Cardinality());
        Assert.Equal(0, readEngine.GetTypeState("MapOfActorToRole")!.PopulatedOrdinals.Cardinality());

        Assert.Equal(
            new PrimaryKey("Actor", "id"),
            ((HollowMapSchema)readEngine.GetTypeState("MapOfActorToRole")!.Schema).HashKey);
    }

    [Fact]
    public void ASetWithNoHashKeyAndNoRecordsStillWrites()
    {
        // The same again without a hash key, because the two lay their buckets out by different code.
        HollowObjectSchema actor = new("Actor", 1);
        actor.AddField("id", FieldType.Int);

        HollowObjectSchema movie = new("Movie", 2);
        movie.AddField("id", FieldType.Int);
        movie.AddField("actors", FieldType.Reference, "SetOfActor");

        HollowWriteStateEngine writeEngine = new();
        writeEngine.AddTypeState(new HollowObjectTypeWriteState(actor));
        writeEngine.AddTypeState(new HollowSetTypeWriteState(new HollowSetSchema("SetOfActor", "Actor")));
        writeEngine.AddTypeState(new HollowObjectTypeWriteState(movie));

        HollowObjectWriteRecord record = new(movie);
        record.SetInt("id", 1);
        record.SetNull("actors");
        writeEngine.Add("Movie", record);

        HollowReadStateEngine readEngine = StateEngineRoundTripper.RoundTripSnapshot(writeEngine);

        Assert.Equal(1, readEngine.GetTypeState("Movie")!.PopulatedOrdinals.Cardinality());
        Assert.Equal(0, readEngine.GetTypeState("SetOfActor")!.PopulatedOrdinals.Cardinality());
        Assert.Null(((HollowSetSchema)readEngine.GetTypeState("SetOfActor")!.Schema).HashKey);
    }

    [Fact]
    public void ATypeThatIsEmptyInOneCycleAndPopulatedInTheNextStillWorks()
    {
        // The cycle after the empty one is where a layout computed from nothing would show up.
        HollowObjectSchema actor = new("Actor", 1);
        actor.AddField("id", FieldType.Int);

        HollowSetSchema actors = new("SetOfActor", "Actor", "id");

        HollowWriteStateEngine writeEngine = new();
        writeEngine.AddTypeState(new HollowObjectTypeWriteState(actor));
        writeEngine.AddTypeState(new HollowSetTypeWriteState(actors));

        HollowReadStateEngine readEngine = StateEngineRoundTripper.RoundTripSnapshot(writeEngine);

        Assert.Equal(0, readEngine.GetTypeState("SetOfActor")!.PopulatedOrdinals.Cardinality());

        HollowObjectWriteRecord actorRecord = new(actor);
        actorRecord.SetInt("id", 7);
        int actorOrdinal = writeEngine.Add("Actor", actorRecord);

        HollowSetWriteRecord setRecord = new();
        setRecord.AddElement(actorOrdinal);
        writeEngine.Add("SetOfActor", setRecord);

        StateEngineRoundTripper.RoundTripDelta(writeEngine, readEngine);

        Assert.Equal(1, readEngine.GetTypeState("SetOfActor")!.PopulatedOrdinals.Cardinality());
        Assert.Equal(1, readEngine.GetTypeState("Actor")!.PopulatedOrdinals.Cardinality());
    }

    private static HollowWriteStateEngine EmptyOfEveryKind()
    {
        HollowWriteStateEngine writeEngine = new();

        writeEngine.AddTypeState(new HollowObjectTypeWriteState(new HollowObjectSchema("TestObject", 0)));
        writeEngine.AddTypeState(
            new HollowListTypeWriteState(new HollowListSchema("TestList", "TestObject")));
        writeEngine.AddTypeState(new HollowSetTypeWriteState(new HollowSetSchema("TestSet", "TestObject")));
        writeEngine.AddTypeState(
            new HollowMapTypeWriteState(new HollowMapSchema("TestMap", "TestObject", "TestObject")));

        return writeEngine;
    }
}
