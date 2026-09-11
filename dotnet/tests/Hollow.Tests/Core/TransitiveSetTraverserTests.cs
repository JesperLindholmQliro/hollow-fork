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
using Hollow.Core.Schema;
using Hollow.Core.Tools.Traverse;
using Hollow.Core.Util;
using Hollow.Core.Write;

namespace Hollow.Tests.Core;

/// <summary>
/// Following references between records, to work out what a selection of records implies.
/// </summary>
/// <remarks>
/// This is what decides which records an incremental producer's deletion actually removes: the record
/// itself, plus everything it referenced that nothing else still needs.
/// </remarks>
public class TransitiveSetTraverserTests
{
    /// <summary>
    /// A dataset with every kind of reference: an object field, a list, a set and a map. Movies 0–4
    /// each reference Actor <c>i</c> and Actor <c>i + 5</c>; the collections hold overlapping actors.
    /// </summary>
    private static HollowReadStateEngine Dataset()
    {
        HollowObjectSchema actorSchema = new("Actor", 1);
        actorSchema.AddField("id", FieldType.Int);

        HollowObjectSchema movieSchema = new("Movie", 3);
        movieSchema.AddField("id", FieldType.Int);
        movieSchema.AddField("lead", FieldType.Reference, "Actor");
        movieSchema.AddField("understudy", FieldType.Reference, "Actor");

        HollowListSchema castSchema = new("Cast", "Actor");
        HollowSetSchema crewSchema = new("Crew", "Actor");
        HollowMapSchema rolesSchema = new("Roles", "Actor", "Actor");

        HollowWriteStateEngine engine = new();
        engine.AddTypeState(new HollowObjectTypeWriteState(actorSchema));
        engine.AddTypeState(new HollowObjectTypeWriteState(movieSchema));
        engine.AddTypeState(new HollowListTypeWriteState(castSchema));
        engine.AddTypeState(new HollowSetTypeWriteState(crewSchema));
        engine.AddTypeState(new HollowMapTypeWriteState(rolesSchema));

        HollowObjectWriteRecord actorRecord = new(actorSchema);
        int[] actors = new int[12];
        for (int i = 0; i < actors.Length; i++)
        {
            actorRecord.Reset();
            actorRecord.SetInt("id", i);
            actors[i] = engine.Add("Actor", actorRecord);
        }

        HollowObjectWriteRecord movieRecord = new(movieSchema);
        for (int i = 0; i < 5; i++)
        {
            movieRecord.Reset();
            movieRecord.SetInt("id", i);
            movieRecord.SetReference("lead", actors[i]);
            movieRecord.SetReference("understudy", actors[i + 5]);
            engine.Add("Movie", movieRecord);
        }

        // A list holding actors 0 and 1, a set holding 1 and 2, a map from 10 to 11.
        HollowListWriteRecord cast = new();
        cast.AddElement(actors[0]);
        cast.AddElement(actors[1]);
        engine.Add("Cast", cast);

        HollowSetWriteRecord crew = new();
        crew.AddElement(actors[1]);
        crew.AddElement(actors[2]);
        engine.Add("Crew", crew);

        HollowMapWriteRecord roles = new();
        roles.AddEntry(actors[10], actors[11]);
        engine.Add("Roles", roles);

        return StateEngineRoundTripper.RoundTripSnapshot(engine);
    }

    private static Dictionary<string, BitSet> Selection(params (string Type, int[] Ordinals)[] selections)
    {
        Dictionary<string, BitSet> matches = new(StringComparer.Ordinal);

        foreach ((string type, int[] ordinals) in selections)
        {
            BitSet bitSet = new();
            foreach (int ordinal in ordinals)
            {
                bitSet.Set(ordinal);
            }

            matches[type] = bitSet;
        }

        return matches;
    }

    private static int[] Selected(Dictionary<string, BitSet> matches, string type) =>
        matches.TryGetValue(type, out BitSet? bitSet) ? [.. bitSet.EnumerateSetBits()] : [];

    [Fact]
    public void AddingTransitiveMatchesFollowsAnObjectsReferenceFields()
    {
        HollowReadStateEngine engine = Dataset();
        Dictionary<string, BitSet> matches = Selection(("Movie", [0, 2]));

        TransitiveSetTraverser.AddTransitiveMatches(engine, matches);

        // Movie 0 holds actors 0 and 5; movie 2 holds actors 2 and 7.
        Assert.Equal([0, 2], Selected(matches, "Movie"));
        Assert.Equal([0, 2, 5, 7], Selected(matches, "Actor"));
    }

    [Fact]
    public void AddingTransitiveMatchesFollowsCollectionElementsAndMapEntries()
    {
        HollowReadStateEngine engine = Dataset();
        Dictionary<string, BitSet> matches = Selection(("Cast", [0]), ("Crew", [0]), ("Roles", [0]));

        TransitiveSetTraverser.AddTransitiveMatches(engine, matches);

        Assert.Equal([0, 1, 2, 10, 11], Selected(matches, "Actor"));
    }

    [Fact]
    public void RemovingWhatIsReferencedOutsideTheClosureKeepsSharedRecords()
    {
        HollowReadStateEngine engine = Dataset();

        // Selecting movie 1 alone and pulling in its actors: actor 1 is also in the list and the set,
        // so it has to survive, while actor 6 belongs to nothing else.
        Dictionary<string, BitSet> matches = Selection(("Movie", [1]));
        TransitiveSetTraverser.AddTransitiveMatches(engine, matches);
        Assert.Equal([1, 6], Selected(matches, "Actor"));

        TransitiveSetTraverser.RemoveReferencedOutsideClosure(engine, matches);

        Assert.Equal([6], Selected(matches, "Actor"));
        Assert.Equal([1], Selected(matches, "Movie"));
    }

    [Fact]
    public void ARecordReferencedOnlyFromInsideTheClosureStays()
    {
        HollowReadStateEngine engine = Dataset();

        // The map is the only thing referencing actors 10 and 11, so taking the map takes them too.
        Dictionary<string, BitSet> matches = Selection(("Roles", [0]));
        TransitiveSetTraverser.AddTransitiveMatches(engine, matches);
        TransitiveSetTraverser.RemoveReferencedOutsideClosure(engine, matches);

        Assert.Equal([10, 11], Selected(matches, "Actor"));
    }

    [Fact]
    public void AddingWhatReferencesTheClosureTraversesUpwards()
    {
        HollowReadStateEngine engine = Dataset();

        // Actor 1 is the lead of movie 1, an element of the list and an element of the set.
        Dictionary<string, BitSet> matches = Selection(("Actor", [1]));

        TransitiveSetTraverser.AddReferencingOutsideClosure(engine, matches);

        Assert.Equal([1], Selected(matches, "Movie"));
        Assert.Equal([0], Selected(matches, "Cast"));
        Assert.Equal([0], Selected(matches, "Crew"));
        Assert.Empty(Selected(matches, "Roles"));
    }

    [Fact]
    public void AnEmptySelectionImpliesNothing()
    {
        HollowReadStateEngine engine = Dataset();
        Dictionary<string, BitSet> matches = Selection(("Movie", []));

        TransitiveSetTraverser.AddTransitiveMatches(engine, matches);
        TransitiveSetTraverser.RemoveReferencedOutsideClosure(engine, matches);

        Assert.All(matches.Values, bitSet => Assert.Equal(0, bitSet.Cardinality()));
    }
}
