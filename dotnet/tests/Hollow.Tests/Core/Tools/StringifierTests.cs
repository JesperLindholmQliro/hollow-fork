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

using System.Text.Json;
using Hollow.Core.Read.Engine;
using Hollow.Core.Tools.Stringifier;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Tests.Core.Tools;

/// <summary>
/// Writing a record out for a person to read, and for a program to.
/// </summary>
/// <remarks>
/// Both forms take liberties with the blob on purpose — a one-field record stands in for its value,
/// and JSON leaves a null field out — so what these check is that the liberties are the intended ones
/// and that what comes out is well formed.
/// </remarks>
public class StringifierTests
{
    private sealed record Actor(string Name, int? Age);

    private sealed record Studio(string Name);

    [HollowPrimaryKey("Id")]
    private sealed record Movie(
        int Id,
        string Title,
        int Year,
        bool IsColour,
        string? Tagline,
        Studio Studio,
        List<Actor> Cast,
        HashSet<string> Tags,
        Dictionary<string, Actor> Roles);

    private static readonly Movie TheMatrix = new(
        1,
        "The Matrix",
        1999,
        true,
        null,
        new Studio("Warner Bros."),
        [new Actor("Keanu Reeves", 34), new Actor("Laurence Fishburne", null)],
        ["sci-fi", "action"],
        new Dictionary<string, Actor> { ["Neo"] = new Actor("Keanu Reeves", 34) });

    private static (HollowReadStateEngine Engine, int Ordinal) Publish(Movie movie)
    {
        HollowWriteStateEngine writeEngine = new();
        HollowObjectMapper mapper = new(writeEngine);
        mapper.InitializeTypeState(typeof(Movie));
        int ordinal = mapper.Add(movie);

        return (StateEngineRoundTripper.RoundTripSnapshot(writeEngine), ordinal);
    }

