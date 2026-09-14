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
using Hollow.Core.Read.Engine;
using Hollow.Core.Tools.Patch.Record;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Tests.Core.Tools.Patch;

/// <summary>
/// Replacing some records of one state with the same records from another. Java has no tests for this,
/// so these are written from what a replacement has to be: the matched records come from the patch
/// source, everything else from the base, and neither side leaves anything behind.
/// </summary>
public class HollowStateEngineRecordPatcherTests
{
    private sealed record Film(int Id, string Title, Studio Studio, List<Actor> Cast);

    private sealed record Studio(string Name);

    /// <summary>
    /// An actor, whose name is the same in both states so that a match spec can name one, and whose
    /// biography is not so that a copy can be told apart from its counterpart.
    /// </summary>
    private sealed record Actor(int Id, string Name, string Bio);

    private sealed record Setting(string Name);

    [Fact]
    public void AMatchedRecordComesFromThePatchSourceAndTheRestFromTheBase()
    {
        TypeMatchSpec spec = new("Film", "Id");
        spec.AddMatchingValue(2);

        HollowReadStateEngine patched = Patch(spec);

        Assert.Equal("Old 1", Title(patched, 1));
        Assert.Equal("New 2", Title(patched, 2));
        Assert.Equal("Old 3", Title(patched, 3));

        // Three films in, three films out — a patch replaces rather than adds.
        Assert.Equal(3, patched.GetTypeState("Film")!.PopulatedOrdinals.Cardinality());
    }

    [Fact]
    public void SeveralMatchingValuesReplaceSeveralRecords()
    {
        TypeMatchSpec spec = new("Film", "Id");
        spec.AddMatchingValue(1);
        spec.AddMatchingValue(3);

        HollowReadStateEngine patched = Patch(spec);

        Assert.Equal("New 1", Title(patched, 1));
        Assert.Equal("Old 2", Title(patched, 2));
        Assert.Equal("New 3", Title(patched, 3));
    }

    [Fact]
    public void DataOnlyTheReplacedRecordReferencedGoesWithIt()
    {
        TypeMatchSpec spec = new("Film", "Id");
        spec.AddMatchingValue(2);

        HollowReadStateEngine patched = Patch(spec);

        HashSet<string> studios = [.. Values(patched, "Studio", "Name")];

        // Film 2 alone used Focus in the base, so the base's copy is taken out with it and the patch
        // source's arrives in its place.
        Assert.Contains("Focus (new)", studios);
        Assert.DoesNotContain("Focus (old)", studios);
    }

    [Fact]
    public void DataTheReplacedRecordSharesWithAnotherIsKept()
    {
        TypeMatchSpec spec = new("Film", "Id");
        spec.AddMatchingValue(2);

        HollowReadStateEngine patched = Patch(spec);

        // Ada is in film 2's cast and film 1's, so the base's copy of her is referenced from outside
        // the replaced closure and stays — otherwise film 1 would be left pointing at nothing.
        Assert.Contains("Ada (old)", Values(patched, "Actor", "Bio"));

        foreach (int ordinal in patched.GetTypeState("Film")!.PopulatedOrdinals.EnumerateSetBits())
        {
            GenericHollowObject film = new(patched, "Film", ordinal);

            Assert.NotNull(film.GetObject("Studio"));
            Assert.NotEmpty(film.GetList("Cast")!);
        }
    }

    [Fact]
    public void AMatchThroughACollectionReplacesEveryRecordThatReachesTheValue()
    {
        // The paths are traversal paths rather than a primary key, so one can run through a list. The
        // step across the list is the literal "element", and the trailing "value" is the field of the
        // String type the mapper maps a string to.
        TypeMatchSpec spec = new("Film", "Cast.element.Name.value");
        spec.AddMatchingValue("Grace");

        HollowReadStateEngine patched = Patch(spec);

        // Grace is only in film 3's cast, in either state.
        Assert.Equal("Old 1", Title(patched, 1));
        Assert.Equal("Old 2", Title(patched, 2));
        Assert.Equal("New 3", Title(patched, 3));
    }

