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

using Hollow.Core.Read;
using Hollow.Core.Read.Engine;
using Hollow.Core.Tools.Checksum;
using Hollow.Core.Tools.Patch.Delta;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Tests.Core.Tools.Patch;

/// <summary>
/// Splicing a delta chain across two states that were never adjacent. Carried from Java's
/// <c>HollowStateDeltaPatcherTest</c>, with the intermediate state and the header tags examined on
/// their own as well.
/// </summary>
public class HollowStateDeltaPatcherTests
{
    [Fact]
    public void TwoTransitionsCarryAConsumerFromOneStateToTheOther()
    {
        HollowReadStateEngine state1 = ConstructState1();
        HollowReadStateEngine state2 = ConstructState2();

        (byte[] delta1, byte[] reverseDelta1, byte[] delta2, byte[] reverseDelta2) = Patch(state1, state2);

        HollowBlobReader reader = new(state1);

        reader.ApplyDelta(HollowBlobInput.Serial(delta1));
        reader.ApplyDelta(HollowBlobInput.Serial(delta2));

        // state1 has been walked forward onto state2's data, header tags and all.
        Assert.Equal("true", state1.HeaderTags["final_state"]);
        Assert.False(state1.HeaderTags.ContainsKey("origin_state"));

        Assert.Equal(
            HollowChecksum.ForStateEngineWithCommonSchemas(state1, state2),
            HollowChecksum.ForStateEngineWithCommonSchemas(state2, state1));

        // And the two reverse deltas walk it back again.
        reader.ApplyDelta(HollowBlobInput.Serial(reverseDelta2));
        reader.ApplyDelta(HollowBlobInput.Serial(reverseDelta1));

        Assert.Equal("true", state1.HeaderTags["origin_state"]);
        Assert.False(state1.HeaderTags.ContainsKey("final_state"));
    }

    [Fact]
    public void TheIntermediateStateHoldsTheEarlierStatesData()
    {
        HollowReadStateEngine state1 = ConstructState1();
        HollowReadStateEngine state2 = ConstructState2();

        (byte[] delta1, _, _, _) = Patch(state1, state2);

        HollowReadStateEngine intermediate = new();
        new HollowBlobReader(intermediate).ReadSnapshot(new MemoryStream(Snapshot(state1, state2)));

        // Only the first transition, so the consumer is on the intermediate state. Every record the
        // earlier state had is still readable there — the changed ones have simply moved ordinal.
        new HollowBlobReader(state1).ApplyDelta(HollowBlobInput.Serial(delta1));

        Assert.Equal(
            HollowChecksum.ForStateEngineWithCommonSchemas(state1, intermediate),
            HollowChecksum.ForStateEngineWithCommonSchemas(intermediate, state1));
    }

    [Fact]
    public void ARecordThatDiffersAtASharedOrdinalIsMovedOutOfTheWay()
    {
        HollowReadStateEngine state1 = ConstructState1();
        HollowReadStateEngine state2 = ConstructState2();

        HollowReadStateEngine intermediate = new();
        new HollowBlobReader(intermediate).ReadSnapshot(new MemoryStream(Snapshot(state1, state2)));

        // The moved records go past the end of both states, because that is the only place an ordinal
        // is certainly free on both sides.
        int pastBothStates =
            Math.Max(state1.GetTypeState("TypeB")!.MaxOrdinal, state2.GetTypeState("TypeB")!.MaxOrdinal);

        Assert.True(intermediate.GetTypeState("TypeB")!.MaxOrdinal > pastBothStates);
    }

    [Fact]
    public void ATypeOnlyOneStateHasIsLeftOutOfThePatch()
    {
        HollowWriteStateEngine writeEngine = new();
        HollowObjectMapper mapper = new(writeEngine);

        mapper.Add(new TypeA1(1, new TypeB1(2, 1)));
        mapper.Add(new OnlyHere("x"));

        HollowReadStateEngine hasExtraType = StateEngineRoundTripper.RoundTripSnapshot(writeEngine);
        HollowReadStateEngine state2 = ConstructState2();

        // Java reads the later state's schema before checking whether it has the type at all, and
        // throws a null reference. The type is simply not part of the patch.
        HollowStateDeltaPatcher patcher = new(hasExtraType, state2);

        Assert.Null(patcher.StateEngine.GetTypeState("OnlyHere"));
        Assert.NotNull(patcher.StateEngine.GetTypeState("TypeA"));
    }

