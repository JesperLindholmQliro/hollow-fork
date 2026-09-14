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

using System.Collections.Concurrent;
using Hollow.Core.Read.DataAccess.Disabled;
using Hollow.Core.Read.Missing;
using Hollow.Core.Schema;

namespace Hollow.Core.Read.DataAccess.Proxy;

/// <summary>
/// A dataset that forwards every read to another one, and can be pointed somewhere else afterwards.
/// </summary>
/// <remarks>
/// <para>
/// This is what object longevity is built on. A typed API holds its data access for as long as the
/// caller holds the API, so ordinarily a record read through it after a delta would read whatever now
/// sits at that ordinal — a different record. With longevity enabled the API is built over one of
/// these instead, and on each delta the outgoing proxy is pointed at a historical state holding the
/// records that transition removed. A caller's existing references keep reading what they always read;
/// new references come from the new API, over the live state.
/// </para>
/// <para>
/// Named <c>HollowProxyDataAccess</c> in Java. The port adds <see cref="WasRead"/>, which stands in
/// for Java's use of the <c>api.sampling</c> framework to tell whether a stale reference is being used
/// — see <c>PORTING.md</c>.
/// </para>
/// </remarks>
public sealed class HollowProxyDataAccess : IHollowDataAccess
{
    private readonly ConcurrentDictionary<string, HollowTypeProxyDataAccess> _typeDataAccesses =
        new(StringComparer.Ordinal);

    private IHollowDataAccess _currentDataAccess = HollowDisabledDataAccess.Instance;
    private bool _wasRead;

    /// <summary>The dataset reads are currently forwarded to.</summary>
    public IHollowDataAccess ProxiedDataAccess => _currentDataAccess;

    /// <inheritdoc />
    public IReadOnlyList<HollowSchema> Schemas => _currentDataAccess.Schemas;

    /// <inheritdoc />
    public IMissingDataHandler MissingDataHandler => _currentDataAccess.MissingDataHandler;

    /// <summary>
    /// Whether any record has been read through this proxy since <see cref="ResetWasRead"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stale reference detector uses this to tell a reference that is merely still reachable from
    /// one that is genuinely in use. Java asks the same question of the sampling framework, through
    /// <c>hasSampleResults()</c>; this port does not have sampling, and the proxy is already on every
    /// read, so the flag lives here instead.
    /// </para>
    /// <para>
    /// Deliberately not interlocked. Setting it is on the read path of every record, and the only
    /// reader is a housekeeping timer — so the cost of a plain write matters and a missed write does
    /// not, since it only delays a drop by one housekeeping interval.
    /// </para>
    /// </remarks>
    public bool WasRead => Volatile.Read(ref _wasRead);

    /// <summary>Forwards reads to <paramref name="dataAccess"/> from now on.</summary>
    /// <remarks>
    /// The per-type proxies are reused rather than rebuilt, because the records a caller already holds
    /// point at those objects. Repointing them is the whole trick.
    /// </remarks>
    public void SetDataAccess(IHollowDataAccess dataAccess)
    {
        ArgumentNullException.ThrowIfNull(dataAccess);

        _currentDataAccess = dataAccess;

        foreach (HollowSchema schema in dataAccess.Schemas)
        {
            if (dataAccess.GetTypeDataAccess(schema.Name) is not { } typeDataAccess)
            {
                continue;
            }

            HollowTypeProxyDataAccess proxy = _typeDataAccesses.GetOrAdd(
                schema.Name, _ => CreateProxy(typeDataAccess));

            proxy.SetCurrentDataAccess(typeDataAccess);
        }
    }

    /// <summary>
    /// Drops the data behind this proxy, so that any further read throws.
    /// </summary>
    /// <remarks>
    /// The per-type proxies stay in place and are pointed at the disabled accesses, so a caller still
    /// holding a record gets a clear exception rather than reading whatever replaced it.
    /// </remarks>
    public void DisableDataAccess()
    {
        _currentDataAccess = HollowDisabledDataAccess.Instance;

        foreach (HollowTypeProxyDataAccess proxy in _typeDataAccesses.Values)
        {
            proxy.SetCurrentDataAccess(proxy.DisabledDataAccess);
        }
    }

    /// <summary>Clears <see cref="WasRead"/>, starting a fresh usage detection window.</summary>
    public void ResetWasRead() => Volatile.Write(ref _wasRead, false);

    /// <inheritdoc />
    public IHollowTypeDataAccess? GetTypeDataAccess(string type) => _typeDataAccesses.GetValueOrDefault(type);

    /// <inheritdoc />
    public IHollowTypeDataAccess? GetTypeDataAccess(string type, int ordinal) =>
        _typeDataAccesses.GetValueOrDefault(type);

    /// <inheritdoc />
    public HollowSchema? GetSchema(string typeName) => _currentDataAccess.GetSchema(typeName);

    /// <inheritdoc />
    public HollowSchema GetNonNullSchema(string typeName) => _currentDataAccess.GetNonNullSchema(typeName);

    /// <inheritdoc />
    public bool HasType(string typeName) => _typeDataAccesses.ContainsKey(typeName);

    /// <summary>Notes that a record was read, for the stale reference detector.</summary>
    internal void NoteRead()
    {
        // Checked before writing: the flag is set once per detection window and read by a timer, so
        // the branch is cheaper than repeatedly dirtying a cache line shared across reader threads.
        if (!_wasRead)
        {
            _wasRead = true;
        }
    }

    private HollowTypeProxyDataAccess CreateProxy(IHollowTypeDataAccess typeDataAccess) =>
        typeDataAccess switch
        {
            IHollowObjectTypeDataAccess => new HollowObjectProxyDataAccess(this),
            IHollowListTypeDataAccess => new HollowListProxyDataAccess(this),
            IHollowSetTypeDataAccess => new HollowSetProxyDataAccess(this),
            IHollowMapTypeDataAccess => new HollowMapProxyDataAccess(this),
            _ => throw new ArgumentException(
                $"cannot proxy a {typeDataAccess.Schema.SchemaType} type", nameof(typeDataAccess)),
        };
}
