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
using System.Reflection;
using Hollow.Api.Custom;
using Hollow.Core.Read.Engine;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;
using Microsoft.CodeAnalysis;

namespace Hollow.Tests.Api.Codegen;

/// <summary>
/// The Roslyn source generator, driven over a model declared in source.
/// </summary>
/// <remarks>
/// <para>
/// The generator runs inside the compiler and cannot load the types it is generating for, so it
/// derives the data model from Roslyn's symbols rather than from reflection. That derivation is the
/// only part of code generation the two front ends do not share, and so the only part that can drift —
/// the test that matters most here is the one asserting it describes the same schemas the object
/// mapper does, over a model that exercises every rule.
/// </para>
/// <para>
/// Everything downstream of the model goes through the same emitters as <c>CodeGeneratorTests</c>, so
/// these do not repeat what the emitted client reads. They check that the generator finds the model,
/// honours its options, and fails loudly rather than silently emitting nothing.
/// </para>
/// </remarks>
public class SourceGeneratorTests
{
    /// <summary>
    /// The model, declared as source for the generator and as CLR types for the object mapper. The two
    /// have to describe the same schemas; keeping them literally side by side is what makes a
    /// divergence obvious.
    /// </summary>
    private const string ModelSource = """
        using System.Collections.Generic;
        using Hollow.Api.Codegen;
        using Hollow.Core.Write.ObjectMapper;

        namespace Acme.Model;

        public sealed record Actor(string Name, int? Age);

        public enum Certificate { U, Pg, Fifteen }

        public sealed record Studio(string Name, [property: HollowInline] string Country);

        [HollowGeneratedApi(Namespace = "Acme.Generated", ApiClassName = "MoviesApi")]
        [HollowPrimaryKey("Id")]
        public sealed record Movie(
            int Id,
            string Title,
            int Year,
            decimal? Budget,
            bool IsColour,
            byte[]? Poster,
            Certificate Rating,
            Studio Studio,
            List<Actor> Cast,
            HashSet<string> Tags,
            Dictionary<string, Actor> Roles);
        """;

    // The same model as CLR types, for the object mapper to derive schemas from.
    private sealed record Actor(string Name, int? Age);

    private enum Certificate
    {
        U,
        Pg,
        Fifteen,
    }

    private sealed record Studio(string Name, [property: HollowInline] string Country);

    [HollowPrimaryKey("Id")]
    private sealed record Movie(
        int Id,
        string Title,
        int Year,
        decimal? Budget,
        bool IsColour,
        byte[]? Poster,
        Certificate Rating,
        Studio Studio,
        List<Actor> Cast,
        HashSet<string> Tags,
        Dictionary<string, Actor> Roles);

    /// <summary>
    /// The generator's derivation, from Roslyn symbols, has to describe the same schemas the object
    /// mapper derives from the loaded types — otherwise a generated client would read a blob that the
    /// producer writes differently. This is the seam that cannot be shared, so it is the one that has
    /// to be checked.
    /// </summary>
    [Fact]
    public void DescribesTheSameModelTheObjectMapperDoes()
    {
        IReadOnlyList<string> fromSymbols = SourceGeneratorDriver.DescribeSchemas(ModelSource);

        HollowWriteStateEngine writeEngine = new();
        new HollowObjectMapper(writeEngine).InitializeTypeState(typeof(Movie));

        IReadOnlyList<string> fromReflection =
            [.. writeEngine.Schemas.Select(schema => schema.ToString()!).Order(StringComparer.Ordinal)];

        Assert.Equal(fromReflection, fromSymbols);
    }

    /// <summary>
    /// The generator emits a client into the compilation, with no diagnostics, and it compiles.
    /// </summary>
    [Fact]
    public void EmitsAClientThatCompiles()
    {
        (Assembly assembly, IReadOnlyList<Diagnostic> diagnostics, IReadOnlyList<string> generatedFiles) =
            SourceGeneratorDriver.Run(ModelSource);

        Assert.Empty(diagnostics);

        Assert.Contains("MoviesApi.Movie.cs", generatedFiles);
        Assert.Contains("MoviesApi.ListOfActor.cs", generatedFiles);
        Assert.Contains("MoviesApi.MovieUniqueKeyIndex.cs", generatedFiles);
        Assert.Contains("MoviesApi.MoviesApi.cs", generatedFiles);

        // The built-in scalars are in Hollow.Core.Types; the generator names them rather than
        // emitting another set.
        Assert.DoesNotContain("MoviesApi.HString.cs", generatedFiles);

        Assert.NotNull(assembly.GetType("Acme.Generated.MoviesApi"));
        Assert.NotNull(assembly.GetType("Acme.Generated.Movie"));
        Assert.NotNull(assembly.GetType("Acme.Generated.MovieUniqueKeyIndex"));
    }

