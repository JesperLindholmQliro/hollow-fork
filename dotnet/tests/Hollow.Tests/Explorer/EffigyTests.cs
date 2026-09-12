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

using Hollow.Core;
using Hollow.Core.Read.Engine;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;
using Hollow.Explorer.Diff.Effigy;

namespace Hollow.Tests.Explorer;

/// <summary>
/// A record turned into an object tree, which is what the diff pages lay out.
/// </summary>
/// <remarks>
/// The thing worth pinning is that an effigy compares by what it holds rather than by where it lives:
/// two records at different ordinals in different states have to be recognised as the same value, or
/// nothing can be paired across a diff.
/// </remarks>
public class EffigyTests
{
    private sealed record Studio(string Name);

    private sealed record Film(int Id, string Title, Studio Studio, List<string> Tags);

    private static HollowReadStateEngine Publish(params Film[] films)
    {
        HollowWriteStateEngine writeEngine = new();
        HollowObjectMapper mapper = new(writeEngine);
        mapper.InitializeTypeState(typeof(Film));

        foreach (Film film in films)
        {
            mapper.Add(film);
        }

        return StateEngineRoundTripper.RoundTripSnapshot(writeEngine);
    }

    private static readonly Film Matrix =
        new(1, "The Matrix", new Studio("Warner Bros."), ["sci-fi", "action"]);

    private static HollowEffigy Effigise(HollowReadStateEngine engine, int ordinal = 0) =>
        new HollowEffigyFactory().Effigy(engine, "Film", ordinal)!;

    private static HollowEffigyField Field(HollowEffigy effigy, string name) =>
        effigy.Fields.Single(field => field.FieldName == name);

    /// <summary>
    /// The tree mirrors the record: a field per field, in schema order, with a reference becoming a
    /// nested effigy rather than an ordinal.
    /// </summary>
    [Fact]
    public void TheTreeMirrorsTheRecord()
    {
        HollowEffigy film = Effigise(Publish(Matrix));

        Assert.Equal("Film", film.ObjectType);
        Assert.Equal(["Id", "Title", "Studio", "Tags"], film.Fields.Select(field => field.FieldName));

        // An int is stored in the record, so it is a leaf holding the number itself.
        Assert.True(Field(film, "Id").IsLeafNode);
        Assert.Equal(1, Field(film, "Id").Value);

        // A string is a reference to the shared String type, so it is a node with a `value` inside.
        HollowEffigyField title = Field(film, "Title");

        Assert.False(title.IsLeafNode);
        Assert.Equal("The Matrix", Field((HollowEffigy)title.Value!, "value").Value);
    }

    /// <summary>
    /// A collection's elements become fields of their own, all called <c>element</c>, in the order the
    /// collection holds them.
    /// </summary>
    [Fact]
    public void ACollectionBecomesOneFieldPerElement()
    {
        HollowEffigy film = Effigise(Publish(Matrix));
        HollowEffigy tags = (HollowEffigy)Field(film, "Tags").Value!;

        Assert.Equal(2, tags.Fields.Count);
        Assert.All(tags.Fields, field => Assert.Equal("element", field.FieldName));

        Assert.Equal(
            ["action", "sci-fi"],
            tags.Fields
                .Select(field => (string?)Field((HollowEffigy)field.Value!, "value").Value)
                .Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Two records holding the same values are the same effigy, whatever ordinals they sit at and
    /// whichever state they came from — which is what lets a diff pair them.
    /// </summary>
    [Fact]
    public void TwoRecordsHoldingTheSameValuesAreEqual()
    {
        // A second film first, so The Matrix lands at a different ordinal in the second state.
        HollowReadStateEngine from = Publish(Matrix);
        HollowReadStateEngine to = Publish(
            new Film(2, "Persona", new Studio("Svensk"), ["drama"]), Matrix);

        HollowEffigy fromFilm = Effigise(from, 0);
        HollowEffigy toFilm = Effigise(to, 1);

        Assert.NotEqual(fromFilm.Ordinal, toFilm.Ordinal);
        Assert.Equal(fromFilm, toFilm);
        Assert.Equal(fromFilm.GetHashCode(), toFilm.GetHashCode());
    }

    /// <summary>A record differing anywhere is a different effigy.</summary>
    [Fact]
    public void ARecordDifferingAnywhereIsNotEqual()
    {
        HollowEffigy original = Effigise(Publish(Matrix));

        Assert.NotEqual(original, Effigise(Publish(Matrix with { Title = "The Matrix Reloaded" })));
        Assert.NotEqual(original, Effigise(Publish(Matrix with { Studio = new Studio("Village") })));
        Assert.NotEqual(original, Effigise(Publish(Matrix with { Tags = ["sci-fi"] })));
    }

    /// <summary>
    /// A record the dataset does not have is nothing rather than an empty tree, so a page can tell the
    /// two apart.
    /// </summary>
    [Fact]
    public void AMissingRecordIsNothing()
    {
        HollowReadStateEngine engine = Publish(Matrix);
        HollowEffigyFactory factory = new();

        Assert.Null(factory.Effigy(engine, "Film", HollowConstants.OrdinalNone));
        Assert.Null(factory.Effigy(engine, "Nope", 0));
    }

    /// <summary>
    /// Fields are read when they are first asked for, so building an effigy of a record that reaches
    /// most of the dataset does not read most of the dataset.
    /// </summary>
    [Fact]
    public void FieldsAreReadOnlyWhenAskedFor()
    {
        HollowEffigy film = Effigise(Publish(Matrix));

        // Asking twice gives the same list back rather than reading the record again.
        Assert.Same(film.Fields, film.Fields);
    }

    /// <summary>A node standing for something the blob does not hold takes the fields it is given.</summary>
    [Fact]
    public void AStandaloneNodeTakesTheFieldsItIsGiven()
    {
        HollowEffigy entry = new("Map.Entry");

        entry.Add(new HollowEffigyField("key", "String", "Neo"));
        entry.Add(new HollowEffigyField("value", "String", "Keanu Reeves"));

        Assert.Equal("Map.Entry", entry.ObjectType);
        Assert.Equal(["key", "value"], entry.Fields.Select(field => field.FieldName));
        Assert.Equal(HollowConstants.OrdinalNone, entry.Ordinal);
    }
}