    /// <summary>Runs both transitions and hands back the four blobs.</summary>
    private static (byte[] Delta1, byte[] ReverseDelta1, byte[] Delta2, byte[] ReverseDelta2) Patch(
        HollowReadStateEngine from, HollowReadStateEngine to)
    {
        HollowStateDeltaPatcher patcher = new(from, to);

        patcher.PrepareInitialTransition();

        HollowBlobWriter writer = new(patcher.StateEngine);
        byte[] delta1 = Write(writer.WriteDelta);
        byte[] reverseDelta1 = Write(writer.WriteReverseDelta);

        patcher.PrepareFinalTransition();

        writer = new HollowBlobWriter(patcher.StateEngine);
        byte[] delta2 = Write(writer.WriteDelta);
        byte[] reverseDelta2 = Write(writer.WriteReverseDelta);

        return (delta1, reverseDelta1, delta2, reverseDelta2);
    }

    /// <summary>A snapshot of the intermediate state, for a consumer that joins part way through.</summary>
    private static byte[] Snapshot(HollowReadStateEngine from, HollowReadStateEngine to)
    {
        HollowStateDeltaPatcher patcher = new(from, to);
        patcher.PrepareInitialTransition();

        return Write(new HollowBlobWriter(patcher.StateEngine).WriteSnapshot);
    }

    private static byte[] Write(Action<Stream> write)
    {
        using MemoryStream stream = new();
        write(stream);

        return stream.ToArray();
    }

    private static HollowReadStateEngine ConstructState1()
    {
        HollowWriteStateEngine writeEngine = new();
        writeEngine.HeaderTags["origin_state"] = "true";

        HollowObjectMapper mapper = new(writeEngine);

        mapper.Add(new TypeB1(1, 0));
        mapper.Add(new TypeA1(1, new TypeB1(2, 1)));
        mapper.Add(new TypeA1(2, new TypeB1(3, 2)));
        mapper.Add(new TypeA1(999, new TypeB1(999, 3)));

        HollowReadStateEngine state1 = StateEngineRoundTripper.RoundTripSnapshot(writeEngine);

        // A second cycle, so that the state the patcher sees has ghost records in it as a real one would.
        mapper.Add(new TypeB1(1, 0));
        mapper.Add(new TypeA1(1, new TypeB1(2, 1)));
        mapper.Add(new TypeA1(2, new TypeB1(3, 2)));
        mapper.Add(new TypeA1(4, new TypeB1(5, 4)));
        mapper.Add(new TypeB1(6, 7));

        StateEngineRoundTripper.RoundTripDelta(writeEngine, state1);

        return state1;
    }

    private static HollowReadStateEngine ConstructState2()
    {
        HollowWriteStateEngine writeEngine = new();
        writeEngine.HeaderTags["final_state"] = "true";

        HollowObjectMapper mapper = new(writeEngine);

        mapper.Add(new TypeB2(1, 100));
        mapper.Add(new TypeA2(1, new TypeB2(2, 101)));
        mapper.Add(new TypeA2(2, new TypeB2(7, 102)));
        mapper.Add(new TypeA2(5, new TypeB2(8, 103)));
        mapper.Add(new TypeA2(4, new TypeB2(5, 104)));
        mapper.Add(new TypeA2(999, new TypeB2(999, 105)));
        mapper.Add(new TypeA2(6, new TypeB2(9, 106)));

        HollowReadStateEngine state2 = StateEngineRoundTripper.RoundTripSnapshot(writeEngine);

        mapper.Add(new TypeB2(1, 100));
        mapper.Add(new TypeA2(1, new TypeB2(2, 101)));
        mapper.Add(new TypeA2(2, new TypeB2(7, 102)));
        mapper.Add(new TypeA2(5, new TypeB2(8, 103)));
        mapper.Add(new TypeA2(4, new TypeB2(5, 104)));
        mapper.Add(new TypeA2(6, new TypeB2(9, 106)));

        StateEngineRoundTripper.RoundTripDelta(writeEngine, state2);

        return state2;
    }

    /// <summary>The earlier state's shape of TypeA.</summary>
    [HollowTypeName("TypeA")]
    private sealed record TypeA1(int A1, TypeB1 B);

    /// <summary>The earlier state's shape of TypeB.</summary>
    [HollowTypeName("TypeB")]
    private sealed record TypeB1(int B1, int B2);

    /// <summary>The later state's TypeA, which is the same shape.</summary>
    [HollowTypeName("TypeA")]
    private sealed record TypeA2(int A1, TypeB2 B);

    /// <summary>
    /// The later state's TypeB, which has dropped <c>B2</c> for a <c>B3</c> of another type — so only
    /// <c>B1</c> is common, and only <c>B1</c> can take part in the delta.
    /// </summary>
    [HollowTypeName("TypeB")]
    private sealed record TypeB2(int B1, float B3);

    /// <summary>A type only one of the two states has.</summary>
    private sealed record OnlyHere(string Name);
}
