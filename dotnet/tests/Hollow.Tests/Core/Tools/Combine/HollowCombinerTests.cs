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

using Hollow.Api.Objects.Generic;
using Hollow.Core;
using Hollow.Core.Index;
using Hollow.Core.Index.Key;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.Set;
using Hollow.Core.Read.Iterator;
using Hollow.Core.Schema;
using Hollow.Core.Tools.Combine;
using Hollow.Core.Util;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Tests.Core.Tools.Combine;

/// <summary>
/// Copying several states into one. Carried from Java's <c>HollowCombinerTest</c> and
/// <c>HollowCombinerPrimaryKeyTests</c>; the field paths are PascalCase here because that is what the
/// .NET object mapper writes.
/// </summary>
public class HollowCombinerTests
{
    /// <summary>Three inputs with overlapping keys, as Java's primary-key tests set up.</summary>
    private readonly HollowReadStateEngine _input1 = Input(1, [(1, 1, 1), (2, 2, 2), (3, 3, 3)]);
    private readonly HollowReadStateEngine _input2 = Input(2, [(4, 2, 3), (5, 4, 4), (6, 6, 6)]);
    private readonly HollowReadStateEngine _input3 = Input(3, [(7, 2, 3), (8, 7, 6), (9, 8, 8), (10, 4, 10)]);

    [Fact]
    public void EveryInputsRecordsArriveAndDuplicatesAreWrittenOnce()
    {
        HollowReadStateEngine shard1 = Sets([(1, ["C1", "C2", "C3"]), (2, ["C2", "C3", "C4"]), (3, ["C1", "C2", "C3"])]);
        HollowReadStateEngine shard2 = Sets([(4, ["C2", "C3", "C4"]), (5, ["C1", "C4", "C5"]), (6, ["C1", "C2", "C3"])]);
        HollowReadStateEngine shard3 = Sets([(7, ["C4", "C5", "C6"])]);

        HollowCombiner combiner = new(shard1, shard2, shard3);
        combiner.Combine();

        HollowReadStateEngine output = StateEngineRoundTripper.RoundTripSnapshot(combiner.Output);

        // Seven A records, because each has its own key. The B sets and the C strings are written once
        // each however many inputs held them, which is the write state's own deduplication doing the
        // work once the ordinals have been rewritten.
        Assert.Equal(6, output.GetTypeState("A")!.MaxOrdinal);
        Assert.Equal(3, output.GetTypeState("B")!.MaxOrdinal);
        Assert.Equal(5, output.GetTypeState("C")!.MaxOrdinal);

        HollowSetTypeReadState sets = (HollowSetTypeReadState)output.GetTypeState("B")!;

        Assert.True(SetExists(output, sets, ["C1", "C2", "C3"]));
        Assert.True(SetExists(output, sets, ["C2", "C3", "C4"]));
        Assert.True(SetExists(output, sets, ["C1", "C4", "C5"]));
        Assert.True(SetExists(output, sets, ["C4", "C5", "C6"]));
    }

    [Fact]
    public void TheOutputKeepsTheInputsDeclaredPrimaryKeys()
    {
        HollowCombiner combiner = new(_input1, _input2);

        HollowReadStateEngine output = StateEngineRoundTripper.RoundTripSnapshot(combiner.Output);

        Assert.Equal(
            DeclaredKeys(_input1.Schemas).Select(key => key.ToString()),
            DeclaredKeys(output.Schemas).Select(key => key.ToString()));
    }

    [Fact]
    public void AKeyForATypeThatAlreadyHasOneReplacesIt()
    {
        HollowCombiner combiner = new(_input1, _input2);

        PrimaryKey replacement = new("TypeA", "Origin");
        combiner.SetPrimaryKeys(replacement);

        Assert.Equal(replacement, Assert.Single(combiner.PrimaryKeys, key => key.Type == "TypeA"));
    }

