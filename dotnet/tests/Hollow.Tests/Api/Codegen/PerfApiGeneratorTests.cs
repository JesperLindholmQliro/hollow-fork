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
using Hollow.Api.PerfApi;
using Hollow.Core.Read.Engine;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Tests.Api.Codegen;

/// <summary>
/// Generating a performance API rather than a client of record wrappers.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>HollowPerformanceAPIGenerator</c>. The emitted text is not pinned — it is an
/// implementation detail — so these compile what comes out and read a real dataset through it, which
/// is the only check that says the generator and the runtime agree.
/// </para>
/// <para>
/// Java emits a primitive accessor and a boxed one per numeric field. One nullable accessor says both
/// here: <see cref="Nullable{T}"/> is a struct, so the pair would buy nothing an API whose whole point
/// is allocating nothing.
/// </para>
/// </remarks>
public class PerfApiGeneratorTests
{
    private const string GeneratedNamespace = "Acme.Perf";

    [Fact]
    public void EveryObjectTypeGetsItsOwnClassAndTheCollectionsUseTheBuiltInOnes()
    {
        IReadOnlyDictionary<string, string> files = Generate();

        Assert.Contains("MoviePerfApi.cs", files.Keys);
        Assert.Contains("StudioPerfApi.cs", files.Keys);
        Assert.Contains("StringPerfApi.cs", files.Keys);
        Assert.Contains("MoviesPerformanceApi.cs", files.Keys);

        // A collection type has no fields of its own, so it needs no generated class.
        Assert.DoesNotContain("ListOfStringPerfApi.cs", files.Keys);

        Assert.Contains(
            "public HollowListTypePerfApi ListOfString { get; }",
            files["MoviesPerformanceApi.cs"],
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheGeneratedApiReadsARealDataset()
    {
        object api = CompiledApi();

        object movies = Read(api, "Movie")!;
        HollowRef reference = RefForOrdinal(movies, 0);

        Assert.Equal(1, Invoke(movies, "GetId", reference));
        Assert.Equal(1995, Invoke(movies, "GetYear", reference));
        Assert.Equal(true, Invoke(movies, "GetIsColour", reference));
        Assert.Equal(63_000_000m, Invoke(movies, "GetBudget", reference));
    }

    [Fact]
    public void AStringCanBeComparedWithoutMaterialisingIt()
    {
        object api = CompiledApi();

        object movies = Read(api, "Movie")!;
        object strings = Read(api, "String")!;

        HollowRef title = (HollowRef)Invoke(movies, "GetTitleRef", RefForOrdinal(movies, 0))!;

        Assert.Equal("Heat", Invoke(strings, "GetValue", title));
        Assert.Equal(true, Invoke(strings, "IsValueEqual", title, "Heat"));
        Assert.Equal(false, Invoke(strings, "IsValueEqual", title, "Ronin"));
    }

    [Fact]
    public void AReferenceYieldsARefIntoTheTypeItPointsAt()
    {
        object api = CompiledApi();

        object movies = Read(api, "Movie")!;
        object studios = Read(api, "Studio")!;

        HollowRef studio = (HollowRef)Invoke(movies, "GetStudioRef", RefForOrdinal(movies, 0))!;

        // The reference carries the type as well as the ordinal, which is what makes it safe to hand
        // to the studio API and nothing else.
        Assert.False(studio.IsNull);
        Assert.Equal(TypeIdentifier(studios), studio.Type);
        Assert.Equal("Warner Bros.", Invoke(studios, "GetName", studio));
    }

    [Fact]
    public void AFieldTheDatasetDoesNotHaveReadsAsAbsentRatherThanFailing()
    {
        // Generated against a model with a Year, run against a dataset written without one.
        Assembly assembly = GeneratedApiCompiler.Compile(Generate());

        HollowReadStateEngine engine = Dataset(typeof(OlderMovie), new OlderMovie(1, "Heat"));

        object api = Activator.CreateInstance(
            assembly.GetType($"{GeneratedNamespace}.MoviesPerformanceApi")!, engine)!;

        object movies = Read(api, "Movie")!;
        HollowRef reference = RefForOrdinal(movies, 0);

        // A consumer ahead of the dataset keeps working; the field it does not have reads as nothing.
        Assert.Null(Invoke(movies, "GetYear", reference));
        Assert.Null(Invoke(movies, "GetBudget", reference));
        Assert.Equal(1, Invoke(movies, "GetId", reference));
    }

    [Fact]
    public void AFieldExistsPropertyIsEmittedOnlyWhereAsked()
    {
        IReadOnlyDictionary<string, string> files = new HollowPerfApiGenerator(
            new HollowPerfApiGeneratorOptions
            {
                Namespace = GeneratedNamespace,
                ApiClassName = "MoviesPerformanceApi",
                CheckFieldExistsMethods = ["Movie.Year"],
            }).Generate(typeof(Movie));

        Assert.Contains("public bool YearFieldExists =>", files["MoviePerfApi.cs"], StringComparison.Ordinal);
        Assert.DoesNotContain("public bool IdFieldExists", files["MoviePerfApi.cs"], StringComparison.Ordinal);
    }

    [Fact]
    public void OptionsAreRequired() =>
        Assert.Throws<ArgumentNullException>(() => new HollowPerfApiGenerator(null!));

    private static IReadOnlyDictionary<string, string> Generate() =>
        new HollowPerfApiGenerator(Options()).Generate(typeof(Movie));

    private static HollowPerfApiGeneratorOptions Options() =>
        new() { Namespace = GeneratedNamespace, ApiClassName = "MoviesPerformanceApi" };

    private static object CompiledApi()
    {
        Assembly assembly = GeneratedApiCompiler.Compile(Generate());

        HollowReadStateEngine engine = Dataset(
            typeof(Movie),
            new Movie(1, "Heat", 1995, 63_000_000m, true, new Studio("Warner Bros."), ["Pacino"]));

        return Activator.CreateInstance(
            assembly.GetType($"{GeneratedNamespace}.MoviesPerformanceApi")!, engine)!;
    }

    private static HollowReadStateEngine Dataset(Type modelType, object record)
    {
        HollowWriteStateEngine writeEngine = new();
        HollowObjectMapper mapper = new(writeEngine);

        mapper.InitializeTypeState(modelType);
        mapper.Add(record);

        return StateEngineRoundTripper.RoundTripSnapshot(writeEngine);
    }

    private static HollowRef RefForOrdinal(object typeApi, int ordinal) =>
        ((HollowTypePerfApi)typeApi).RefForOrdinal(ordinal);

    private static int TypeIdentifier(object typeApi) =>
        ((HollowTypePerfApi)typeApi).RefForOrdinal(0).Type;

    private static object? Read(object instance, string member) =>
        instance.GetType().GetProperty(member)?.GetValue(instance);

    /// <summary>
    /// Invokes a generated accessor, refusing to treat one the generator never emitted as a null
    /// answer — which is the distinction several of these tests turn on.
    /// </summary>
    private static object? Invoke(object instance, string method, params object?[] arguments)
    {
        MethodInfo accessor = instance.GetType().GetMethod(method)
            ?? throw new InvalidOperationException(
                $"{instance.GetType().Name} has no {method}; the generator did not emit it");

        return accessor.Invoke(instance, arguments);
    }

    private sealed record Studio([property: HollowInline] string Name);

    [HollowPrimaryKey("Id")]
    private sealed record Movie(
        int Id,
        string Title,
        int Year,
        [property: HollowInline] decimal? Budget,
        bool IsColour,
        Studio Studio,
        List<string> Cast);

    /// <summary>The same type name with fewer fields, for the dataset that is behind the client.</summary>
    [HollowTypeName("Movie")]
    [HollowPrimaryKey("Id")]
    private sealed record OlderMovie(int Id, string Title);
}
