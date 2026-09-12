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
using Hollow.Api.Consumer;
using Hollow.Core.Read.Engine;

namespace Hollow.Explorer;

/// <summary>
/// The dataset the explorer is looking at, and what the pages need to know about it beyond the data
/// itself.
/// </summary>
/// <remarks>
/// <para>
/// Java's <c>HollowExplorerUI</c> is both this and the request router. Routing is the web framework's
/// job here, so what is left is the dataset, the header the embedder wants shown, and the cache of
/// per-type heap figures that the home page would otherwise recompute on every request.
/// </para>
/// <para>
/// Every member is safe to use from several requests at once.
/// </para>
/// </remarks>
public sealed class HollowExplorer
{
    private readonly HollowConsumer? _consumer;
    private readonly HollowReadStateEngine? _stateEngine;

    private readonly ConcurrentDictionary<string, string> _headerDisplayEntries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HeaderCell> _commonHeaderEntries = new(StringComparer.Ordinal);

    private readonly Lock _heapStatsLock = new();
    private volatile IReadOnlyDictionary<string, HeapStats>? _cachedHeapStats;
    private long _cachedRandomizedTag = -1;

    /// <summary>
    /// Looks at whatever <paramref name="consumer"/> currently holds, following it across updates.
    /// </summary>
    public HollowExplorer(HollowConsumer consumer) =>
        _consumer = consumer ?? throw new ArgumentNullException(nameof(consumer));

    /// <summary>
    /// Looks at <paramref name="stateEngine"/>, which whoever created it is responsible for updating.
    /// </summary>
    public HollowExplorer(HollowReadStateEngine stateEngine) =>
        _stateEngine = stateEngine ?? throw new ArgumentNullException(nameof(stateEngine));

    /// <summary>
    /// The state the next request will read, which changes under the explorer as the consumer updates.
    /// </summary>
    public HollowReadStateEngine StateEngine =>
        _consumer?.StateEngine
        ?? _stateEngine
        ?? throw new InvalidOperationException("the consumer has not loaded a state yet");

    /// <summary>
    /// The version the state being shown was published as, or <see langword="null"/> when the explorer
    /// was handed a state engine rather than a consumer and so has no version to name.
    /// </summary>
    public long? CurrentStateVersion => _consumer?.CurrentVersionId;

    /// <summary>
    /// A line the embedder wants in the page header — the dataset's name, say — with the address it
    /// links to.
    /// </summary>
    /// <remarks>
    /// Java holds the text and the address in one map under two different keys, so that the text is
    /// also the key its own address is stored under. Two properties say the same thing without the
    /// collision that arrangement invites.
    /// </remarks>
    public string? HeaderDisplayString { get; set; }

    /// <summary>Where <see cref="HeaderDisplayString"/> links to, if anywhere.</summary>
    public string? HeaderStringUrl { get; set; }

    /// <summary>
    /// Anything else the embedder wants to record about the dataset, shown nowhere but readable back.
    /// </summary>
    public IDictionary<string, string> HeaderDisplayEntries => _headerDisplayEntries;

    /// <summary>
    /// Extra cells for the navigation bar at the top of every page, keyed so they can be replaced.
    /// </summary>
    /// <remarks>
    /// The value is written into the page as HTML rather than as text, because the point of it is to
    /// let an embedder add a link. Only put markup here that you wrote.
    /// </remarks>
    public void AddCommonHeaderEntry(string key, string html, int position) =>
        _commonHeaderEntries[key] = new HeaderCell(html, position);

    /// <summary>Removes a cell added by <see cref="AddCommonHeaderEntry"/>.</summary>
    public void RemoveCommonHeaderEntry(string key) => _commonHeaderEntries.TryRemove(key, out _);

    /// <summary>The common header cells, in the order their positions ask for.</summary>
    public IEnumerable<string> CommonHeaderEntries =>
        _commonHeaderEntries.Values.OrderBy(cell => cell.Position).Select(cell => cell.Html);

    /// <summary>
    /// Works out each type's heap and hole figures now, so that the first request does not have to.
    /// </summary>
    /// <remarks>
    /// Walking every type's ordinals is slow enough to be worth doing once per state rather than once
    /// per request, which is all this cache is: it is keyed on the state's randomized tag, so a new
    /// state invalidates it by not matching.
    /// </remarks>
    public void PrefillHeapStatsCache()
    {
        HollowReadStateEngine engine = StateEngine;
        long currentTag = engine.RandomizedTag;

        if (_cachedHeapStats is not null && currentTag == Interlocked.Read(ref _cachedRandomizedTag))
        {
            return;
        }

        lock (_heapStatsLock)
        {
            // Re-read under the lock, in case another request filled the cache while this one waited.
            if (_cachedHeapStats is not null && currentTag == _cachedRandomizedTag)
            {
                return;
            }

            Dictionary<string, HeapStats> stats = new(engine.TypeStates.Count, StringComparer.Ordinal);

            foreach (HollowTypeReadState typeState in engine.TypeStates.Values)
            {
                stats[typeState.TypeName] =
                    new HeapStats(typeState.ApproxHeapFootprintInBytes, typeState.ApproxHoleCostInBytes);
            }

            _cachedHeapStats = stats;
            Interlocked.Exchange(ref _cachedRandomizedTag, currentTag);
        }
    }

    /// <summary>
    /// Throws away the cached heap figures, so that the next request works them out again.
    /// </summary>
    /// <remarks>
    /// A state the explorer moved to by delta keeps its randomized tag, so the cache does not notice it
    /// on its own. An embedder applying deltas should call this after each one.
    /// </remarks>
    public void ClearCache()
    {
        lock (_heapStatsLock)
        {
            _cachedHeapStats = null;
            _cachedRandomizedTag = -1;
        }
    }

    /// <summary>
    /// The heap and hole figures for <paramref name="typeState"/>, cached where the cache has them.
    /// </summary>
    internal HeapStats GetHeapStats(HollowTypeReadState typeState)
    {
        if (_cachedHeapStats?.TryGetValue(typeState.TypeName, out HeapStats cached) == true)
        {
            return cached;
        }

        return new HeapStats(typeState.ApproxHeapFootprintInBytes, typeState.ApproxHoleCostInBytes);
    }

    /// <summary>What a type costs, as of the state the cache was filled from.</summary>
    internal readonly record struct HeapStats(long ApproxHeapFootprint, long ApproxHoleFootprint);

    private readonly record struct HeaderCell(string Html, int Position);
}
