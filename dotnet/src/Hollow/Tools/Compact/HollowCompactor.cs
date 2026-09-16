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

using System.Globalization;
using Hollow.Core.Read.Engine;
using Hollow.Core.Schema;
using Hollow.Core.Tools.Patch.Delta;
using Hollow.Core.Tools.Traverse;
using Hollow.Core.Util;
using Hollow.Core.Write;
using Hollow.Core.Write.Copy;

namespace Hollow.Tools.Compact;

/// <summary>
/// Moves records off the high end of a type's ordinal space into the holes a long delta chain has
/// left behind, so that the space can be reclaimed.
/// </summary>
/// <remarks>
/// <para>
/// A record keeps its ordinal for as long as it exists, so a dataset that churns leaves gaps: a type
/// whose highest ordinal is 100 but which holds twenty records still costs a consumer the full
/// hundred slots. Compaction closes the gaps by producing a delta that consists of nothing but
/// removals and re-additions of identical records at lower ordinals.
/// </para>
/// <para>
/// It takes more than one delta to finish, because relocating one type's records changes the
/// ordinals the referencing types point at, which churns them in turn. A single cycle therefore only
/// compacts types that do not reference one another, directly or transitively.
/// </para>
/// <para>
/// By default a cycle relocates every misplaced record of every targeted type, which can produce a
/// very large delta. Setting <see cref="CompactionConfig.ApproximateDeltaBytesPerCycle"/> bounds it:
/// a cycle then compacts only the single type with the biggest hole footprint, and only as many of
/// its records as the budget affords once the referencing closure is counted too.
/// </para>
/// <para>
/// An instance plans its work from the read state once and must not outlive that state: construct a
/// new compactor per cycle.
/// </para>
/// </remarks>
public sealed class HollowCompactor
{
    /// <summary>
    /// Whether a relocated set or map element keeps the bucket it occupied.
    /// </summary>
    /// <remarks>
    /// Java asks the read engine which types have a defined hash code, which only answers yes when a
    /// <c>HollowObjectHashCodeFinder</c> has been installed. This port does not carry that hook, so
    /// the answer is always no and the question collapses to this constant.
    /// </remarks>
    private const bool PreserveHashPositions = false;

    private readonly HollowWriteStateEngine _writeEngine;
    private readonly HollowReadStateEngine _readEngine;
    private readonly CompactionConfig _config;
    private readonly Dictionary<string, string> _skippedTypes = new(StringComparer.Ordinal);

    private Dictionary<string, int>? _compactionPlan;

    /// <summary>
    /// Prepares a compaction of <paramref name="writeEngine"/>, planned from the read state that
    /// holds the same data.
    /// </summary>
    /// <param name="writeEngine">The write state to compact.</param>
    /// <param name="readEngine">A read state at the same data state as the write state.</param>
    /// <param name="config">What makes a type worth compacting.</param>
    public HollowCompactor(
        HollowWriteStateEngine writeEngine, HollowReadStateEngine readEngine, CompactionConfig config)
    {
        ArgumentNullException.ThrowIfNull(writeEngine);
        ArgumentNullException.ThrowIfNull(readEngine);
        ArgumentNullException.ThrowIfNull(config);

        _writeEngine = writeEngine;
        _readEngine = readEngine;
        _config = config;
    }

    /// <summary>
    /// The types left out of this cycle's plan, mapped to why.
    /// </summary>
    /// <remarks>
    /// A budget too small to afford even one record together with its referencing closure means that
    /// type can never be compacted, however many cycles run. Java writes that to a log, where a
    /// producer that wanted to act on it would have to go looking; here it is part of the answer.
    /// </remarks>
    public IReadOnlyDictionary<string, string> SkippedTypes
    {
        get
        {
            CalculateCompactionPlan();

            return _skippedTypes;
        }
    }

    /// <summary>Whether any type meets the configured criteria for compaction.</summary>
    public bool NeedsCompaction() => CalculateCompactionPlan().Count > 0;