    [Fact]
    public void AnIgnoredTypeIsLeftOutAltogether()
    {
        TypeMatchSpec spec = new("Film", "Id");
        spec.AddMatchingValue(2);

        HollowStateEngineRecordPatcher patcher = new(BaseState(), PatchSource());
        patcher.AddTypeMatchSpec(spec);
        patcher.SetIgnoredTypes("Setting");

        HollowReadStateEngine patched = StateEngineRoundTripper.RoundTripSnapshot(patcher.Patch());

        Assert.Equal(0, patched.GetTypeState("Setting")!.PopulatedOrdinals.Cardinality());
        Assert.NotEqual(0, patched.GetTypeState("Film")!.PopulatedOrdinals.Cardinality());
    }

    [Fact]
    public void ATypeTheStatesDoNotHaveMatchesNothing()
    {
        TypeMatchSpec spec = new("Nonexistent", "Id");
        spec.AddMatchingValue(2);

        // Java reads the type state's maximum ordinal before checking whether the state has the type,
        // and throws a null reference. Nothing matches, so nothing is replaced.
        HollowReadStateEngine patched = Patch(spec);

        Assert.Equal("Old 1", Title(patched, 1));
        Assert.Equal("Old 2", Title(patched, 2));
        Assert.Equal("Old 3", Title(patched, 3));
    }

    [Fact]
    public void NoMatchSpecAtAllLeavesTheBaseAsItWas()
    {
        HollowStateEngineRecordPatcher patcher = new(BaseState(), PatchSource());

        HollowReadStateEngine patched = StateEngineRoundTripper.RoundTripSnapshot(patcher.Patch());

        Assert.Equal("Old 1", Title(patched, 1));
        Assert.Equal("Old 2", Title(patched, 2));
        Assert.Equal("Old 3", Title(patched, 3));
    }

    private static HollowReadStateEngine Patch(TypeMatchSpec spec)
    {
        HollowStateEngineRecordPatcher patcher = new(BaseState(), PatchSource());
        patcher.AddTypeMatchSpec(spec);

        return StateEngineRoundTripper.RoundTripSnapshot(patcher.Patch());
    }

    /// <summary>The title of the film with <paramref name="id"/>.</summary>
    private static string Title(HollowReadStateEngine stateEngine, int id)
    {
        foreach (int ordinal in stateEngine.GetTypeState("Film")!.PopulatedOrdinals.EnumerateSetBits())
        {
            GenericHollowObject film = new(stateEngine, "Film", ordinal);

            if (film.GetInt("Id") == id)
            {
                return film.GetObject("Title")!.GetString("value")!;
            }
        }

        return "";
    }

    /// <summary>Every value of one string field of one type.</summary>
    private static IEnumerable<string> Values(
        HollowReadStateEngine stateEngine, string typeName, string fieldName)
    {
        foreach (int ordinal in stateEngine.GetTypeState(typeName)!.PopulatedOrdinals.EnumerateSetBits())
        {
            yield return new GenericHollowObject(stateEngine, typeName, ordinal)
                .GetObject(fieldName)!
                .GetString("value")!;
        }
    }

    private static HollowReadStateEngine BaseState() => State("old");

    private static HollowReadStateEngine PatchSource() => State("new");

    /// <summary>
    /// The same three films either way, with every value carrying which state it came from so that a
    /// record can be told apart from its counterpart.
    /// </summary>
    private static HollowReadStateEngine State(string which)
    {
        HollowWriteStateEngine writeEngine = new();
        HollowObjectMapper mapper = new(writeEngine);

        Studio universal = new($"Universal ({which})");
        Studio focus = new($"Focus ({which})");

        Actor ada = new(1, "Ada", $"Ada ({which})");
        Actor grace = new(2, "Grace", $"Grace ({which})");
        Actor alan = new(3, "Alan", $"Alan ({which})");

        string title = which == "old" ? "Old" : "New";

        mapper.Add(new Film(1, $"{title} 1", universal, [ada, alan]));
        mapper.Add(new Film(2, $"{title} 2", focus, [ada]));
        mapper.Add(new Film(3, $"{title} 3", universal, [grace]));

        mapper.Add(new Setting($"region ({which})"));

        return StateEngineRoundTripper.RoundTripSnapshot(writeEngine);
    }
}
