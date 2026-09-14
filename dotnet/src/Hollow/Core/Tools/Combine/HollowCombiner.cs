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

using Hollow.Core.Index;
using Hollow.Core.Index.Key;
using Hollow.Core.Read.Engine;
using Hollow.Core.Schema;
using Hollow.Core.Util;
using Hollow.Core.Write;
using Hollow.Core.Write.Copy;

namespace Hollow.Core.Tools.Combine;

/// <summary>
/// Copies one or more read states into a single write state.
/// </summary>
/// <remarks>
/// <para>
/// The inputs' ordinals are unrelated to each other and to the output's, so every reference has to be
/// rewritten as the records are copied. That is what the per-input ordinal remappers do: a table per
/// type, filled in as records are written, and asking about an ordinal nothing has copied yet copies
/// it — which pulls each record's references across recursively.
/// </para>
/// <para>
/// Given primary keys, the same record arriving from two inputs is written once and the second input's
/// references are pointed at the first input's copy. Keys are worked through in dependency order, a
/// round per key, because deduplicating a type changes what the types referencing it look like.
/// </para>
/// <para>
/// Named <c>HollowCombiner</c> in Java, which copies on a <c>SimultaneousExecutor</c>. This port copies
/// on one thread, so Java's <c>ThreadLocal</c> of per-type copiers is a plain field here.
/// </para>
/// </remarks>
public sealed class HollowCombiner
{
    private readonly HollowReadStateEngine[] _inputs;
    private readonly IOrdinalRemapper[] _ordinalRemappers;
    private readonly IHollowCombinerCopyDirector _copyDirector;
    private readonly HashSet<string> _ignoredTypes = new(StringComparer.Ordinal);

    private Dictionary<string, HollowCombinerCopier> _copiersPerType = new(StringComparer.Ordinal);
    private List<PrimaryKey> _primaryKeys = [];

    /// <summary>Combines <paramref name="inputs"/> into a state built from the first one's schemas.</summary>
    public HollowCombiner(params HollowReadStateEngine[] inputs)
        : this(IHollowCombinerCopyDirector.Default, Validate(inputs), inputs)
    {
    }

    /// <summary>
    /// Combines <paramref name="inputs"/> as <paramref name="director"/> says, into a state built from
    /// the first input's schemas.
    /// </summary>
    public HollowCombiner(IHollowCombinerCopyDirector director, params HollowReadStateEngine[] inputs)
        : this(director, Validate(inputs), inputs)
    {
    }

    /// <summary>Combines <paramref name="inputs"/> into <paramref name="output"/>.</summary>
    public HollowCombiner(HollowWriteStateEngine output, params HollowReadStateEngine[] inputs)
        : this(IHollowCombinerCopyDirector.Default, output, inputs)
    {
    }

    /// <summary>
    /// Combines <paramref name="inputs"/> into <paramref name="output"/> as
    /// <paramref name="copyDirector"/> says.
    /// </summary>
    public HollowCombiner(
        IHollowCombinerCopyDirector copyDirector,
        HollowWriteStateEngine output,
        params HollowReadStateEngine[] inputs)
    {
        ArgumentNullException.ThrowIfNull(copyDirector);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentOutOfRangeException.ThrowIfZero(inputs.Length);

        _inputs = inputs;
        _copyDirector = copyDirector;
        _ordinalRemappers = new IOrdinalRemapper[inputs.Length];

        Output = output;

        InitializePrimaryKeys();
    }

    /// <summary>The state being combined into.</summary>
    /// <remarks>Named <c>getCombinedStateEngine()</c> in Java.</remarks>
    public HollowWriteStateEngine Output { get; }

    /// <summary>The keys records are deduplicated by, in dependency order.</summary>
    public IReadOnlyList<PrimaryKey> PrimaryKeys => _primaryKeys;

