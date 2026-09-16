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

using System.Collections;
using System.Reflection;
using Hollow.Api.Custom;
using Hollow.Api.Sampling;
using Hollow.Core.Read.Engine;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;
using Hollow.Tests.Api.Codegen;

namespace Hollow.Tests.Api.Sampling;

/// <summary>
/// Counting the record wrappers a generated API hands out.
/// </summary>
/// <remarks>
/// An allocation count rather than a read count, and the one sampler only generated code can feed —
/// which is why it is tested against a real generated API rather than a hand-written one. What it
/// answers is which types a caller is materialising, and so whether caching them or moving to the
/// performance API would pay.
/// </remarks>
public class CreationSamplingTests
{
    private const string ModelSource = """
        using Hollow.Api.Codegen;
        using Hollow.Core.Write.ObjectMapper;

        namespace Acme.Model;

        [HollowGeneratedApi(Namespace = "Acme.Generated", ApiClassName = "MoviesApi")]
        [HollowPrimaryKey("Id")]
        public sealed record Movie(int Id, string Title);
        """;

    [Fact]
    public void AGeneratedApiCountsTheWrappersItHandsOut()
    {
        HollowApi api = GeneratedApi();

        api.SetSamplingDirector(new EnabledSamplingDirector());

        Materialise(api);

        Assert.Equal(3, Creations(api, "Movie"));

        // Nothing asked for the strings as records, so none were made.
        Assert.Equal(0, Creations(api, "String"));
    }

    [Fact]
    public void NoWrapperIsCountedUntilSamplingIsTurnedOn()
    {
        HollowApi api = GeneratedApi();

        Materialise(api);

        Assert.False(api.ObjectCreationSampler.HasSampleResults);
    }

    [Fact]
    public void ResettingThroughTheApiClearsTheCreationCountsToo()
    {
        HollowApi api = GeneratedApi();

        api.SetSamplingDirector(new EnabledSamplingDirector());

        Materialise(api);

        Assert.True(api.ObjectCreationSampler.HasSampleResults);

        api.ResetSampling();

        Assert.False(api.ObjectCreationSampler.HasSampleResults);
    }

    [Fact]
    public void CreationsAreReportedApartFromReads()
    {
        HollowApi api = GeneratedApi();

        api.SetSamplingDirector(new EnabledSamplingDirector());

        Materialise(api);

        // An allocation is not a read, so mixing the two into one list would be a category error:
        // the field results say what was looked at, these say what was built to look at it with.
        Assert.DoesNotContain(
            api.GetSampleResults(),
            result => string.Equals(result.Identifier, "Movie", StringComparison.Ordinal));

        Assert.Contains(
            api.ObjectCreationSampler.GetSampleResults(),
            result => string.Equals(result.Identifier, "Movie", StringComparison.Ordinal));
    }

    private static long Creations(HollowApi api, string typeName) =>
        api.ObjectCreationSampler.GetSampleResults()
            .Single(result => string.Equals(result.Identifier, typeName, StringComparison.Ordinal))
            .SampleCount;

    /// <summary>Walks every film, which is what makes the API hand out a wrapper per record.</summary>
    private static void Materialise(HollowApi api) =>
        _ = ((IEnumerable)Read(api, "AllMovie")!).Cast<object>().ToList();

    private static HollowApi GeneratedApi()
    {
        (Assembly assembly, _, _) = SourceGeneratorDriver.Run(ModelSource);

        HollowWriteStateEngine writeEngine = new();
        HollowReadStateEngine readEngine = new();
        HollowObjectMapper mapper = new(writeEngine);

        mapper.InitializeTypeState(typeof(Movie));
        mapper.Add(new Movie(1, "Heat"));
        mapper.Add(new Movie(2, "Ronin"));
        mapper.Add(new Movie(3, "Collateral"));

        StateEngineRoundTripper.RoundTripSnapshot(writeEngine, readEngine);

        return (HollowApi)Activator.CreateInstance(
            assembly.GetType("Acme.Generated.MoviesApi")!, readEngine)!;
    }

    private static object? Read(object instance, string member) =>
        instance.GetType().GetProperty(member)?.GetValue(instance)
        ?? instance.GetType().GetMethod(member, [])?.Invoke(instance, []);

    /// <summary>The same model as CLR types, for the object mapper to derive schemas from.</summary>
    [HollowPrimaryKey("Id")]
    private sealed record Movie(int Id, string Title);
}
