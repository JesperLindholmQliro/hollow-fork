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

using Hollow.Core.Read.DataAccess;

namespace Hollow.Api.Custom;

/// <summary>
/// A typed view over a Hollow dataset: one <see cref="HollowTypeApi"/> per type in the data model.
/// </summary>
/// <remarks>
/// <para>
/// This is the base of a generated API, and of a hand-written one. A subclass constructs a type API
/// for each type it knows about, registers it with <see cref="AddTypeApi"/>, and exposes accessors
/// that turn an ordinal into something worth reading.
/// </para>
/// <para>
/// Named <c>HollowAPI</c> in Java; see <see cref="HollowTypeApi"/> for why the acronym is spelled
/// <c>Api</c> here.
/// </para>
/// </remarks>
public class HollowApi
{
    private readonly List<HollowTypeApi> _typeApis = [];

    /// <summary>
    /// Initialises an API over <paramref name="dataAccess"/>.
    /// </summary>
    public HollowApi(IHollowDataAccess dataAccess)
    {
        ArgumentNullException.ThrowIfNull(dataAccess);

        DataAccess = dataAccess;
    }

    /// <summary>The dataset this API reads.</summary>
    public IHollowDataAccess DataAccess { get; }

    /// <summary>The type APIs registered with this one, in the order they were added.</summary>
    public IReadOnlyList<HollowTypeApi> TypeApis => _typeApis;

    /// <summary>
    /// Releases whatever the cached delegates are holding, so that a state this API was built over can
    /// be collected.
    /// </summary>
    /// <remarks>
    /// A no-op unless a subclass caches; one that does overrides this and detaches its caches.
    /// </remarks>
    public virtual void DetachCaches()
    {
        // Nothing cached here.
    }

    /// <summary>
    /// Registers <paramref name="typeApi"/> with this API.
    /// </summary>
    protected void AddTypeApi(HollowTypeApi typeApi)
    {
        ArgumentNullException.ThrowIfNull(typeApi);

        _typeApis.Add(typeApi);
    }
}
