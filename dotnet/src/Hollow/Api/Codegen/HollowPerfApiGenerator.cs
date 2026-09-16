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

/// <summary>
/// What to emit, and where, for a performance API.
/// </summary>
/// <remarks>
/// Java carries these on a builder nested inside the generator. An init-only options object says the
/// same thing and makes the required ones required.
/// </remarks>
public sealed class HollowPerfApiGeneratorOptions
{
    /// <summary>The namespace the emitted files declare.</summary>
    public required string Namespace { get; init; }

    /// <summary>The name of the emitted API class.</summary>
    public string ApiClassName { get; init; } = "PerformanceApi";

    /// <summary>
    /// The <c>Type.field</c> names that also get a property saying whether the loaded dataset has the
    /// field at all.
    /// </summary>
    /// <remarks>
    /// Worth asking only where a client is knowingly ahead of some of the datasets it reads: every
    /// accessor already answers absent for a field the dataset lacks, so this is for the caller that
    /// wants to branch rather than take the absent value.
    /// </remarks>
    public IReadOnlyCollection<string> CheckFieldExistsMethods { get; init; } = [];

    internal PerfApiEmitterOptions ToEmitterOptions() =>
        new(Namespace, ApiClassName, CheckFieldExistsMethods.ToHashSet(StringComparer.Ordinal));
}

/// <summary>
/// Emits a performance API for a data model: a class per object type whose accessors read a field
/// with a constant index, and nothing that allocates.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <see cref="HollowCodeGenerator"/>, which emits record wrappers. Use this where
/// a process walks millions of records in a loop and the wrappers would be the cost — see the
/// performance API section of <c>PORTING.md</c>.
/// </para>
/// <para>
/// Java calls this <c>HollowPerformanceAPIGenerator</c> and writes files to a directory itself. This
/// returns the sources, as <see cref="HollowCodeGenerator"/> does, and leaves writing them to
/// <see cref="HollowCodeGenerator.WriteTo"/> — which a build that generates into its source tree
/// needs, since it skips files whose content has not changed.
/// </para>
/// </remarks>
public sealed class HollowPerfApiGenerator(HollowPerfApiGeneratorOptions options)
{
    private readonly PerfApiEmitter _emitter =
        new((options ?? throw new ArgumentNullException(nameof(options))).ToEmitterOptions());

    /// <summary>
    /// Emits a performance API for the model <paramref name="modelTypes"/> describe, as file name to
    /// source.
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
    /// Emits a performance API for the model <paramref name="dataset"/> declares, as file name to
    /// source.
    /// </summary>
    public IReadOnlyDictionary<string, string> Generate(IHollowDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        return _emitter.Emit(dataset.Schemas.Select(HollowCodeGenerator.ToModelSchema));
    }
}