    /// <summary>
    /// Relocates this cycle's planned records, leaving the write state ready to produce the delta.
    /// </summary>
    /// <remarks>
    /// The write state must be untouched since its last <c>PrepareForNextCycle</c>, and the read
    /// state must reflect the same data, or the plan names ordinals that mean something else.
    /// </remarks>
    public void Compact()
    {
        Dictionary<string, int> plan = CalculateCompactionPlan();

        Dictionary<string, BitSet> relocatedOrdinals = new(StringComparer.Ordinal);
        PartialOrdinalRemapper remapper = new();

        foreach ((string target, int relocations) in plan)
        {
            relocatedOrdinals[target] = RelocateRecords(target, relocations, remapper);
        }

        // Everything that pointed at a relocated record has to be rewritten to point at where it went.
        TransitiveSetTraverser.AddReferencingOutsideClosure(_readEngine, relocatedOrdinals);

        // In dependency order, so that a referencing type is rewritten after what it references has
        // been remapped.
        foreach (HollowSchema schema in HollowSchemaSorter.DependencyOrderedSchemaList(_writeEngine.Schemas))
        {
            if (plan.ContainsKey(schema.Name))
            {
                continue;
            }

            HollowTypeWriteState writeState = _writeEngine.GetTypeState(schema.Name)!;

            writeState.AddAllObjectsFromPreviousCycle();

            if (relocatedOrdinals.TryGetValue(schema.Name, out BitSet? affected)
                && affected.Cardinality() > 0)
            {
                RewriteReferencingRecords(schema, writeState, affected, remapper);
            }
        }
    }

    /// <summary>
    /// Moves the <paramref name="relocations"/> highest records of one type into its lowest holes,
    /// returning the ordinals they vacated.
    /// </summary>
    private BitSet RelocateRecords(string target, int relocations, PartialOrdinalRemapper remapper)
    {
        HollowTypeReadState typeState = _readEngine.GetTypeState(target)!;
        HollowTypeWriteState writeState = _writeEngine.GetTypeState(target)!;

        BitSet populated = PopulatedOrdinals(target);
        BitSet vacated = new(populated.Length);

        writeState.AddAllObjectsFromPreviousCycle();

        HollowRecordCopier copier = HollowRecordCopier.Create(typeState);
        IntMap remapped = new(relocations);

        int ordinalToRelocate = populated.Length;
        int relocatePosition = -1;

        try
        {
            for (int i = 0; i < relocations; i++)
            {
                do
                {
                    ordinalToRelocate--;
                }
                while (!populated.Get(ordinalToRelocate));

                relocatePosition = populated.NextClearBit(relocatePosition + 1);

                vacated.Set(ordinalToRelocate);
                writeState.RemoveOrdinalFromThisCycle(ordinalToRelocate);
                writeState.MapOrdinal(
                    copier.Copy(ordinalToRelocate),
                    relocatePosition,
                    markPreviousCycle: false,
                    markCurrentCycle: true);

                remapped.Put(ordinalToRelocate, relocatePosition);
            }
        }
        finally
        {
            // The free list is now wrong either way, and leaving it wrong after a failure would
            // corrupt the next cycle rather than just this one.
            writeState.RecalculateFreeOrdinals();
        }

        remapper.AddOrdinalRemapping(target, remapped);

        return vacated;
    }

    /// <summary>
    /// Re-adds the records of one type whose references moved, and records where they themselves went.
    /// </summary>
    private void RewriteReferencingRecords(
        HollowSchema schema,
        HollowTypeWriteState writeState,
        BitSet affected,
        PartialOrdinalRemapper remapper)
    {
        HollowTypeReadState readState = _readEngine.GetTypeState(schema.Name)!;
        IntMap remapped = new(affected.Cardinality());

        HollowRecordCopier copier =
            HollowRecordCopier.Create(readState, schema, remapper, PreserveHashPositions);

        foreach (int ordinal in affected.EnumerateSetBits())
        {
            remapped.Put(ordinal, writeState.Add(copier.Copy(ordinal)));
            writeState.RemoveOrdinalFromThisCycle(ordinal);
        }

        remapper.AddOrdinalRemapping(schema.Name, remapped);
    }