    /// <summary>
    /// The text form is a line per field, nested by indentation, with every field shown — including the
    /// ones holding nothing, which is the difference from the JSON form.
    /// </summary>
    [Fact]
    public void TheTextFormShowsEveryField()
    {
        (HollowReadStateEngine engine, int ordinal) = Publish(TheMatrix);

        string text = new HollowRecordStringifier().Stringify(engine, "Movie", ordinal);

        Assert.Contains("Id: 1", text, StringComparison.Ordinal);
        Assert.Contains("Title: The Matrix", text, StringComparison.Ordinal);
        Assert.Contains("IsColour: true", text, StringComparison.Ordinal);
        Assert.Contains("Tagline: null", text, StringComparison.Ordinal);
        Assert.Contains("Studio: Warner Bros.", text, StringComparison.Ordinal);

        // A list numbers its elements, a set does not, and a map writes each entry as a pair.
        Assert.Contains("e0: ", text, StringComparison.Ordinal);
        Assert.Contains("e: ", text, StringComparison.Ordinal);
        Assert.Contains("k: Neo", text, StringComparison.Ordinal);
        Assert.Contains("v: ", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nearly every string in a Hollow dataset is a record holding one field, so leaving them expanded
    /// buries the data in a layer that says nothing.
    /// </summary>
    [Fact]
    public void ASingleFieldRecordCanStandInForItsValue()
    {
        (HollowReadStateEngine engine, int ordinal) = Publish(TheMatrix);

        Assert.Contains(
            "Title: The Matrix",
            new HollowRecordStringifier(collapseSingleFieldObjects: true).Stringify(engine, "Movie", ordinal),
            StringComparison.Ordinal);

        // Left expanded, the title is a String record with a value field.
        Assert.Contains(
            "value: The Matrix",
            new HollowRecordStringifier(collapseSingleFieldObjects: false).Stringify(engine, "Movie", ordinal),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheTextFormCanShowTypesAndOrdinals()
    {
        (HollowReadStateEngine engine, int ordinal) = Publish(TheMatrix);

        string text = new HollowRecordStringifier(showOrdinals: true, showTypes: true)
            .Stringify(engine, "Movie", ordinal);

        Assert.Contains("(Movie)", text, StringComparison.Ordinal);
        Assert.Contains("(ordinal 0)", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The JSON form has to actually be JSON, which a hand-rolled writer is the easiest thing to get
    /// subtly wrong — so it is parsed rather than pattern-matched.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheJsonFormParses(bool prettyPrint)
    {
        (HollowReadStateEngine engine, int ordinal) = Publish(TheMatrix);

        string json = new HollowRecordJsonStringifier(prettyPrint).Stringify(engine, "Movie", ordinal);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement movie = document.RootElement;

        Assert.Equal(1, movie.GetProperty("Id").GetInt32());
        Assert.Equal("The Matrix", movie.GetProperty("Title").GetString());
        Assert.True(movie.GetProperty("IsColour").GetBoolean());
        Assert.Equal("Warner Bros.", movie.GetProperty("Studio").GetString());

        // A null field is absent rather than null, which is what a reader of JSON expects.
        Assert.False(movie.TryGetProperty("Tagline", out _));

        Assert.Equal(
            ["Keanu Reeves", "Laurence Fishburne"],
            movie.GetProperty("Cast").EnumerateArray().Select(actor => actor.GetProperty("Name").GetString()));

        Assert.Equal(
            ["action", "sci-fi"],
            movie.GetProperty("Tags").EnumerateArray().Select(tag => tag.GetString()).Order(StringComparer.Ordinal));

        // A map keyed by a value becomes a JSON object keyed by it.
        Assert.Equal(
            "Keanu Reeves",
            movie.GetProperty("Roles").GetProperty("Neo").GetProperty("Name").GetString());
    }

    /// <summary>
    /// A JSON key can only be a string, so a map keyed by a record has to become a list of pairs
    /// instead of an object.
    /// </summary>
    [Fact]
    public void AMapKeyedByARecordBecomesAListOfPairs()
    {
        HollowWriteStateEngine writeEngine = new();
        HollowObjectMapper mapper = new(writeEngine);
        int ordinal = mapper.Add(new Billing(new Dictionary<Actor, Studio>
        {
            [new Actor("Keanu Reeves", 34)] = new Studio("Warner Bros."),
        }));

        HollowReadStateEngine engine = StateEngineRoundTripper.RoundTripSnapshot(writeEngine);

        string json = new HollowRecordJsonStringifier().Stringify(engine, "Billing", ordinal);

        using JsonDocument document = JsonDocument.Parse(json);

        // Billing holds one field, so it collapses to the map itself — which is the array, because its
        // keys are records rather than values.
        JsonElement pair = document.RootElement.EnumerateArray().Single();

        Assert.Equal("Keanu Reeves", pair.GetProperty("key").GetProperty("Name").GetString());
        Assert.Equal("Warner Bros.", pair.GetProperty("value").GetString());
    }

    private sealed record Billing(Dictionary<Actor, Studio> By);

    /// <summary>
    /// A string is escaped rather than written through, or the JSON it lands in stops being JSON.
    /// </summary>
    [Fact]
    public void AStringWithJsonInItIsEscaped()
    {
        HollowWriteStateEngine writeEngine = new();
        HollowObjectMapper mapper = new(writeEngine);
        int ordinal = mapper.Add(new Studio("a \"quoted\"\nname\twith\\control"));

        HollowReadStateEngine engine = StateEngineRoundTripper.RoundTripSnapshot(writeEngine);

        string json = new HollowRecordJsonStringifier(prettyPrint: false)
            .Stringify(engine, "Studio", ordinal);

        using JsonDocument document = JsonDocument.Parse(json);

        // Studio holds one field and so does String, so both collapse and the record is just the text.
        Assert.Equal("a \"quoted\"\nname\twith\\control", document.RootElement.GetString());
    }

    /// <summary>
    /// A type the caller excluded is written as null rather than expanded, for a type whose records are
    /// large and beside the point.
    /// </summary>
    [Fact]
    public void AnExcludedTypeIsWrittenAsNull()
    {
        (HollowReadStateEngine engine, int ordinal) = Publish(TheMatrix);

        HollowRecordJsonStringifier stringifier = new();
        stringifier.AddExcludeObjectTypes("ListOfActor");

        using JsonDocument document =
            JsonDocument.Parse(stringifier.Stringify(engine, "Movie", ordinal));

        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("Cast").ValueKind);
    }

    /// <summary>
    /// A type the dataset does not have is said so rather than throwing, since a stringifier is usually
    /// looking at data it was not written for.
    /// </summary>
    [Fact]
    public void AMissingTypeIsReportedRatherThanThrowing()
    {
        (HollowReadStateEngine engine, _) = Publish(TheMatrix);

        Assert.Equal("[missing type Nope]", new HollowRecordStringifier().Stringify(engine, "Nope", 0));
        Assert.Equal("{ }", new HollowRecordJsonStringifier().Stringify(engine, "Nope", 0));
    }
}