    [Fact]
    public void AKeyForATypeThatHasNoneIsAddedAlongsideTheRest()
    {
        HollowCombiner combiner = new(_input1, _input2);

        int before = combiner.PrimaryKeys.Count;
        PrimaryKey added = new("TypeC", "Key");

        combiner.SetPrimaryKeys(added);

        Assert.Equal(before + 1, combiner.PrimaryKeys.Count);
        Assert.Contains(added, combiner.PrimaryKeys);
    }

    [Fact]
    public void SettingKeysOnASingleInputDoesNothingBecauseThereIsNothingToDeduplicate()
    {
        HollowCombiner combiner = new(_input1);

        combiner.SetPrimaryKeys(new PrimaryKey("TypeB", "Key"));

        Assert.Empty(combiner.PrimaryKeys);
    }

    [Fact]
    public void CascadingKeysFoldEachTypeOntoTheFirstInputThatHadIt()
    {
        HollowReadStateEngine output = Combine(
            null, new PrimaryKey("TypeB", "Key"), new PrimaryKey("TypeC", "Key"));

        // Read as: A record 4, from input 2, references the B keyed 2 — which input 1 already had, so
        // the reference points at input 1's copy, and at input 1's C in turn.
        AssertObject(output, 1, 1, 1, 1, 1, 1);
        AssertObject(output, 2, 1, 2, 1, 2, 1);
        AssertObject(output, 3, 1, 3, 1, 3, 1);
        AssertObject(output, 4, 2, 2, 1, 2, 1);
        AssertObject(output, 5, 2, 4, 2, 4, 2);
        AssertObject(output, 6, 2, 6, 2, 6, 2);
        AssertObject(output, 7, 3, 2, 1, 2, 1);
        AssertObject(output, 8, 3, 7, 3, 6, 2);
        AssertObject(output, 9, 3, 8, 3, 8, 3);
        AssertObject(output, 10, 3, 4, 2, 4, 2);
    }

    [Fact]
    public void ACompoundKeyReachingThroughAReferenceDeduplicatesOnTheWholeKey()
    {
        HollowReadStateEngine output = Combine(null, new PrimaryKey("TypeB", "Key", "C.Key"));

        // B is keyed on its own key and its C's, so input 2's (2, 3) is a different record from input
        // 1's (2, 2) and is kept — where the cascading case above folded them together.
        AssertObject(output, 1, 1, 1, 1, 1, 1);
        AssertObject(output, 2, 1, 2, 1, 2, 1);
        AssertObject(output, 3, 1, 3, 1, 3, 1);
        AssertObject(output, 4, 2, 2, 2, 3, 2);
        AssertObject(output, 5, 2, 4, 2, 4, 2);
        AssertObject(output, 6, 2, 6, 2, 6, 2);
        AssertObject(output, 7, 3, 2, 2, 3, 2);
        AssertObject(output, 8, 3, 7, 3, 6, 3);
        AssertObject(output, 9, 3, 8, 3, 8, 3);
        AssertObject(output, 10, 3, 4, 3, 10, 3);
    }

    [Fact]
    public void ACompoundKeyAndACascadingOneAreAppliedInDependencyOrder()
    {
        HollowReadStateEngine output = Combine(
            null, new PrimaryKey("TypeB", "Key", "C.Key"), new PrimaryKey("TypeC", "Key"));

        // C is deduplicated in an earlier round than B, so by the time B's compound key is evaluated
        // its "C.Key" already names the surviving C.
        AssertObject(output, 1, 1, 1, 1, 1, 1);
        AssertObject(output, 2, 1, 2, 1, 2, 1);
        AssertObject(output, 3, 1, 3, 1, 3, 1);
        AssertObject(output, 4, 2, 2, 2, 3, 1);
        AssertObject(output, 5, 2, 4, 2, 4, 2);
        AssertObject(output, 6, 2, 6, 2, 6, 2);
        AssertObject(output, 7, 3, 2, 2, 3, 1);
        AssertObject(output, 8, 3, 7, 3, 6, 2);
        AssertObject(output, 9, 3, 8, 3, 8, 3);
        AssertObject(output, 10, 3, 4, 3, 10, 3);
    }