    /// <summary>
    /// The types to compact this cycle, mapped to how many of their records to relocate.
    /// </summary>
    private Dictionary<string, int> CalculateCompactionPlan()
    {
        if (_compactionPlan is not null)
        {
            return _compactionPlan;
        }

        IReadOnlySet<string> targets = FindCompactionTargets();

        if (_config.IsBudgeted)
        {
            targets = CostliestType(targets);
        }

        Dictionary<string, int> plan = new(StringComparer.Ordinal);

        foreach (string target in targets)
        {
            BitSet populated = PopulatedOrdinals(target);
            int relocations = MisplacedOrdinalCount(populated);

            if (_config.IsBudgeted)
            {
                relocations = RelocationsWithinBudget(target, populated, relocations);
            }

            if (relocations > 0)
            {
                plan[target] = relocations;
            }
        }

        _compactionPlan = plan;

        return _compactionPlan;
    }

    private BitSet PopulatedOrdinals(string type) =>
        _readEngine.GetTypeState(type)!.GetListener<PopulatedOrdinalListener>()!.PopulatedOrdinals;

    /// <summary>The populated ordinals sitting at or above the record count.</summary>
    /// <remarks>
    /// Every one of them could be housed below that watermark, because there are exactly as many
    /// slots below it as there are records.
    /// </remarks>
    private static int MisplacedOrdinalCount(BitSet populated)
    {
        int count = 0;

        for (int ordinal = populated.NextSetBit(populated.Cardinality());
             ordinal != -1;
             ordinal = populated.NextSetBit(ordinal + 1))
        {
            count++;
        }

        return count;
    }

    /// <summary>The highest <paramref name="count"/> populated ordinals.</summary>
    private static BitSet HighestPopulated(BitSet populated, int count)
    {
        BitSet highest = new(populated.Length);
        int ordinal = populated.Length;

        for (int i = 0; i < count; i++)
        {
            do
            {
                ordinal--;
            }
            while (!populated.Get(ordinal));

            highest.Set(ordinal);
        }

        return highest;
    }

    /// <summary>
    /// The candidate types, no two of which reference one another directly or transitively.
    /// </summary>
    private HashSet<string> FindCompactionTargets()
    {
        HashSet<string> targets = new(StringComparer.Ordinal);

        foreach (HollowSchema schema in HollowSchemaSorter.DependencyOrderedSchemaList(_readEngine))
        {
            if (IsCompactionCandidate(schema.Name) && !DependsOnAny(schema.Name, targets))
            {
                targets.Add(schema.Name);
            }
        }

        return targets;
    }

    /// <summary>Narrows the targets to the one type whose holes cost the most.</summary>
    private IReadOnlySet<string> CostliestType(IReadOnlySet<string> targets)
    {
        string? costliest = null;
        long costliestHoleBytes = -1;

        foreach (string type in targets)
        {
            long holeBytes = _readEngine.GetTypeState(type)!.ApproxHoleCostInBytes;

            if (holeBytes > costliestHoleBytes)
            {
                costliestHoleBytes = holeBytes;
                costliest = type;
            }
        }

        return costliest is null ? targets : new HashSet<string>(StringComparer.Ordinal) { costliest };
    }