    /// <summary>
    /// Deduplicates records by these keys, on top of the keys the output's schemas already declare.
    /// </summary>
    /// <remarks>
    /// A record matching an already-copied record on any of the keys is not copied again, and every
    /// reference to it is pointed at the copy that was kept — which is the one from the input given
    /// first. With one input there is nothing to deduplicate, so this does nothing.
    /// </remarks>
    public void SetPrimaryKeys(params PrimaryKey[] newKeys)
    {
        ArgumentNullException.ThrowIfNull(newKeys);

        if (newKeys.Length == 0 || _inputs.Length == 1)
        {
            return;
        }

        // A key given here replaces the schema's key for the same type rather than joining it.
        Dictionary<string, PrimaryKey> keysByType = _primaryKeys.ToDictionary(
            key => key.Type, StringComparer.Ordinal);

        foreach (PrimaryKey key in newKeys)
        {
            keysByType[key.Type] = key;
        }

        _primaryKeys = SortPrimaryKeys([.. keysByType.Values]);
    }

    /// <summary>
    /// Says not to copy these types at all.
    /// </summary>
    /// <remarks>
    /// Nothing that is copied may reference an ignored type: the reference would be left pointing at
    /// a record that was never written.
    /// </remarks>
    public void AddIgnoredTypes(params string[] typeNames)
    {
        ArgumentNullException.ThrowIfNull(typeNames);

        foreach (string typeName in typeNames)
        {
            _ignoredTypes.Add(typeName);
        }
    }

    /// <summary>
    /// Copies the inputs into the output.
    /// </summary>
    /// <remarks>
    /// One round per group of keys that do not depend on each other. A round deduplicates the types
    /// those keys reach; the next round can then see the result, which is why a type referencing a
    /// keyed type cannot be copied in the same round as the key itself.
    /// </remarks>
    public void Combine()
    {
        CreateOrdinalRemappers();

        HashSet<string> processedTypes = new(StringComparer.Ordinal);
        HashSet<PrimaryKey> processedPrimaryKeys = [];
        HashSet<PrimaryKey> selectedPrimaryKeys = [];

        while (processedTypes.Count < Output.OrderedTypeStates.Count)
        {
            foreach (PrimaryKey key in _primaryKeys)
            {
                if (!processedPrimaryKeys.Contains(key)
                    && !_ignoredTypes.Contains(key.Type)
                    && !IsAnySelectedPrimaryKeyADependencyOf(key.Type, selectedPrimaryKeys))
                {
                    selectedPrimaryKeys.Add(key);
                }
            }

            HashSet<string> typesToProcess = new(StringComparer.Ordinal);
            Dictionary<string, HollowPrimaryKeyIndex?[]> primaryKeyIndexes = new(StringComparer.Ordinal);
            HollowCombinerExcludePrimaryKeysCopyDirector primaryKeyCopyDirector = new(_copyDirector);

            foreach (HollowSchema schema in Output.Schemas)
            {
                if (processedTypes.Contains(schema.Name) || _ignoredTypes.Contains(schema.Name))
                {
                    continue;
                }

                if (selectedPrimaryKeys.Count != 0
                    && !IsAnySelectedPrimaryKeyDependentOn(schema.Name, selectedPrimaryKeys))
                {
                    continue;
                }

                foreach (PrimaryKey key in selectedPrimaryKeys.Where(key => key.Type == schema.Name))
                {
                    primaryKeyIndexes[key.Type] = ExcludeLaterDuplicates(key, primaryKeyCopyDirector);
                }

                typesToProcess.Add(schema.Name);
            }

            if (typesToProcess.Count == 0)
            {
                break;
            }

            CopyRound(typesToProcess, processedTypes, primaryKeyIndexes, primaryKeyCopyDirector, selectedPrimaryKeys);

            processedTypes.UnionWith(typesToProcess);
            processedPrimaryKeys.UnionWith(selectedPrimaryKeys);
            selectedPrimaryKeys.Clear();
        }
    }

    /// <summary>
    /// Copies one record of one type, and says where it landed.
    /// </summary>
    /// <remarks>
    /// Reached from a remapper rather than called directly: remapping a reference copies the record it
    /// points at if nothing has copied it yet.
    /// </remarks>
    internal int CopyOrdinal(string typeName, int currentOrdinal) =>
        _copiersPerType.GetValueOrDefault(typeName) is { } copier
            ? copier.Copy(currentOrdinal)
            : currentOrdinal;

    private static HollowWriteStateEngine Validate(HollowReadStateEngine[] inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentOutOfRangeException.ThrowIfZero(inputs.Length);

        return HollowWriteStateCreator.CreateWithSchemas(inputs[0].Schemas);
    }