    [Fact]
    public void AnExcludedRecordIsReplacedByTheNextInputsRecordWithTheSameKey()
    {
        HollowCombinerExcludePrimaryKeysCopyDirector director = new();
        director.ExcludeKey(new HollowPrimaryKeyIndex(_input1, "TypeC", "Key"), 3);

        HollowReadStateEngine output = Combine(
            director, new PrimaryKey("TypeB", "Key", "C.Key"), new PrimaryKey("TypeC", "Key"));

        // Input 1's C keyed 3 is excluded, so the reference resolves to input 2's copy of it instead
        // of vanishing — which is what makes an exclusion a replacement rather than a deletion.
        AssertObject(output, 3, 1, 3, 1, 3, 2);
    }

    [Fact]
    public void AnExcludedRecordStillArrivesWhenAnotherInputHoldsIt()
    {
        HollowCombinerExcludePrimaryKeysCopyDirector director = new();
        director.ExcludeKey(new HollowPrimaryKeyIndex(_input3, "TypeC", "Key"), 8);

        HollowReadStateEngine output = Combine(
            director, new PrimaryKey("TypeB", "Key", "C.Key"), new PrimaryKey("TypeC", "Key"));

        // Excluding a record only stops it being copied directly. Input 3's A keyed 9 still references
        // it, so it comes across anyway.
        AssertObject(output, 9, 3, 8, 3, 8, 3);
    }

    [Fact]
    public void ExcludingReferencedObjectsTakesTheWholeClosureOut()
    {
        HollowCombinerExcludePrimaryKeysCopyDirector director = new();
        director.ExcludeKey(new HollowPrimaryKeyIndex(_input3, "TypeA", "Key"), 9);

        HollowReadStateEngine output = Combine(
            director, new PrimaryKey("TypeB", "Key", "C.Key"), new PrimaryKey("TypeC", "Key"));

        // The A is gone, but the B and C it alone referenced are still copied directly.
        Assert.Equal(HollowConstants.OrdinalNone, MatchingOrdinal(output, "TypeA", 9));
        Assert.NotEqual(HollowConstants.OrdinalNone, MatchingOrdinal(output, "TypeB", 8));
        Assert.NotEqual(HollowConstants.OrdinalNone, MatchingOrdinal(output, "TypeC", 8));

        director.ExcludeReferencedObjects();

        output = Combine(director, new PrimaryKey("TypeB", "Key", "C.Key"), new PrimaryKey("TypeC", "Key"));

        Assert.Equal(HollowConstants.OrdinalNone, MatchingOrdinal(output, "TypeA", 9));
        Assert.Equal(HollowConstants.OrdinalNone, MatchingOrdinal(output, "TypeB", 8));
        Assert.Equal(HollowConstants.OrdinalNone, MatchingOrdinal(output, "TypeC", 8));
    }

    [Fact]
    public void AnIgnoredTypeIsNotCopied()
    {
        HollowCombiner combiner = new(_input1, _input2);

        // TypeA is what references everything else, so leaving it out leaves the rest unreachable and
        // therefore uncopied too.
        combiner.AddIgnoredTypes("TypeA");
        combiner.Combine();

        HollowReadStateEngine output = StateEngineRoundTripper.RoundTripSnapshot(combiner.Output);

        Assert.Equal(0, output.GetTypeState("TypeA")!.PopulatedOrdinals.Cardinality());
    }

