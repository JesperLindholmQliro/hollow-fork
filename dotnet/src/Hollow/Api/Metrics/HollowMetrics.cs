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

using Hollow.Core.Read.Engine;

namespace Hollow.Api.Metrics;

/// <summary>
/// What a producer or consumer has done, and what the dataset it is holding costs.
/// </summary>
/// <remarks>
/// <para>
/// The size half is the same on both sides: the heap each type occupies and how many records it
/// holds, recomputed whenever a new state arrives. The counting half is not, so the two subclasses
/// add their own.
/// </para>
/// <para>
/// Nothing here reports anywhere. Hollow's metrics are values a host application reads and forwards
/// to whatever it already uses; <see cref="HollowMetricsCollector{TMetrics}"/> is the hook for that.
/// </para>
/// </remarks>
public abstract class HollowMetrics
{
    private readonly Dictionary<string, long> _typeHeapFootprint = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _typePopulatedOrdinals = new(StringComparer.Ordinal);

    /// <summary>The version of the state currently held.</summary>
    public long CurrentVersion { get; set; }

    /// <summary>What every type costs in heap, by type name.</summary>
    /// <remarks>
    /// Java hands out its live <c>HashMap</c>, so a caller can corrupt the metrics by writing to what
    /// it was given. A read-only view costs nothing and cannot.
    /// </remarks>
    public IReadOnlyDictionary<string, long> TypeHeapFootprint => _typeHeapFootprint;

    /// <summary>How many records every type holds, by type name.</summary>
    public IReadOnlyDictionary<string, int> TypePopulatedOrdinals => _typePopulatedOrdinals;

    /// <summary>What the whole dataset costs in heap.</summary>
    public long TotalHeapFootprint { get; private set; }

    /// <summary>How many records the whole dataset holds.</summary>
    public int TotalPopulatedOrdinals { get; private set; }

    /// <summary>Records that a state of <paramref name="version"/> is now held.</summary>
    protected void Update(long version) => CurrentVersion = version;

    /// <summary>Records a new state and recomputes what it costs.</summary>
    protected void Update(HollowReadStateEngine stateEngine, long version)
    {
        Update(version);
        CalculateTypeMetrics(stateEngine);
    }

    /// <summary>
    /// Recomputes the heap footprint and record count of every type, and of the dataset.
    /// </summary>
    internal void CalculateTypeMetrics(HollowReadStateEngine stateEngine)
    {
        ArgumentNullException.ThrowIfNull(stateEngine);

        // Cleared rather than added to: a type dropped from the model between states would otherwise
        // keep reporting its last known cost forever. Java leaves the stale entry in place.
        _typeHeapFootprint.Clear();
        _typePopulatedOrdinals.Clear();

        TotalHeapFootprint = 0L;
        TotalPopulatedOrdinals = 0;

        foreach (HollowTypeReadState typeState in stateEngine.TypeStates.Values)
        {
            long heapCost = typeState.ApproxHeapFootprintInBytes;
            int populatedOrdinals = typeState.PopulatedOrdinals.Cardinality();

            TotalHeapFootprint += heapCost;
            TotalPopulatedOrdinals += populatedOrdinals;

            _typeHeapFootprint[typeState.Schema.Name] = heapCost;
            _typePopulatedOrdinals[typeState.Schema.Name] = populatedOrdinals;
        }
    }
}

/// <summary>
/// The hook a host application implements to forward Hollow's metrics to its own monitoring.
/// </summary>
/// <remarks>
/// <see cref="Collect"/> is called after each update, with the metrics as they now stand. Whatever it
/// does with them — a counter, a gauge, a log line — is the application's business; Hollow only says
/// when there is something new to report.
/// </remarks>
/// <typeparam name="TMetrics">The metrics this collects.</typeparam>
public abstract class HollowMetricsCollector<TMetrics>
    where TMetrics : HollowMetrics
{
    /// <summary>The metrics last collected, or <see langword="null"/> before the first update.</summary>
    public TMetrics? Metrics { get; set; }

    /// <summary>Called with the metrics as they now stand.</summary>
    public abstract void Collect(TMetrics metrics);
}