    /// <summary>
    /// Builds one key index per input and marks, for every record an earlier input holds, the matching
    /// record in each later input as one not to copy.
    /// </summary>
    private HollowPrimaryKeyIndex?[] ExcludeLaterDuplicates(
        PrimaryKey key, HollowCombinerExcludePrimaryKeysCopyDirector primaryKeyCopyDirector)
    {
        HollowPrimaryKeyIndex?[] indexes =
        [
            .. _inputs.Select(input =>
                input.GetTypeState(key.Type) is null ? null : new HollowPrimaryKeyIndex(input, key)),
        ];

        for (int i = 0; i < indexes.Length; i++)
        {
            if (_inputs[i].GetTypeState(key.Type) is not { } typeState)
            {
                continue;
            }

            foreach (int ordinal in typeState.PopulatedOrdinals.EnumerateSetBits())
            {
                if (!primaryKeyCopyDirector.ShouldCopy(typeState, ordinal))
                {
                    continue;
                }

                object?[] recordKey = indexes[i]!.GetRecordKey(ordinal);

                for (int j = i + 1; j < indexes.Length; j++)
                {
                    if (indexes[j] is { } laterIndex)
                    {
                        primaryKeyCopyDirector.ExcludeKey(laterIndex, recordKey);
                    }
                }
            }
        }

        return indexes;
    }

    /// <summary>
    /// Copies every input's records of the types this round covers.
    /// </summary>
    /// <remarks>
    /// The copiers for types already done are kept alongside the round's own, because a record copied
    /// this round may reference one of them and the remapper has to be able to reach its copier.
    /// </remarks>
    private void CopyRound(
        HashSet<string> typesToProcess,
        HashSet<string> processedTypes,
        Dictionary<string, HollowPrimaryKeyIndex?[]> primaryKeyIndexes,
        HollowCombinerExcludePrimaryKeysCopyDirector primaryKeyCopyDirector,
        HashSet<PrimaryKey> selectedPrimaryKeys)
    {
        bool deduplicating = selectedPrimaryKeys.Count != 0;

        for (int i = 0; i < _inputs.Length; i++)
        {
            IHollowCombinerCopyDirector copyDirector = deduplicating ? primaryKeyCopyDirector : _copyDirector;
            IOrdinalRemapper ordinalRemapper = deduplicating
                ? new HollowCombinerPrimaryKeyOrdinalRemapper(_ordinalRemappers, primaryKeyIndexes, i)
                : _ordinalRemappers[i];

            HollowReadStateEngine input = _inputs[i];
            Dictionary<string, HollowCombinerCopier> copierMap = new(StringComparer.Ordinal);
            List<HollowCombinerCopier> copierList = [];

            foreach (string typeName in typesToProcess)
            {
                if (Copier(input, typeName, ordinalRemapper) is { } copier)
                {
                    copierList.Add(copier);
                    copierMap[typeName] = copier;
                }
            }

            foreach (string typeName in processedTypes)
            {
                if (Copier(input, typeName, _ordinalRemappers[i]) is { } copier)
                {
                    copierMap[typeName] = copier;
                }
            }

            _copiersPerType = copierMap;

            // One pass across every type at once, ordinal by ordinal, rather than a pass per type:
            // a copier drops out as soon as its type runs out of ordinals.
            for (int ordinal = 0; copierList.Count != 0; ordinal++)
            {
                CopyOrdinalForAllStates(ordinal, copierList, copyDirector);
            }
        }
    }

    private HollowCombinerCopier? Copier(
        HollowReadStateEngine input, string typeName, IOrdinalRemapper ordinalRemapper) =>
        input.GetTypeState(typeName) is { } readState && Output.GetTypeState(typeName) is { } writeState
            ? new HollowCombinerCopier(readState, writeState, ordinalRemapper)
            : null;

    private static void CopyOrdinalForAllStates(
        int currentOrdinal, List<HollowCombinerCopier> copiers, IHollowCombinerCopyDirector copyDirector)
    {
        for (int i = copiers.Count - 1; i >= 0; i--)
        {
            HollowCombinerCopier copier = copiers[i];

            if (currentOrdinal > copier.ReadTypeState.MaxOrdinal)
            {
                copiers.RemoveAt(i);
            }
            else if (copyDirector.ShouldCopy(copier.ReadTypeState, currentOrdinal))
            {
                copier.Copy(currentOrdinal);
            }
        }
    }