    /// <summary>
    /// How many of a type's highest records the delta budget affords, of the
    /// <paramref name="relocations"/> that want relocating.
    /// </summary>
    private int RelocationsWithinBudget(string target, BitSet populated, int relocations)
    {
        long budget = _config.ApproximateDeltaBytesPerCycle;
        long deltaBytes = ApproximateDeltaBytes(target, HighestPopulated(populated, relocations));

        if (deltaBytes <= budget)
        {
            return relocations;
        }

        // A record costs the delta its own bytes plus those of everything referencing it, so the
        // per-record cost is uneven and scaling the estimate down is no evidence that even one fits.
        long singleRecordBytes = ApproximateDeltaBytes(target, HighestPopulated(populated, 1));

        if (singleRecordBytes > budget)
        {
            _skippedTypes[target] = string.Create(
                CultureInfo.InvariantCulture,
                $"Relocating a single {target} record is estimated to add {singleRecordBytes} bytes to the delta, over the configured budget of {budget}. Raise the budget to at least {singleRecordBytes} bytes to reclaim this type's ordinal holes.");

            return 0;
        }

        return (int)Math.Max(1, relocations * budget / deltaBytes);
    }

    /// <summary>
    /// What relocating the given records would add to the delta, counting the records that reference
    /// them and would have to be rewritten too.
    /// </summary>
    private long ApproximateDeltaBytes(string target, BitSet candidates)
    {
        Dictionary<string, BitSet> churned = new(StringComparer.Ordinal) { [target] = candidates };

        TransitiveSetTraverser.AddReferencingOutsideClosure(_readEngine, churned);

        long deltaBytes = 0;

        foreach ((string type, BitSet ordinals) in churned)
        {
            deltaBytes += (long)ordinals.Cardinality() * ApproximateRecordSize(type);
        }

        return deltaBytes;
    }

    private long ApproximateRecordSize(string type)
    {
        HollowTypeReadState typeState = _readEngine.GetTypeState(type)!;
        int records = typeState.PopulatedOrdinals.Cardinality();

        return records == 0 ? 0 : Math.Max(1, typeState.ApproxHeapFootprintInBytes / records);
    }

    private bool IsCompactionCandidate(string typeName)
    {
        HollowTypeReadState typeState = _readEngine.GetTypeState(typeName)!;
        BitSet populated = typeState.PopulatedOrdinals;

        if (populated.Length == 0)
        {
            // Java divides by zero here and lets the resulting NaN comparison come out false. An
            // empty type has no holes; say so rather than arriving at it by accident.
            return false;
        }

        double holePercentage =
            (populated.Length - populated.Cardinality()) / (double)populated.Length * 100d;

        return holePercentage > _config.MinCandidateHolePercentage
            && typeState.ApproxHoleCostInBytes > _config.MinCandidateHoleCostInBytes;
    }

    private bool DependsOnAny(string type, IEnumerable<string> targets) =>
        targets.Any(target => HollowSchemaSorter.TypeIsTransitivelyDependent(_readEngine, type, target));
}

/// <summary>
/// What makes a type worth compacting, and how much delta a compaction cycle may cost.
/// </summary>
/// <remarks>
/// Java nests this inside the compactor. A configuration that a producer holds and hands over per
/// cycle is not part of the compactor's own vocabulary, so here it stands on its own.
/// </remarks>
public sealed record CompactionConfig
{
    private readonly long _approximateDeltaBytesPerCycle = long.MaxValue;

    /// <summary>
    /// A type is a candidate only when its holes cost more than this many bytes.
    /// </summary>
    public required long MinCandidateHoleCostInBytes { get; init; }

    /// <summary>
    /// A type is a candidate only when holes take up more than this percentage of its ordinal space.
    /// </summary>
    public required int MinCandidateHolePercentage { get; init; }

    /// <summary>
    /// Roughly how many bytes of delta a compaction cycle may produce.
    /// </summary>
    /// <remarks>
    /// Left unset, a cycle relocates every misplaced record of every targeted type at once. Set, a
    /// cycle compacts at most one type, and only as far as the budget reaches — so the dataset
    /// converges over several cycles rather than in one very large delta.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The budget is less than one byte.</exception>
    public long ApproximateDeltaBytesPerCycle
    {
        get => _approximateDeltaBytesPerCycle;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);

            _approximateDeltaBytesPerCycle = value;
        }
    }

    /// <summary>Whether a budget was set at all.</summary>
    internal bool IsBudgeted => _approximateDeltaBytesPerCycle != long.MaxValue;
}
