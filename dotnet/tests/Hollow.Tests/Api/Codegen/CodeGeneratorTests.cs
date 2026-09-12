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

using System.Reflection;
using Hollow.Api.Codegen;
using Hollow.Api.Consumer;
using Hollow.Api.Custom;
using Hollow.Api.Objects;
using Hollow.Api.Objects.Generic;
using Hollow.Core.Index.Key;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Engine;
using Hollow.Core.Schema;
using Hollow.Core.Types;
using Hollow.Core.Util;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Tests.Api.Codegen;

/// <summary>
/// The code generator, checked by compiling what it emits and reading real records through it.
/// </summary>
/// <remarks>
/// <para>
/// "It generates the right text" is not a claim worth testing — the text is an implementation detail
/// and pinning it makes every improvement a test change. What matters is that the output compiles
/// without a warning and reads the same records the untyped API does, so that is what these assert.
/// </para>
/// <para>
/// The generated types are reached by reflection because they do not exist until the test runs. That
/// is awkward to read, and it is the price of testing a generator honestly rather than testing a
/// hand-written stand-in for its output.
/// </para>
/// </remarks>
public class CodeGeneratorTests
{
    // ---- The data model the client is generated for. ----

    private sealed record Actor(string Name, int? Age);

    private sealed record Studio(string Name);

    [HollowPrimaryKey("Id")]
    private sealed record Movie(
        int Id,
        string Title,
        int Year,
        decimal? Budget,
        bool IsColour,
        byte[]? Poster,
        Studio Studio,
        List<Actor> Cast,
        HashSet<string> Tags,
        Dictionary<string, Actor> Roles);

    private static readonly Movie TheMatrix = new(
        1, "The Matrix", 1999, 63_000_000m, true, [1, 2, 3],
        new Studio("Warner Bros."),
        [new Actor("Keanu Reeves", 34), new Actor("Laurence Fishburne", null)],
        ["sci-fi", "action"],
        new Dictionary<string, Actor> { ["Neo"] = new Actor("Keanu Reeves", 34) });

    private static readonly Movie JohnWick = new(
        2, "John Wick", 2014, null, true, null,
        new Studio("Lionsgate"),
        [new Actor("Keanu Reeves", 34)],
        ["action"],
        new Dictionary<string, Actor> { ["John"] = new Actor("Keanu Reeves", 34) });

    internal const string GeneratedNamespace = "Acme.Movies";

    /// <summary>
    /// The generated client, compiled once and shared, for a test that only reads what was emitted
    /// rather than what it does with data.
    /// </summary>
    internal static Assembly CompiledClient() =>
        GeneratedApiCompiler.Compile(Generator().Generate(typeof(Movie)));

    private static HollowCodeGenerator Generator(HollowCodeGeneratorOptions? options = null) =>
        new(options ?? new HollowCodeGeneratorOptions { Namespace = GeneratedNamespace });

    /// <summary>A read state holding the two films, for a test that only needs data to point at.</summary>
    internal static HollowReadStateEngine TwoFilmsReadState() => Populate(TheMatrix, JohnWick).Read;

    /// <summary>Writes the movies and returns a read state over them.</summary>
    private static (HollowWriteStateEngine Write, HollowReadStateEngine Read, HollowObjectMapper Mapper)
        Populate(params Movie[] movies)
    {
        HollowWriteStateEngine writeEngine = new();
        HollowReadStateEngine readEngine = new();
        HollowObjectMapper mapper = new(writeEngine);
        mapper.InitializeTypeState(typeof(Movie));

        foreach (Movie movie in movies)
        {
            mapper.Add(movie);
        }

        StateEngineRoundTripper.RoundTripSnapshot(writeEngine, readEngine);

        return (writeEngine, readEngine, mapper);
    }

    /// <summary>Generates, compiles and instantiates the API over <paramref name="dataAccess"/>.</summary>
    private static (HollowApi Api, Assembly Assembly) GeneratedApi(
        IHollowDataAccess dataAccess, HollowCodeGeneratorOptions? options = null)
    {
        Assembly assembly = GeneratedApiCompiler.Compile(Generator(options).Generate(typeof(Movie)));
        Type apiType = assembly.GetType($"{GeneratedNamespace}.MoviesApi")!;

        return ((HollowApi)Activator.CreateInstance(apiType, dataAccess)!, assembly);
    }

