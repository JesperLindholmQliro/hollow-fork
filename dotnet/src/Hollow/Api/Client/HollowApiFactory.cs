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

using System.Diagnostics.CodeAnalysis;
using Hollow.Api.Custom;
using Hollow.Core.Read.DataAccess;

namespace Hollow.Api.Client;

/// <summary>
/// Builds the typed API a consumer hands out, over whichever data it currently holds.
/// </summary>
/// <remarks>
/// A consumer rebuilds its API whenever it loads a snapshot, since none of the previous ordinals mean
/// anything afterwards. A delta keeps the same instance: the type APIs read through the same state
/// engine either way, and anything caching underneath is itself a delta listener.
/// </remarks>
public interface IHollowApiFactory
{
    /// <summary>Builds an API over <paramref name="dataAccess"/>.</summary>
    HollowApi CreateApi(IHollowDataAccess dataAccess);

    /// <summary>
    /// Builds an API over <paramref name="dataAccess"/>, given the one it replaces.
    /// </summary>
    /// <remarks>
    /// Defaults to ignoring <paramref name="previousCycleApi"/>, which is what an API with nothing
    /// cached wants.
    /// </remarks>
    HollowApi CreateApi(IHollowDataAccess dataAccess, HollowApi previousCycleApi) => CreateApi(dataAccess);
}

/// <summary>
/// Builds a plain <see cref="HollowApi"/>, which is what a consumer with no generated API gets.
/// </summary>
/// <remarks>
/// It carries no type APIs, so nothing typed can be read through it — the generic records
/// (<c>GenericHollowObject</c> and friends) read the state engine directly and do not need one.
/// </remarks>
public sealed class DefaultHollowApiFactory : IHollowApiFactory
{
    /// <summary>The shared instance; this factory holds no state.</summary>
    public static readonly DefaultHollowApiFactory Instance = new();

    private DefaultHollowApiFactory()
    {
    }

    /// <inheritdoc />
    public HollowApi CreateApi(IHollowDataAccess dataAccess) => new(dataAccess);
}

/// <summary>
/// Builds an API by calling the function it was given.
/// </summary>
/// <remarks>
/// What a hand-written API uses: <c>new DelegateHollowApiFactory(access =&gt; new MovieApi(access))</c>.
/// Java has no equivalent — it reaches for the generated class and reflects over its constructors,
/// which <see cref="GeneratedHollowApiFactory{TApi}"/> covers.
/// </remarks>
public sealed class DelegateHollowApiFactory : IHollowApiFactory
{
    private readonly Func<IHollowDataAccess, HollowApi> _create;
    private readonly Func<IHollowDataAccess, HollowApi, HollowApi>? _recreate;

    /// <summary>
    /// Builds every API with <paramref name="create"/>.
    /// </summary>
    /// <param name="create">Builds an API over a dataset.</param>
    /// <param name="recreate">
    /// Builds an API given the one it replaces, for an API that carries something across. Defaults to
    /// ignoring the previous one.
    /// </param>
    public DelegateHollowApiFactory(
        Func<IHollowDataAccess, HollowApi> create,
        Func<IHollowDataAccess, HollowApi, HollowApi>? recreate = null)
    {
        ArgumentNullException.ThrowIfNull(create);

        _create = create;
        _recreate = recreate;
    }

    /// <inheritdoc />
    public HollowApi CreateApi(IHollowDataAccess dataAccess) => _create(dataAccess);

    /// <inheritdoc />
    public HollowApi CreateApi(IHollowDataAccess dataAccess, HollowApi previousCycleApi) =>
        _recreate is null ? _create(dataAccess) : _recreate(dataAccess, previousCycleApi);
}

/// <summary>
/// Builds a generated API by its constructors, as the generator emits them.
/// </summary>
/// <remarks>
/// <para>
/// A generated API declares <c>(IHollowDataAccess)</c> and, where any type may be cached,
/// <c>(IHollowDataAccess, ISet&lt;string&gt;)</c> and
/// <c>(IHollowDataAccess, ISet&lt;string&gt;, IReadOnlyDictionary&lt;string, string&gt;, TApi)</c>.
/// This picks the widest one the generated class actually has.
/// </para>
/// <para>
/// Prefer <see cref="DelegateHollowApiFactory"/> where the API type is known at the call site: it
/// costs no reflection and survives trimming. This exists for the case Java's
/// <c>HollowAPIFactory.ForGeneratedAPI</c> exists for — wiring up a generated class by type.
/// </para>
/// </remarks>
/// <typeparam name="TApi">The generated API type.</typeparam>
public sealed class GeneratedHollowApiFactory<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TApi>
    : IHollowApiFactory
    where TApi : HollowApi
{
    private readonly HashSet<string> _cachedTypes;

    /// <summary>
    /// Builds <typeparamref name="TApi"/>, caching every record of the named types.
    /// </summary>
    /// <param name="cachedTypes">
    /// The types to cache whole, which the generated API only honours if it was generated with caching
    /// support.
    /// </param>
    public GeneratedHollowApiFactory(params string[] cachedTypes)
    {
        _cachedTypes = [.. cachedTypes];
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TApi"/> declares none of the constructors a generated API is expected to.
    /// </exception>
    public HollowApi CreateApi(IHollowDataAccess dataAccess) =>
        Construct([dataAccess, _cachedTypes]) ?? ConstructOrThrow([dataAccess]);

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TApi"/> declares none of the constructors a generated API is expected to.
    /// </exception>
    public HollowApi CreateApi(IHollowDataAccess dataAccess, HollowApi previousCycleApi) =>
        Construct([dataAccess, _cachedTypes, EmptyTypeSubstitutions, previousCycleApi])
        ?? ConstructOrThrow([dataAccess]);

    private static readonly Dictionary<string, string> EmptyTypeSubstitutions = [];

    private static HollowApi? Construct(object?[] arguments)
    {
        foreach (System.Reflection.ConstructorInfo candidate in typeof(TApi).GetConstructors())
        {
            System.Reflection.ParameterInfo[] parameters = candidate.GetParameters();

            if (parameters.Length != arguments.Length)
            {
                continue;
            }

            bool matches = true;

            for (int i = 0; i < parameters.Length; i++)
            {
                if (arguments[i] is not { } argument || !parameters[i].ParameterType.IsInstanceOfType(argument))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return (HollowApi)candidate.Invoke(arguments);
            }
        }

        return null;
    }

    private static HollowApi ConstructOrThrow(object?[] arguments) =>
        Construct(arguments)
        ?? throw new InvalidOperationException(
            $"{typeof(TApi)} has no constructor taking an {nameof(IHollowDataAccess)}, so it cannot be "
            + "built as a generated API; pass a "
            + $"{nameof(DelegateHollowApiFactory)} instead");
}