    /// <summary>
    /// The generated client reads what a producer mapping the same model writes. That the two halves
    /// of this test share no code at all is the point of it.
    /// </summary>
    [Fact]
    public void TheGeneratedClientReadsWhatTheProducerWrites()
    {
        (Assembly assembly, _, _) = SourceGeneratorDriver.Run(ModelSource);

        HollowWriteStateEngine writeEngine = new();
        HollowReadStateEngine readEngine = new();
        HollowObjectMapper mapper = new(writeEngine);
        mapper.InitializeTypeState(typeof(Movie));
        mapper.Add(new Movie(
            1, "The Matrix", 1999, 63_000_000m, true, [1, 2, 3], Certificate.Fifteen,
            new Studio("Warner Bros.", "US"),
            [new Actor("Keanu Reeves", 34), new Actor("Laurence Fishburne", null)],
            ["sci-fi"],
            new Dictionary<string, Actor> { ["Neo"] = new Actor("Keanu Reeves", 34) }));
        StateEngineRoundTripper.RoundTripSnapshot(writeEngine, readEngine);

        Type apiType = assembly.GetType("Acme.Generated.MoviesApi")!;
        HollowApi api = (HollowApi)Activator.CreateInstance(apiType, readEngine)!;

        object movie = ((System.Collections.IEnumerable)Read(api, "AllMovie")!).Cast<object>().Single();

        Assert.Equal(1, Read(movie, "Id"));
        Assert.Equal("The Matrix", Read(movie, "Title"));
        Assert.Equal(1999, Read(movie, "Year"));
        Assert.Equal(63_000_000m, Read(movie, "Budget"));
        Assert.Equal(true, Read(movie, "IsColour"));
        Assert.Equal(new byte[] { 1, 2, 3 }, Read(movie, "Poster"));

        // An enum is stored as its member name, so the data survives a renumbering. Its type has one
        // value field, so the shortcut applies and the name reads straight off the movie.
        Assert.Equal("Fifteen", Read(movie, "Rating"));
        Assert.Equal("Fifteen", Read(Read(movie, "RatingRecord")!, "Name"));

        object studio = Read(movie, "Studio")!;
        Assert.Equal("Warner Bros.", Read(studio, "Name"));

        // An inlined string is a value field on the record rather than a reference, so it reads
        // straight off the wrapper.
        Assert.Equal("US", Read(studio, "Country"));

        Assert.Equal(
            ["Keanu Reeves", "Laurence Fishburne"],
            ((System.Collections.IEnumerable)Read(movie, "Cast")!).Cast<object>()
                .Select(actor => (string?)Read(actor, "Name"))
                .Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The attribute's options reach the emitters, so the same model can generate a different client.
    /// </summary>
    [Fact]
    public void TheAttributeCarriesTheGeneratorsOptions()
    {
        const string Source = """
            using Hollow.Api.Codegen;
            using Hollow.Core.Write.ObjectMapper;

            namespace Acme.Model;

            [HollowGeneratedApi(
                Namespace = "Acme.Bare",
                ApiClassName = "BareApi",
                UseErgonomicShortcuts = false,
                GenerateUniqueKeyIndexes = false,
                GenerateCachedDelegates = false,
                GenerateFieldPaths = false)]
            [HollowPrimaryKey("Id")]
            public sealed record Thing(int Id, string Label);
            """;

        (Assembly assembly, IReadOnlyList<Diagnostic> diagnostics, IReadOnlyList<string> files) =
            SourceGeneratorDriver.Run(Source);

        Assert.Empty(diagnostics);

        // No index, even though the type declares a key.
        Assert.DoesNotContain("BareApi.ThingUniqueKeyIndex.cs", files);

        Type thing = assembly.GetType("Acme.Bare.Thing")!;

        // Without the shortcut the property is the wrapper, and there is no Record suffix.
        Assert.Equal("HString", thing.GetProperty("Label")!.PropertyType.Name);
        Assert.Null(thing.GetProperty("LabelRecord"));

        // And no cached delegate was emitted for it.
        Assert.Null(assembly.GetType("Acme.Bare.ThingCachedDelegate"));
        Assert.NotNull(assembly.GetType("Acme.Bare.ThingLookupDelegate"));

        // Nor any of the typed routes.
        Assert.DoesNotContain("BareApi.BarePaths.cs", files);
        Assert.Null(assembly.GetType("Acme.Bare.BarePaths"));
    }

    /// <summary>
    /// The namespace defaults to the marked type's own with <c>.Generated</c> appended — not to the
    /// model's namespace, where the wrapper generated for a type would collide with the type it was
    /// generated from. The API is named for where the model lives rather than for where the client is
    /// emitted, so it is <c>CatalogueApi</c> and not <c>GeneratedApi</c>.
    /// </summary>
    [Fact]
    public void TheNamespaceAndApiNameDefaultFromTheMarkedType()
    {
        const string Source = """
            using Hollow.Api.Codegen;

            namespace Acme.Catalogue;

            [HollowGeneratedApi]
            public sealed record Thing(int Id);
            """;

        (Assembly assembly, IReadOnlyList<Diagnostic> diagnostics, _) = SourceGeneratorDriver.Run(Source);

        Assert.Empty(diagnostics);
        Assert.NotNull(assembly.GetType("Acme.Catalogue.Generated.CatalogueApi"));
    }

    /// <summary>
    /// Two roots that agree on where the client goes are one client, so a model with several entry
    /// points does not produce two APIs each knowing half of it.
    /// </summary>
    [Fact]
    public void TwoRootsSharingAnApiBecomeOneClient()
    {
        const string Source = """
            using Hollow.Api.Codegen;

            namespace Acme.Model;

            [HollowGeneratedApi(Namespace = "Acme.Both", ApiClassName = "BothApi")]
            public sealed record First(int Id);

            [HollowGeneratedApi(Namespace = "Acme.Both", ApiClassName = "BothApi")]
            public sealed record Second(string Label);
            """;

        (Assembly assembly, IReadOnlyList<Diagnostic> diagnostics, _) = SourceGeneratorDriver.Run(Source);

        Assert.Empty(diagnostics);

        Type api = assembly.GetType("Acme.Both.BothApi")!;

        Assert.NotNull(api.GetMethod("GetFirst"));
        Assert.NotNull(api.GetMethod("GetSecond"));
        Assert.Null(assembly.GetType("Acme.Both.FirstApi"));
    }

    /// <summary>
    /// A model the mapper cannot describe is a build error pointing at the declaration, not a silently
    /// missing client.
    /// </summary>
    [Fact]
    public void AModelThatCannotBeMappedIsAnError()
    {
        const string Source = """
            using Hollow.Api.Codegen;

            namespace Acme.Model;

            [HollowGeneratedApi(Namespace = "Acme.Broken", ApiClassName = "BrokenApi")]
            public sealed class NoFields
            {
                private int hidden;
            }
            """;

        Diagnostic error = Assert.Single(SourceGeneratorDriver.RunExpectingFailure(Source));

        Assert.Equal("HOLLOW001", error.Id);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains(
            "at least one field", error.GetMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    /// <summary>
    /// A compilation with no marked type gets no generated files, rather than an empty API.
    /// </summary>
    [Fact]
    public void AModelWithNoRootGeneratesNothing()
    {
        const string Source = """
            namespace Acme.Model;

            public sealed record Unmarked(int Id);
            """;

        (_, IReadOnlyList<Diagnostic> diagnostics, IReadOnlyList<string> files) =
            SourceGeneratorDriver.Run(Source, expectGeneratedFiles: false);

        Assert.Empty(diagnostics);
        Assert.Empty(files);
    }

    private static object? Read(object instance, string member) =>
        instance.GetType().GetProperty(member)?.GetValue(instance)
        ?? instance.GetType().GetMethod(member, [])?.Invoke(instance, []);
}
