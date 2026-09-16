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

using Hollow.Core;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Api.Codegen;

/// <summary>What to emit, and where, for a test data API.</summary>
public sealed class HollowTestDataGeneratorOptions
{
    /// <summary>The namespace the emitted files declare.</summary>
    public required string Namespace { get; init; }

    /// <summary>The name of the emitted dataset class.</summary>
    public string DatasetClassName { get; init; } = "TestDataset";

    internal TestDataEmitterOptions ToEmitterOptions() => new(Namespace, DatasetClassName);
}

/// <summary>
/// Emits a fluent builder per type, so that a test can describe a dataset in code rather than by
/// writing records against schemas by hand.
/// </summary>
/// <remarks>
/// <para>
/// The builders write through <c>Hollow.Api.TestData</c>, which is ported; this is the generator that
/// gives them names from a model. A test then reads the result through a consumer, which is what makes
/// it a test of the thing under test rather than of the fixture.
/// </para>
/// <para>
/// Java calls this <c>HollowTestDataAPIGenerator</c> and writes files to a directory itself. This
/// returns the sources, as the other two generators here do.
/// </para>
/// </remarks>
public sealed class HollowTestDataGenerator(HollowTestDataGeneratorOptions options)
{
    private readonly TestDataEmitter _emitter =
        new((options ?? throw new ArgumentNullException(nameof(options))).ToEmitterOptions());

    /// <summary>
    /// Emits builders for the model <paramref name="modelTypes"/> describe, as file name to source.
    /// </summary>
    public IReadOnlyDictionary<string, string> Generate(params Type[] modelTypes)
    {
        ArgumentNullException.ThrowIfNull(modelTypes);

        HollowWriteStateEngine writeEngine = new();
        HollowObjectMapper mapper = new(writeEngine);

        foreach (Type modelType in modelTypes)
        {
            mapper.InitializeTypeState(modelType);
        }

        return Generate(writeEngine);
    }

    /// <summary>
    /// Emits builders for the model <paramref name="dataset"/> declares, as file name to source.
    /// </summary>
    public IReadOnlyDictionary<string, string> Generate(IHollowDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        return _emitter.Emit(dataset.Schemas.Select(HollowCodeGenerator.ToModelSchema));
    }
}
