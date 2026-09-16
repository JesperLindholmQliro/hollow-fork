/*
 *  Copyright 2021 Netflix, Inc.
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

using System.Reflection;
using Hollow.Api.Codegen;
using Hollow.Api.Objects.Generic;
using Hollow.Api.TestData;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Schema;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Tests.Api.Codegen;

/// <summary>
/// Generating fluent builders, so that a test can describe a dataset in code.
/// </summary>
/// <remarks>
/// Ported from <c>HollowTestDataAPIGenerator</c>. The emitted text is not pinned; these compile it,
/// build a dataset through it and read the result back, which is the only check that says the
/// generator and <c>Hollow.Api.TestData</c> agree about schemas, ordinals and nesting.
/// </remarks>
public class TestDataGeneratorTests
{
    private const string GeneratedNamespace = "Acme.TestData";

    [Fact]
    public void EveryTypeGetsABuilderAndTheDatasetGetsAnAccessor()
    {
        IReadOnlyDictionary<string, string> files = Generate();

        Assert.Contains("MovieTestData.cs", files.Keys);
        Assert.Contains("StringTestData.cs", files.Keys);
        Assert.Contains("ListOfStringTestData.cs", files.Keys);
        Assert.Contains("MapOfStringToStringTestData.cs", files.Keys);
        Assert.Contains("MoviesTestDataset.cs", files.Keys);

        Assert.Contains(
            "public MovieTestData<object?> Movie()",
            files["MoviesTestDataset.cs"],
            StringComparison.Ordinal);
    }

    [Fact]
    public void ADatasetDescribedInCodeReadsBackThroughAConsumer()
    {
        (object dataset, _) = Compiled();

        Invoke(Invoke(Invoke(dataset, "Movie"), "Id", 1), "Title", "Heat");
        Invoke(Invoke(Invoke(dataset, "Movie"), "Id", 2), "Title", "Ronin");

        HollowReadStateEngine engine = ((HollowTestDataset)dataset).BuildSnapshot();

        Assert.Equal([1, 2], Ids(engine).Order());
        Assert.Equal(["Heat", "Ronin"], Titles(engine).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AShortcutFillsInTheWrapperTypeForYou()
    {
        IReadOnlyDictionary<string, string> files = Generate();

        // String holds one value field, so a title is set with the string rather than by walking into
        // a String record and out again.
        Assert.Contains(
            "public MovieTestData<TParent> Title(string? value)",
            files["MovieTestData.cs"],
            StringComparison.Ordinal);

        // The long way round is still there, for a type that has more than one field.
        Assert.Contains(
            "public StringTestData<MovieTestData<TParent>> Title()",
            files["MovieTestData.cs"],
            StringComparison.Ordinal);
    }

    [Fact]
    public void UpWalksBackOutToTheRecordThatHoldsIt()
    {
        (object dataset, _) = Compiled();

        object movie = Invoke(dataset, "Movie");
        object cast = Invoke(movie, "Cast");

        // The builder is typed by its parent, so walking out lands on the movie rather than on object.
        Assert.Same(movie, Invoke(Invoke(cast, "String", "Pacino"), "Up"));

        Invoke(movie, "Id", 1);

        HollowReadStateEngine engine = ((HollowTestDataset)dataset).BuildSnapshot();

        Assert.Equal(1, Ids(engine).Single());
        Assert.Equal(1, engine.GetTypeState("ListOfString")!.PopulatedOrdinals.Cardinality());
    }

    [Fact]
    public void AMapEntryIsAddedWholeRatherThanHalfBuilt()
    {
        (object dataset, _) = Compiled();

        object movie = Invoke(dataset, "Movie");
        object ratings = Invoke(movie, "Ratings");

        // Both shortcuts apply, so an entry of two wrapper types is one call.
        Invoke(ratings, "Entry", "imdb", "8.2");
        Invoke(movie, "Id", 1);

        HollowReadStateEngine engine = ((HollowTestDataset)dataset).BuildSnapshot();

        Assert.Equal(1, engine.GetTypeState("MapOfStringToString")!.PopulatedOrdinals.Cardinality());
        Assert.Contains("imdb", Values(engine));
        Assert.Contains("8.2", Values(engine));
    }

    [Fact]
    public void TheGeneratedSchemasAreTheOnesTheModelDeclares()
    {
        (_, Assembly assembly) = Compiled();

        HollowWriteStateEngine writeEngine = new();
        new HollowObjectMapper(writeEngine).InitializeTypeState(typeof(Movie));

        foreach (HollowSchema derived in writeEngine.Schemas)
        {
            object generated = assembly.GetType($"{GeneratedNamespace}.{derived.Name}TestData`1")!
                .MakeGenericType(typeof(object))
                .GetField("RecordSchema")!
                .GetValue(null)!;

            // Byte-for-byte what the object mapper derives, which is what makes a dataset described
            // in code read exactly like one a producer wrote.
            Assert.Equal(derived.ToString(), generated.ToString());
        }
    }

    [Fact]
    public void OptionsAreRequired() =>
        Assert.Throws<ArgumentNullException>(() => new HollowTestDataGenerator(null!));

    private static IReadOnlyDictionary<string, string> Generate() =>
        new HollowTestDataGenerator(
            new HollowTestDataGeneratorOptions
            {
                Namespace = GeneratedNamespace,
                DatasetClassName = "MoviesTestDataset",
            }).Generate(typeof(Movie));

    private static (object Dataset, Assembly Assembly) Compiled()
    {
        Assembly assembly = GeneratedApiCompiler.Compile(Generate());

        return (
            Activator.CreateInstance(assembly.GetType($"{GeneratedNamespace}.MoviesTestDataset")!)!,
            assembly);
    }

    private static IEnumerable<int> Ids(HollowReadStateEngine engine)
    {
        HollowObjectTypeReadState movies = (HollowObjectTypeReadState)engine.GetTypeState("Movie")!;
        int id = movies.Schema.GetPosition("Id");

        return [.. movies.PopulatedOrdinals.EnumerateSetBits().Select(ordinal => movies.ReadInt(ordinal, id))];
    }

    private static IEnumerable<string?> Titles(HollowReadStateEngine engine)
    {
        HollowObjectTypeReadState movies = (HollowObjectTypeReadState)engine.GetTypeState("Movie")!;
        int title = movies.Schema.GetPosition("Title");

        return
        [
            .. movies.PopulatedOrdinals.EnumerateSetBits()
                .Select(ordinal => new GenericHollowObject(engine, "Movie", ordinal)
                    .GetObject("Title")?.GetString("value")),
        ];
    }

    private static IEnumerable<string?> Values(HollowReadStateEngine engine)
    {
        HollowObjectTypeReadState strings = (HollowObjectTypeReadState)engine.GetTypeState("String")!;

        return [.. strings.PopulatedOrdinals.EnumerateSetBits().Select(ordinal => strings.ReadString(ordinal, 0))];
    }

    private static object Invoke(object instance, string method, params object?[] arguments)
    {
        MethodInfo builder = instance.GetType().GetMethod(
            method,
            [.. arguments.Select(argument => argument?.GetType() ?? typeof(string))])
            ?? instance.GetType().GetMethods().Single(
                candidate => candidate.Name == method && candidate.GetParameters().Length == arguments.Length);

        object?[] converted = [.. builder.GetParameters().Select(
            (parameter, index) => Convert(arguments[index], parameter.ParameterType))];

        return builder.Invoke(instance, converted)!;
    }

    private static object? Convert(object? argument, Type parameterType) =>
        argument is null || parameterType.IsInstanceOfType(argument)
            ? argument
            : System.Convert.ChangeType(
                argument,
                Nullable.GetUnderlyingType(parameterType) ?? parameterType,
                System.Globalization.CultureInfo.InvariantCulture);

    [HollowPrimaryKey("Id")]
    private sealed record Movie(
        int Id,
        string Title,
        List<string> Cast,
        Dictionary<string, string> Ratings);
}