    [Fact]
    public void ACombineNeedsAtLeastOneInput() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new HollowCombiner());

    private HollowReadStateEngine Combine(
        IHollowCombinerCopyDirector? director, params PrimaryKey[] keys)
    {
        HollowCombiner combiner = director is null
            ? new HollowCombiner(_input1, _input2, _input3)
            : new HollowCombiner(director, _input1, _input2, _input3);

        combiner.SetPrimaryKeys(keys);
        combiner.Combine();

        return StateEngineRoundTripper.RoundTripSnapshot(combiner.Output);
    }

    /// <summary>
    /// Checks one A record and the B and C it reaches, each by its key and the input it came from.
    /// </summary>
    private static void AssertObject(
        HollowReadStateEngine output,
        int aKey,
        int expectAOrigin,
        int expectBKey,
        int expectBOrigin,
        int expectCKey,
        int expectCOrigin)
    {
        GenericHollowObject typeA = new(output, "TypeA", MatchingOrdinal(output, "TypeA", aKey));

        Assert.Equal(aKey, typeA.GetInt("Key"));
        Assert.Equal(expectAOrigin, typeA.GetInt("Origin"));

        GenericHollowObject typeB = typeA.GetObject("B")!;

        Assert.Equal(expectBKey, typeB.GetInt("Key"));
        Assert.Equal(expectBOrigin, typeB.GetInt("Origin"));

        GenericHollowObject typeC = typeB.GetObject("C")!;

        Assert.Equal(expectCKey, typeC.GetInt("Key"));
        Assert.Equal(expectCOrigin, typeC.GetInt("Origin"));
    }

    private static int MatchingOrdinal(HollowReadStateEngine output, string type, int key) =>
        new HollowPrimaryKeyIndex(output, type, "Key").GetMatchingOrdinal(key);

    private static IEnumerable<PrimaryKey> DeclaredKeys(IEnumerable<HollowSchema> schemas) =>
        schemas.OfType<HollowObjectSchema>().Select(schema => schema.PrimaryKey).OfType<PrimaryKey>();

    private static bool SetExists(
        HollowReadStateEngine output, HollowSetTypeReadState sets, string[] expected)
    {
        for (int ordinal = 0; ordinal <= sets.MaxOrdinal; ordinal++)
        {
            HashSet<string> values =
            [
                .. sets.ElementOrdinals(ordinal).AsEnumerable()
                    .Select(element => new GenericHollowObject(output, "C", element).GetString("c1")!),
            ];

            if (values.SetEquals(expected))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A state of A records, each holding a set of C strings — Java's hand-built schemas, which exist
    /// to exercise a set type rather than only object types.
    /// </summary>
    private static HollowReadStateEngine Sets((int AValue, string[] CValues)[] records)
    {
        HollowObjectSchema aSchema = new("A", 2, new PrimaryKey("A", "a1"));
        aSchema.AddField("a1", FieldType.Int);
        aSchema.AddField("a2", FieldType.Reference, "B");

        HollowSetSchema bSchema = new("B", "C");

        HollowObjectSchema cSchema = new("C", 1);
        cSchema.AddField("c1", FieldType.String);

        HollowWriteStateEngine writeEngine = HollowWriteStateCreator.CreateWithSchemas([aSchema, bSchema, cSchema]);

        foreach ((int aValue, string[] cValues) in records)
        {
            HollowSetWriteRecord bRec = new();

            foreach (string cValue in cValues)
            {
                HollowObjectWriteRecord cRec = new(cSchema);
                cRec.SetString("c1", cValue);

                bRec.AddElement(writeEngine.Add("C", cRec));
            }

            HollowObjectWriteRecord aRec = new(aSchema);
            aRec.SetInt("a1", aValue);
            aRec.SetReference("a2", writeEngine.Add("B", bRec));

            writeEngine.Add("A", aRec);
        }

        return StateEngineRoundTripper.RoundTripSnapshot(writeEngine);
    }

    private static HollowReadStateEngine Input(int origin, (int AKey, int BKey, int CKey)[] records)
    {
        HollowWriteStateEngine writeEngine = new();
        HollowObjectMapper mapper = new(writeEngine);

        foreach ((int aKey, int bKey, int cKey) in records)
        {
            mapper.Add(new TypeA(aKey, origin, new TypeB(bKey, origin, new TypeC(cKey, origin))));
        }

        return StateEngineRoundTripper.RoundTripSnapshot(writeEngine);
    }

    private sealed record TypeA(int Key, int Origin, TypeB B);

    private sealed record TypeB(int Key, int Origin, TypeC C);

    private sealed record TypeC(int Key, int Origin);
}