    private void CreateOrdinalRemappers()
    {
        for (int i = 0; i < _ordinalRemappers.Length; i++)
        {
            _ordinalRemappers[i] = new HollowCombinerOrdinalRemapper(this, _inputs[i]);
        }
    }

    private void InitializePrimaryKeys()
    {
        if (_inputs.Length == 1)
        {
            return;
        }

        _primaryKeys = SortPrimaryKeys(
        [
            .. Output.Schemas
                .OfType<HollowObjectSchema>()
                .Where(schema => !_ignoredTypes.Contains(schema.Name))
                .Select(schema => schema.PrimaryKey)
                .OfType<PrimaryKey>(),
        ]);
    }

    /// <summary>
    /// Orders the keys so that a type is deduplicated before the types that reference it.
    /// </summary>
    private List<PrimaryKey> SortPrimaryKeys(List<PrimaryKey> primaryKeys)
    {
        IReadOnlyList<HollowSchema> dependencyOrdered =
            HollowSchemaSorter.DependencyOrderedSchemaList(Output.Schemas);

        int DependencyIndex(PrimaryKey key)
        {
            for (int i = 0; i < dependencyOrdered.Count; i++)
            {
                if (dependencyOrdered[i].Name == key.Type)
                {
                    return i;
                }
            }

            throw new ArgumentException($"a primary key names {key.Type}, which the output does not have");
        }

        primaryKeys.Sort((first, second) => DependencyIndex(first) - DependencyIndex(second));

        return primaryKeys;
    }

    private bool IsAnySelectedPrimaryKeyADependencyOf(string type, HashSet<PrimaryKey> selectedPrimaryKeys) =>
        selectedPrimaryKeys.Any(
            key => HollowSchemaSorter.TypeIsTransitivelyDependent(Output, type, key.Type));

    private bool IsAnySelectedPrimaryKeyDependentOn(string type, HashSet<PrimaryKey> selectedPrimaryKeys) =>
        selectedPrimaryKeys.Any(
            key => HollowSchemaSorter.TypeIsTransitivelyDependent(Output, key.Type, type));

    /// <summary>
    /// Copies one type's records from one input into the output.
    /// </summary>
    /// <remarks>
    /// Java also keeps a hash-order-independent ordinal map here, so that two sets differing only in
    /// bucket order are written once. That only ever applies with <c>HollowObjectHashCodeFinder</c>,
    /// the deprecated custom-hash-code mechanism this port does not have, so it is left out along with
    /// the hash positions it was there to preserve.
    /// </remarks>
    private sealed class HollowCombinerCopier
    {
        private readonly HollowRecordCopier _copier;
        private readonly BitSet _populatedOrdinals;
        private readonly HollowTypeWriteState _writeState;
        private readonly IOrdinalRemapper _ordinalRemapper;

        internal HollowCombinerCopier(
            HollowTypeReadState readState, HollowTypeWriteState writeState, IOrdinalRemapper ordinalRemapper)
        {
            _copier = HollowRecordCopier.Create(
                readState, writeState.Schema, ordinalRemapper, preserveHashPositions: false);
            _populatedOrdinals = readState.PopulatedOrdinals;
            _writeState = writeState;
            _ordinalRemapper = ordinalRemapper;
        }

        internal HollowTypeReadState ReadTypeState => _copier.ReadTypeState;

        private string Type => _copier.ReadTypeState.Schema.Name;

        internal int Copy(int ordinal)
        {
            if (!_populatedOrdinals.Get(ordinal))
            {
                return HollowConstants.OrdinalNone;
            }

            if (!_ordinalRemapper.OrdinalIsMapped(Type, ordinal))
            {
                int outputOrdinal = _writeState.Add(_copier.Copy(ordinal));

                _ordinalRemapper.RemapOrdinal(Type, ordinal, outputOrdinal);

                return outputOrdinal;
            }

            return _ordinalRemapper.GetMappedOrdinal(Type, ordinal);
        }
    }
}