    private static object? Read(object instance, string member) =>
        instance.GetType().GetProperty(member)?.GetValue(instance)
        ?? instance.GetType().GetMethod(member, [])?.Invoke(instance, []);

    private static object First(HollowApi api, string typeName)
    {
        object all = Read(api, "All" + typeName)!;

        return ((System.Collections.IEnumerable)all).Cast<object>().First();
    }

    // ---- What the generator emits ----

    [Fact]
    public void EmitsOneFilePerTypeAndOneForTheApi()
    {
        IReadOnlyDictionary<string, string> files = Generator().Generate(typeof(Movie));

        // A class per model type, the built-in scalars excepted: those are in Hollow.Core.Types
        // already, so generating another set would be six more classes saying the same thing.
        Assert.Contains("Movie.cs", files.Keys);
        Assert.Contains("Actor.cs", files.Keys);
        Assert.Contains("Studio.cs", files.Keys);
        Assert.Contains("ListOfActor.cs", files.Keys);
        Assert.Contains("SetOfString.cs", files.Keys);
        Assert.Contains("MapOfStringToActor.cs", files.Keys);
        Assert.Contains("MoviesApi.cs", files.Keys);

        Assert.DoesNotContain("String.cs", files.Keys);
        Assert.DoesNotContain("Integer.cs", files.Keys);

        // The primary key Movie declares gets an index; nothing else does.
        Assert.Contains("MovieUniqueKeyIndex.cs", files.Keys);
        Assert.DoesNotContain("ActorUniqueKeyIndex.cs", files.Keys);

        foreach (string source in files.Values)
        {
            Assert.StartsWith("// <auto-generated />", source, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The claim the whole generator rests on: what comes out is valid C#, and compiles clean. A
    /// warning in generated source is one the caller cannot fix.
    /// </summary>
    [Fact]
    public void TheGeneratedCodeCompilesWithoutAWarning()
    {
        Assert.NotNull(GeneratedApiCompiler.Compile(Generator().Generate(typeof(Movie))));
    }

    // ---- What the generated client reads ----

    [Fact]
    public void ReadsEveryKindOfValueField()
    {
        (_, HollowReadStateEngine readEngine, _) = Populate(TheMatrix);
        (HollowApi api, _) = GeneratedApi(readEngine);

        object movie = First(api, "Movie");

        Assert.Equal(1, Read(movie, "Id"));
        Assert.Equal(1999, Read(movie, "Year"));
        Assert.Equal(true, Read(movie, "IsColour"));
        Assert.Equal(new byte[] { 1, 2, 3 }, Read(movie, "Poster"));
    }

    /// <summary>
    /// Java emits a primitive getter and a boxed one per numeric field, because only the boxed one can
    /// be null. One nullable property covers both, and a null field reads as null rather than as a
    /// sentinel.
    /// </summary>
    [Fact]
    public void ANullableFieldReadsAsNull()
    {
        (_, HollowReadStateEngine readEngine, _) = Populate(JohnWick);
        (HollowApi api, _) = GeneratedApi(readEngine);

        object movie = First(api, "Movie");

        Assert.Null(Read(movie, "Budget"));
        Assert.Null(Read(movie, "Poster"));
        Assert.Equal(2014, Read(movie, "Year"));
    }

    /// <summary>
    /// A field referencing a type with a single value field reads as that value, so <c>movie.Title</c>
    /// is a string rather than an <c>HString</c>. The wrapper is still reachable.
    /// </summary>
    [Fact]
    public void AReferenceToASingleValueTypeReadsAsItsValue()
    {
        (_, HollowReadStateEngine readEngine, _) = Populate(TheMatrix);
        (HollowApi api, _) = GeneratedApi(readEngine);

        object movie = First(api, "Movie");

        Assert.Equal("The Matrix", Read(movie, "Title"));
        Assert.Equal(63_000_000m, Read(movie, "Budget"));

        HString title = Assert.IsType<HString>(Read(movie, "TitleRecord"));
        Assert.Equal("The Matrix", title.Value);
    }

    [Fact]
    public void TheShortcutCanBeTurnedOff()
    {
        (_, HollowReadStateEngine readEngine, _) = Populate(TheMatrix);
        (HollowApi api, _) = GeneratedApi(
            readEngine,
            new HollowCodeGeneratorOptions { Namespace = GeneratedNamespace, UseErgonomicShortcuts = false });

        object movie = First(api, "Movie");

        // Without the shortcut the property is the wrapper itself, and there is no Record suffix.
        HString title = Assert.IsType<HString>(Read(movie, "Title"));
        Assert.Equal("The Matrix", title.Value);
        Assert.Null(movie.GetType().GetProperty("TitleRecord"));
    }

    [Fact]
    public void FollowsAReferenceToAGeneratedType()
    {
        (_, HollowReadStateEngine readEngine, _) = Populate(TheMatrix);
        (HollowApi api, _) = GeneratedApi(readEngine);

        object studio = Read(First(api, "Movie"), "Studio")!;

        Assert.Equal("Warner Bros.", Read(studio, "Name"));
    }

    [Fact]
    public void ReadsAListAsAReadOnlyList()
    {
        (_, HollowReadStateEngine readEngine, _) = Populate(TheMatrix);
        (HollowApi api, _) = GeneratedApi(readEngine);

        object cast = Read(First(api, "Movie"), "Cast")!;
        List<object> actors = [.. ((System.Collections.IEnumerable)cast).Cast<object>()];

        Assert.Equal(2, actors.Count);
        Assert.Equal(
            ["Keanu Reeves", "Laurence Fishburne"],
            actors.Select(actor => (string?)Read(actor, "Name")).Order(StringComparer.Ordinal));

        // Nullable through a collection element too.
        Assert.Null(Read(actors.Single(actor => (string?)Read(actor, "Name") == "Laurence Fishburne"), "Age"));
    }

    [Fact]
    public void ReadsASetAndAMap()
    {
        (_, HollowReadStateEngine readEngine, _) = Populate(TheMatrix);
        (HollowApi api, _) = GeneratedApi(readEngine);

        object movie = First(api, "Movie");

        object tags = Read(movie, "Tags")!;
        Assert.Equal(
            ["action", "sci-fi"],
            ((System.Collections.IEnumerable)tags).Cast<HString>()
                .Select(tag => tag.Value)
                .Order(StringComparer.Ordinal));

        object roles = Read(movie, "Roles")!;
        List<KeyValuePair<HString, object>> entries =
        [
            .. ((System.Collections.IEnumerable)roles).Cast<object>()
                .Select(entry => new KeyValuePair<HString, object>(
                    (HString)Read(entry, "Key")!, Read(entry, "Value")!)),
        ];

        Assert.Equal("Neo", Assert.Single(entries).Key.Value);
        Assert.Equal("Keanu Reeves", Read(entries[0].Value, "Name"));
    }

    /// <summary>
    /// A string field is compared without the stored string being materialised, which is the one read
    /// the typed layer can do that a naive wrapper cannot.
    /// </summary>
    [Fact]
    public void ComparesAStringWithoutMaterialisingIt()
    {
        (_, HollowReadStateEngine readEngine, _) = Populate(TheMatrix);
        (HollowApi api, _) = GeneratedApi(readEngine);

        object studio = Read(First(api, "Movie"), "Studio")!;
        MethodInfo isNameEqual = studio.GetType().GetMethod("IsNameEqual", [typeof(string)])!;

        Assert.Equal(true, isNameEqual.Invoke(studio, ["Warner Bros."]));
        Assert.Equal(false, isNameEqual.Invoke(studio, ["Lionsgate"]));

        // And on the record whose field is the reference: the comparison goes through the shared
        // String record without the stored text ever being built.
        object movie = First(api, "Movie");
        MethodInfo isTitleEqual = movie.GetType().GetMethod("IsTitleEqual", [typeof(string)])!;

        Assert.Equal(true, isTitleEqual.Invoke(movie, [Read(movie, "Title")]));
        Assert.Equal(false, isTitleEqual.Invoke(movie, ["Nothing"]));
    }

    [Fact]
    public void EnumeratesEveryRecordOfAType()
    {
        (_, HollowReadStateEngine readEngine, _) = Populate(TheMatrix, JohnWick);
        (HollowApi api, _) = GeneratedApi(readEngine);

        IEnumerable<object> movies = ((System.Collections.IEnumerable)Read(api, "AllMovie")!).Cast<object>();

        Assert.Equal(
            ["John Wick", "The Matrix"],
            movies.Select(movie => (string?)Read(movie, "Title")).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Every record of the API's types, read back through the untyped generic layer, has to agree with
    /// what the generated client says. That is the check that the generated field positions are right.
    /// </summary>
    [Fact]
    public void AgreesWithTheUntypedLayerOnEveryRecord()
    {
        (_, HollowReadStateEngine readEngine, _) = Populate(TheMatrix, JohnWick);
        (HollowApi api, _) = GeneratedApi(readEngine);

        foreach (object movie in ((System.Collections.IEnumerable)Read(api, "AllMovie")!).Cast<object>())
        {
            GenericHollowObject generic = new(
                readEngine, "Movie", ((IHollowRecord)movie).Ordinal);

            Assert.Equal(generic.GetInt("Id"), Read(movie, "Id"));
            Assert.Equal(generic.GetInt("Year"), Read(movie, "Year"));
            Assert.Equal(
                generic.GetObject("Title")!.GetString("value"), Read(movie, "Title"));
            // A nullable byte[] is stored as a reference to a shared Bytes record, so the untyped read
            // has to follow the reference where the generated property does it for you.
            Assert.Equal(generic.GetObject("Poster")?.GetBytes("value"), Read(movie, "Poster"));
        }
    }

    // ---- Caching, missing types and the index ----

    /// <summary>
    /// Caching a type is a construction argument rather than a code change, and a cached record's
    /// fields are read once rather than on every access.
    /// </summary>
    [Fact]
    public void ATypeCanBeReadThroughACache()
    {
        (_, HollowReadStateEngine readEngine, _) = Populate(TheMatrix, JohnWick);

        Assembly assembly = GeneratedApiCompiler.Compile(Generator().Generate(typeof(Movie)));
        Type apiType = assembly.GetType($"{GeneratedNamespace}.MoviesApi")!;

        HollowApi api = (HollowApi)Activator.CreateInstance(
            apiType, readEngine, new HashSet<string>(StringComparer.Ordinal) { "Movie" })!;

        MethodInfo getMovie = apiType.GetMethod("GetMovie", [typeof(int)])!;

        object johnWick = ((System.Collections.IEnumerable)Read(api, "AllMovie")!).Cast<object>()
            .Single(movie => (string?)Read(movie, "Title") == "John Wick");
        int ordinal = ((IHollowRecord)johnWick).Ordinal;

        object? first = getMovie.Invoke(api, [ordinal]);
        object? second = getMovie.Invoke(api, [ordinal]);

        // The same wrapper both times, which is what caching a type buys.
        Assert.Same(first, second);
        Assert.Equal("John Wick", Read(first!, "Title"));

        api.DetachCaches();
    }

    /// <summary>
    /// A client generated from one model, pointed at a dataset written from another. The type it wants
    /// is simply not there, and every read of it goes to the missing-data handler rather than failing.
    /// </summary>
    [Fact]
    public void AClientReadsADatasetMissingAWholeType()
    {
        // The dataset holds only actors; the generated client expects the whole movie model.
        HollowWriteStateEngine writeEngine = new();
        HollowReadStateEngine readEngine = new();
        HollowObjectMapper mapper = new(writeEngine);
        mapper.InitializeTypeState(typeof(Actor));
        mapper.Add(new Actor("Keanu Reeves", 34));
        StateEngineRoundTripper.RoundTripSnapshot(writeEngine, readEngine);

        (HollowApi api, Assembly assembly) = GeneratedApi(readEngine);

        // The type API knows it is standing in for a type that is not there.
        object movieTypeApi = Read(api, "MovieTypeApi")!;
        Assert.False((bool)Read(movieTypeApi, "IsTypePresent")!);

        // So the type holds no records, rather than throwing.
        Assert.Empty(((System.Collections.IEnumerable)Read(api, "AllMovie")!).Cast<object>());

        // And the type that is there still reads.
        Assert.Equal("Keanu Reeves", Read(First(api, "Actor"), "Name"));

        _ = assembly;
    }

    /// <summary>
    /// A consumer holding the two films, and the generated client over it.
    /// </summary>
    private static (HollowConsumer Consumer, Assembly Assembly) TwoFilms()
    {
        InMemoryBlobStore store = new();
        HollowWriteStateEngine writeEngine = new();
        HollowObjectMapper mapper = new(writeEngine);
        mapper.InitializeTypeState(typeof(Movie));
        mapper.Add(TheMatrix);
        mapper.Add(JohnWick);
        store.Publish(writeEngine, 1);

        Assembly assembly = GeneratedApiCompiler.Compile(Generator().Generate(typeof(Movie)));
        Type apiType = assembly.GetType($"{GeneratedNamespace}.MoviesApi")!;

        HollowConsumer consumer = new HollowConsumerBuilder()
            .WithBlobRetriever(store)
            .WithApiFactory(new Hollow.Api.Client.DelegateHollowApiFactory(
                access => (HollowApi)Activator.CreateInstance(apiType, access)!))
            .Build();

        consumer.TriggerRefreshTo(1);

        return (consumer, assembly);
    }

    /// <summary>
    /// A type declaring a primary key gets a record naming and typing that key, and an index that takes
    /// it — rather than a loose <c>object[]</c> whose order only the schema knows.
    /// </summary>
    [Fact]
    public void TheGeneratedIndexFindsARecordByItsDeclaredKey()
    {
        (HollowConsumer consumer, Assembly assembly) = TwoFilms();
        Type indexType = assembly.GetType($"{GeneratedNamespace}.MovieUniqueKeyIndex")!;
        Type keyType = assembly.GetType($"{GeneratedNamespace}.MoviePrimaryKey")!;

        // The key's one component is typed from the schema, not taken as object.
        Assert.Equal(
            typeof(int), keyType.GetProperty("Id")!.PropertyType);

        object index = Activator.CreateInstance(indexType, consumer)!;
        MethodInfo findMatch = indexType.GetMethod("FindMatch")!;

        Assert.Equal(keyType, findMatch.GetParameters().Single().ParameterType);

        object Key(int id) => Activator.CreateInstance(keyType, id)!;

        Assert.Equal("John Wick", Read(findMatch.Invoke(index, [Key(2)])!, "Title"));
        Assert.Equal("The Matrix", Read(findMatch.Invoke(index, [Key(1)])!, "Title"));
        Assert.Null(findMatch.Invoke(index, [Key(99)]));
    }

    /// <summary>
    /// The API can do the same lookup itself, so the common case needs no index object at all. It
    /// builds one on first use and keeps it, so a second lookup does not rebuild it.
    /// </summary>
    [Fact]
    public void TheGeneratedApiLooksUpARecordByItsKey()
    {
        (HollowConsumer consumer, Assembly assembly) = TwoFilms();
        Type keyType = assembly.GetType($"{GeneratedNamespace}.MoviePrimaryKey")!;

        HollowApi api = consumer.Api!;
        MethodInfo findMovie = api.GetType().GetMethod("FindMovie")!;

        object Key(int id) => Activator.CreateInstance(keyType, id)!;

        Assert.Equal("The Matrix", Read(findMovie.Invoke(api, [Key(1)])!, "Title"));
        Assert.Equal("John Wick", Read(findMovie.Invoke(api, [Key(2)])!, "Title"));
        Assert.Null(findMovie.Invoke(api, [Key(99)]));

        // The wrapper it hands back is the same one the ordinal accessor gives, so a lookup is a way
        // into the client rather than a parallel one.
        Assert.Equal(
            Read(findMovie.Invoke(api, [Key(1)])!, "Title"),
            Read(api.GetType().GetMethod("GetMovie")!.Invoke(api, [0])!, "Title"));

        // And detaching releases the index it built, which is what stops it outliving the data.
        api.DetachCaches();
        Assert.Equal("The Matrix", Read(findMovie.Invoke(api, [Key(1)])!, "Title"));
    }

    /// <summary>
    /// A key spelled across several fields is one record with one property per field, so two key fields
    /// of the same type cannot be passed the wrong way round.
    /// </summary>
    [Fact]
    public void ACompoundKeyBecomesARecordOfItsParts()
    {
        Assembly assembly = GeneratedApiCompiler.Compile(Generator().Generate(typeof(Screening)));
        Type keyType = assembly.GetType($"{GeneratedNamespace}.ScreeningPrimaryKey")!;

        // Title crosses a reference into the shared String type, and still resolves to string rather
        // than to object.
        Assert.Equal(typeof(string), keyType.GetProperty("Title")!.PropertyType);
        Assert.Equal(typeof(int), keyType.GetProperty("Year")!.PropertyType);

        Assert.Equal(
            ["Title", "Year"],
            keyType.GetConstructors().Single().GetParameters().Select(parameter => parameter.Name));
    }

    [HollowPrimaryKey("Title", "Year")]
    private sealed record Screening(string Title, int Year, string Venue);

    /// <summary>
    /// The generated API is what a consumer hands out, which is the whole point of generating it.
    /// </summary>
    [Fact]
    public void AConsumerHandsOutTheGeneratedApi()
    {
        InMemoryBlobStore store = new();
        HollowWriteStateEngine writeEngine = new();
        HollowObjectMapper mapper = new(writeEngine);
        mapper.InitializeTypeState(typeof(Movie));
        mapper.Add(TheMatrix);
        store.Publish(writeEngine, 1);

        Assembly assembly = GeneratedApiCompiler.Compile(Generator().Generate(typeof(Movie)));
        Type factoryType = assembly.GetType($"{GeneratedNamespace}.MoviesApiFactory")!;

        HollowConsumer consumer = new HollowConsumerBuilder()
            .WithBlobRetriever(store)
            .WithApiFactory((Hollow.Api.Client.IHollowApiFactory)Activator.CreateInstance(
                factoryType, new object[] { Array.Empty<string>() })!)
            .Build();

        consumer.TriggerRefreshTo(1);

        Assert.Equal("The Matrix", Read(First(consumer.Api!, "Movie"), "Title"));
    }

    // ---- Naming ----

    /// <summary>
    /// A type name that is not a C# identifier still has to generate one, and two that would collide
    /// have to be refused rather than silently producing one class.
    /// </summary>
    [Fact]
    public void RefusesTwoTypesThatWouldGenerateTheSameClass()
    {
        HollowObjectSchema first = new("my-type", 1, (PrimaryKey?)null);
        first.AddField("value", FieldType.Int);

        HollowObjectSchema second = new("my_type", 1, (PrimaryKey?)null);
        second.AddField("value", FieldType.Int);

        SimpleHollowDataset dataset = new([first, second]);

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(() => _ = Generator().Generate(dataset));

        Assert.Contains("MyType", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The generator's table of built-in scalar wrappers has to agree with the one the runtime uses to
    /// instantiate them, or a generated API would name a class that reads a different field type.
    /// </summary>
    [Fact]
    public void TheBuiltInScalarTableAgreesWithTheRuntime()
    {
        foreach (FieldType fieldType in Enum.GetValues<FieldType>())
        {
            if (fieldType == FieldType.Reference || fieldType == FieldType.Bytes)
            {
                continue;
            }

            HollowObjectSchema schema = new("Scalar", 1, (PrimaryKey?)null);
            schema.AddField("value", fieldType);

            HollowWriteStateEngine writeEngine = HollowWriteStateCreator.CreateWithSchemas([schema]);
            HollowReadStateEngine readEngine = StateEngineRoundTripper.RoundTripSnapshot(writeEngine);

            IHollowObjectTypeDataAccess access =
                (IHollowObjectTypeDataAccess)readEngine.GetTypeState("Scalar")!;

            HollowObject wrapper = HollowScalarTypes.Instantiate(access, 0);
            HollowObjectTypeApi typeApi = HollowScalarTypes.TypeApiFor(new HollowApi(readEngine), access)!;

            Assert.Equal(
                wrapper.GetType().Name + "TypeApi",
                typeApi.GetType().Name);
        }
    }
}
