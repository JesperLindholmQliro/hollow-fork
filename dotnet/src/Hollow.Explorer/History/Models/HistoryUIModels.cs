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

using Hollow.Core;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Tools.History;
using Hollow.Core.Tools.History.KeyIndex;
using Hollow.Core.Util;
using Hollow.Explorer.History.Naming;

namespace Hollow.Explorer.History.Models;

/// <summary>
/// How many records of a type — or of a whole state — were added, removed and modified.
/// </summary>
/// <remarks>Java nests this inside <c>HistoryOverviewPage</c> as <c>ChangeBreakdown</c>.</remarks>
public sealed class ChangeBreakdown
{
    /// <summary>An empty breakdown, to be added to.</summary>
    public ChangeBreakdown()
    {
    }

    /// <summary>The breakdown of one type's changes.</summary>
    public ChangeBreakdown(HollowHistoricalStateTypeKeyOrdinalMapping keyMapping) => Add(keyMapping);

    /// <summary>How many records changed.</summary>
    public int ModifiedRecords { get; private set; }

    /// <summary>How many records arrived.</summary>
    public int AddedRecords { get; private set; }

    /// <summary>How many records went.</summary>
    public int RemovedRecords { get; private set; }

    /// <summary>How many records changed in any way.</summary>
    public int Total => ModifiedRecords + AddedRecords + RemovedRecords;

    /// <summary>Folds one type's changes into this breakdown.</summary>
    public void Add(HollowHistoricalStateTypeKeyOrdinalMapping keyMapping)
    {
        ArgumentNullException.ThrowIfNull(keyMapping);

        ModifiedRecords += keyMapping.NumberOfModifiedRecords;
        AddedRecords += keyMapping.NumberOfNewRecords;
        RemovedRecords += keyMapping.NumberOfRemovedRecords;
    }
}

/// <summary>One version's line on the overview page.</summary>
/// <param name="DateDisplayString">The version read as a moment, where it reads as one.</param>
/// <param name="Version">The version itself.</param>
/// <param name="TopLevelChanges">The whole state's changes.</param>
/// <param name="TopLevelChangesByType">The same, broken down by type.</param>
/// <param name="OverviewDisplayHeaderValues">
/// The header tags the UI was asked to show, in the order it was asked for them.
/// </param>
/// <param name="ReshardingInvocationHeader">
/// The resharding tag the producer left, when this transition resharded a type.
/// </param>
public sealed record HistoryOverviewRow(
    string DateDisplayString,
    long Version,
    ChangeBreakdown TopLevelChanges,
    IReadOnlyDictionary<string, ChangeBreakdown> TopLevelChangesByType,
    IReadOnlyList<string?> OverviewDisplayHeaderValues,
    string? ReshardingInvocationHeader = null);

/// <summary>One version in the list of versions a record changed in.</summary>
/// <param name="VersionId">The version.</param>
/// <param name="DateDisplayString">The version read as a moment, where it reads as one.</param>
public sealed record HistoricalObjectChangeVersion(long VersionId, string DateDisplayString);

/// <summary>
/// One record as it changed across one transition.
/// </summary>
/// <remarks>
/// <para>
/// Either ordinal may be <see cref="HollowConstants.OrdinalNone"/>: no <c>from</c> means the record
/// arrived, no <c>to</c> means it went, and both present means it changed.
/// </para>
/// <para>Named <c>RecordDiff</c> in Java.</para>
/// </remarks>
public sealed class RecordDiff : IComparable<RecordDiff>
{
    private readonly HollowHistoricalState _historicalState;
    private readonly HollowHistoryRecordNamer _recordNamer;
    private readonly HollowHistoricalStateTypeKeyOrdinalMapping _typeKeyOrdinalMapping;
    private readonly IHollowObjectTypeDataAccess? _typeDataAccess;

    /// <summary>
    /// Describes the record at <paramref name="keyOrdinal"/> across one transition.
    /// </summary>
    public RecordDiff(
        HollowHistoricalState historicalState,
        HollowHistoryRecordNamer recordNamer,
        HollowHistoricalStateTypeKeyOrdinalMapping typeKeyOrdinalMapping,
        IHollowObjectTypeDataAccess? typeDataAccess,
        int keyOrdinal,
        int fromOrdinal,
        int toOrdinal)
    {
        ArgumentNullException.ThrowIfNull(historicalState);
        ArgumentNullException.ThrowIfNull(recordNamer);
        ArgumentNullException.ThrowIfNull(typeKeyOrdinalMapping);

        _historicalState = historicalState;
        _recordNamer = recordNamer;
        _typeKeyOrdinalMapping = typeKeyOrdinalMapping;
        _typeDataAccess = typeDataAccess;

        KeyOrdinal = keyOrdinal;
        FromOrdinal = fromOrdinal;
        ToOrdinal = toOrdinal;
    }

    /// <summary>The record's key.</summary>
    public int KeyOrdinal { get; }

    /// <summary>Where the record sat before, or none if it arrived.</summary>
    public int FromOrdinal { get; }

    /// <summary>Where the record sits after, or none if it went.</summary>
    public int ToOrdinal { get; }

    /// <summary>What to call the record, as of whichever side of the transition still has it.</summary>
    public string IdentifierString => _recordNamer.GetRecordName(
        _historicalState,
        _typeKeyOrdinalMapping,
        KeyOrdinal,
        _typeDataAccess,
        ToOrdinal != HollowConstants.OrdinalNone ? ToOrdinal : FromOrdinal);

    /// <summary>
    /// Orders by name, descending.
    /// </summary>
    /// <remarks>
    /// Java compares the other record's name to this one's, which reverses the order. The pages are
    /// built around that, so it is kept.
    /// </remarks>
    public int CompareTo(RecordDiff? other) =>
        other is null ? 1 : string.CompareOrdinal(other.IdentifierString, IdentifierString);
}

/// <summary>
/// A group of changed records, and the groups beneath it.
/// </summary>
/// <remarks>
/// The state-type page groups its records by chosen key fields, so that a type keyed by country and id
/// can be read country by country rather than as one long list. A node is one such group.
/// </remarks>
public sealed class RecordDiffTreeNode
{
    private readonly HollowHistoricalState _historicalState;
    private readonly HollowHistoryRecordNamer _recordNamer;
    private readonly Dictionary<object, RecordDiffTreeNode> _childNodes = [];
    private readonly List<RecordDiff> _recordDiffs = [];

    /// <summary>
    /// Starts a group named <paramref name="groupName"/> under
    /// <paramref name="parentHierarchicalFieldName"/>.
    /// </summary>
    public RecordDiffTreeNode(
        string parentHierarchicalFieldName,
        object groupIdentifier,
        string groupName,
        HollowHistoricalState historicalState,
        HollowHistoryRecordNamer recordNamer)
    {
        ArgumentNullException.ThrowIfNull(historicalState);
        ArgumentNullException.ThrowIfNull(recordNamer);

        HierarchicalFieldName = $"{parentHierarchicalFieldName}.{groupIdentifier}";
        GroupName = groupName;

        _historicalState = historicalState;
        _recordNamer = recordNamer;
    }

    /// <summary>
    /// The path from the root to this group, which is how a page names the group it is expanding.
    /// </summary>
    public string HierarchicalFieldName { get; }

    /// <summary>What this group is called.</summary>
    public string GroupName { get; }

    /// <summary>The records in this group, not counting its subgroups'.</summary>
    public IReadOnlyList<RecordDiff> RecordDiffs => _recordDiffs;

    /// <summary>The groups beneath this one.</summary>
    public IReadOnlyCollection<RecordDiffTreeNode> SubGroups => _childNodes.Values;

    /// <summary>Whether there are any groups beneath this one.</summary>
    public bool HasSubGroups => _childNodes.Count > 0;

    /// <summary>Whether this group holds nothing at all.</summary>
    public bool IsEmpty => _recordDiffs.Count == 0 && _childNodes.Count == 0;

    /// <summary>How many records this group holds, counting its subgroups'.</summary>
    public int DiffCount => _recordDiffs.Count + _childNodes.Values.Sum(child => child.DiffCount);

    /// <summary>
    /// The subgroup for <paramref name="value"/>, starting one if there is none yet.
    /// </summary>
    public RecordDiffTreeNode GetChildNode(object value, int keyFieldIndex)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!_childNodes.TryGetValue(value, out RecordDiffTreeNode? child))
        {
            child = new RecordDiffTreeNode(
                HierarchicalFieldName,
                value,
                _recordNamer.GetKeyFieldName(_historicalState, value, keyFieldIndex),
                _historicalState,
                _recordNamer);

            _childNodes[value] = child;
        }

        return child;
    }

    /// <summary>Puts <paramref name="diff"/> in this group.</summary>
    public void AddRecordDiff(RecordDiff diff) => _recordDiffs.Add(diff);
}

/// <summary>
/// How many of one type's records changed in one state, without working out which.
/// </summary>
/// <remarks>
/// The state page lists every type this way, and only the type the reader opens has its records
/// gathered.
/// </remarks>
public sealed class HistoryStateTypeChangeSummary
{
    /// <summary>Summarises <paramref name="typeName"/>'s changes at <paramref name="stateVersion"/>.</summary>
    public HistoryStateTypeChangeSummary(
        long stateVersion, string typeName, HollowHistoricalStateTypeKeyOrdinalMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        Version = stateVersion;
        TypeName = typeName;
        Modifications = mapping.NumberOfModifiedRecords;
        Additions = mapping.NumberOfNewRecords;
        Removals = mapping.NumberOfRemovedRecords;
    }

    /// <summary>The state this summarises.</summary>
    public long Version { get; }

    /// <summary>The type this summarises.</summary>
    public string TypeName { get; }

    /// <summary>How many records changed.</summary>
    public int Modifications { get; }

    /// <summary>How many records arrived.</summary>
    public int Additions { get; }

    /// <summary>How many records went.</summary>
    public int Removals { get; }

    /// <summary>How many records changed in any way.</summary>
    public int TotalChanges => Modifications + Additions + Removals;

    /// <summary>Whether nothing of this type changed.</summary>
    public bool IsEmpty => TotalChanges == 0;
}

/// <summary>
/// Every record of one type that changed in one state, grouped by chosen key fields.
/// </summary>
/// <remarks>Named <c>HistoryStateTypeChanges</c> in Java.</remarks>
public sealed class HistoryStateTypeChanges
{
    /// <summary>
    /// Gathers <paramref name="typeName"/>'s changes in <paramref name="historicalState"/>.
    /// </summary>
    /// <param name="historicalState">The state to read.</param>
    /// <param name="typeName">The type to gather.</param>
    /// <param name="recordNamer">What to call each record and group.</param>
    /// <param name="groupedFieldNames">
    /// The key fields to group by, outermost first. A name that is not a key field is ignored.
    /// </param>
    public HistoryStateTypeChanges(
        HollowHistoricalState historicalState,
        string typeName,
        HollowHistoryRecordNamer recordNamer,
        params string[] groupedFieldNames)
    {
        ArgumentNullException.ThrowIfNull(historicalState);
        ArgumentNullException.ThrowIfNull(recordNamer);
        ArgumentNullException.ThrowIfNull(groupedFieldNames);

        StateVersion = historicalState.Version;
        TypeName = typeName;
        GroupedFieldNames = groupedFieldNames;

        ModifiedRecords = new RecordDiffTreeNode("", "Modified", "Modified", historicalState, recordNamer);
        AddedRecords = new RecordDiffTreeNode("", "Added", "Added", historicalState, recordNamer);
        RemovedRecords = new RecordDiffTreeNode("", "Removed", "Removed", historicalState, recordNamer);

        HollowHistoricalStateTypeKeyOrdinalMapping? typeKeyMapping =
            historicalState.KeyOrdinalMapping.GetTypeMapping(typeName);

        if (typeKeyMapping is null)
        {
            return;
        }

        IHollowObjectTypeDataAccess? dataAccess =
            historicalState.DataAccess.GetTypeDataAccess(typeName) as IHollowObjectTypeDataAccess;

        int[] groupedFieldIndexes =
            GetGroupedFieldIndexes(groupedFieldNames, typeKeyMapping.KeyIndex.KeyFields);

        // A key that was both removed and added is one record that changed; a key on only one side
        // arrived or went.
        foreach ((int keyOrdinal, int fromOrdinal) in typeKeyMapping.RemovedOrdinalMappings())
        {
            int toOrdinal = typeKeyMapping.FindAddedOrdinal(keyOrdinal);

            Add(
                toOrdinal != HollowConstants.OrdinalNone ? ModifiedRecords : RemovedRecords,
                historicalState,
                typeKeyMapping,
                recordNamer,
                dataAccess,
                keyOrdinal,
                fromOrdinal,
                toOrdinal,
                groupedFieldIndexes);
        }

        foreach ((int keyOrdinal, int toOrdinal) in typeKeyMapping.AddedOrdinalMappings())
        {
            if (typeKeyMapping.FindRemovedOrdinal(keyOrdinal) != HollowConstants.OrdinalNone)
            {
                // Already counted as a modification on the way through the removals.
                continue;
            }

            Add(
                AddedRecords,
                historicalState,
                typeKeyMapping,
                recordNamer,
                dataAccess,
                keyOrdinal,
                HollowConstants.OrdinalNone,
                toOrdinal,
                groupedFieldIndexes);
        }
    }

    /// <summary>The state these changes are of.</summary>
    public long StateVersion { get; }

    /// <summary>The type these changes are of.</summary>
    public string TypeName { get; }

    /// <summary>The key fields the records are grouped by.</summary>
    public IReadOnlyList<string> GroupedFieldNames { get; }

    /// <summary>The records that changed.</summary>
    public RecordDiffTreeNode ModifiedRecords { get; }

    /// <summary>The records that arrived.</summary>
    public RecordDiffTreeNode AddedRecords { get; }

    /// <summary>The records that went.</summary>
    public RecordDiffTreeNode RemovedRecords { get; }

    /// <summary>Whether nothing of this type changed.</summary>
    public bool IsEmpty => ModifiedRecords.IsEmpty && AddedRecords.IsEmpty && RemovedRecords.IsEmpty;

    /// <summary>
    /// The group at <paramref name="hierarchicalFieldName"/>, or <see langword="null"/> if there is
    /// none.
    /// </summary>
    public RecordDiffTreeNode? FindTreeNode(string hierarchicalFieldName) =>
        FindTreeNode(ModifiedRecords, hierarchicalFieldName)
        ?? FindTreeNode(AddedRecords, hierarchicalFieldName)
        ?? FindTreeNode(RemovedRecords, hierarchicalFieldName);

    private static RecordDiffTreeNode? FindTreeNode(RecordDiffTreeNode node, string hierarchicalFieldName)
    {
        if (string.Equals(node.HierarchicalFieldName, hierarchicalFieldName, StringComparison.Ordinal))
        {
            return node;
        }

        return node.SubGroups
            .Select(child => FindTreeNode(child, hierarchicalFieldName))
            .FirstOrDefault(found => found is not null);
    }

    private static void Add(
        RecordDiffTreeNode node,
        HollowHistoricalState historicalState,
        HollowHistoricalStateTypeKeyOrdinalMapping typeKeyMapping,
        HollowHistoryRecordNamer recordNamer,
        IHollowObjectTypeDataAccess? dataAccess,
        int keyOrdinal,
        int fromOrdinal,
        int toOrdinal,
        int[] groupedFieldIndexes)
    {
        foreach (int fieldIndex in groupedFieldIndexes)
        {
            node = node.GetChildNode(
                typeKeyMapping.KeyIndex.GetKeyFieldValue(fieldIndex, keyOrdinal), fieldIndex);
        }

        node.AddRecordDiff(new RecordDiff(
            historicalState, recordNamer, typeKeyMapping, dataAccess, keyOrdinal, fromOrdinal, toOrdinal));
    }

    /// <summary>
    /// The position of each named field within the key.
    /// </summary>
    /// <remarks>
    /// A name that is not a key field yields -1 in Java, which then reads field -1 and fails. Grouping
    /// by a field the key does not have is a caller's mistake rather than a crash, so it is dropped.
    /// </remarks>
    private static int[] GetGroupedFieldIndexes(string[] groupedFieldNames, IReadOnlyList<string> keyFields) =>
        [
            .. groupedFieldNames
                .Select(name => IndexOf(keyFields, name))
                .Where(index => index != -1),
        ];

    private static int IndexOf(IReadOnlyList<string> keyFields, string name)
    {
        for (int i = 0; i < keyFields.Count; i++)
        {
            if (string.Equals(keyFields[i], name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>
/// What a search turned up in one state.
/// </summary>
/// <remarks>Named <c>HistoryStateQueryMatches</c> in Java.</remarks>
public sealed class HistoryStateQueryMatches
{
    /// <summary>
    /// Gathers the records of <paramref name="perTypeQueryMatchingKeys"/> that changed in
    /// <paramref name="historicalState"/>.
    /// </summary>
    /// <param name="historicalState">The state to look in.</param>
    /// <param name="recordNamerFor">What to call a record of a given type.</param>
    /// <param name="dateDisplayString">The state's version read as a moment.</param>
    /// <param name="perTypeQueryMatchingKeys">The key ordinals the search matched, by type.</param>
    /// <remarks>
    /// Java takes the whole <c>HollowHistoryUI</c> here, to ask it for a type's record namer. Taking
    /// the question rather than the object keeps the models free of the pages.
    /// </remarks>
    public HistoryStateQueryMatches(
        HollowHistoricalState historicalState,
        Func<string, HollowHistoryRecordNamer> recordNamerFor,
        string dateDisplayString,
        IReadOnlyDictionary<string, IntList> perTypeQueryMatchingKeys)
    {
        ArgumentNullException.ThrowIfNull(historicalState);
        ArgumentNullException.ThrowIfNull(recordNamerFor);
        ArgumentNullException.ThrowIfNull(perTypeQueryMatchingKeys);

        StateVersion = historicalState.Version;
        DateDisplayString = dateDisplayString;

        TypeMatches =
        [
            .. perTypeQueryMatchingKeys
                .Select(entry => new TypeMatch(
                    historicalState, recordNamerFor(entry.Key), entry.Key, entry.Value))
                .Where(match => match.HasMatches),
        ];
    }

    /// <summary>The state searched.</summary>
    public long StateVersion { get; }

    /// <summary>The state's version read as a moment, where it reads as one.</summary>
    public string DateDisplayString { get; }

    /// <summary>What was found, by type.</summary>
    public IReadOnlyList<TypeMatch> TypeMatches { get; }

    /// <summary>Whether anything was found at all.</summary>
    public bool HasMatches => TypeMatches.Count > 0;

    /// <summary>
    /// What a search turned up of one type in one state.
    /// </summary>
    /// <remarks>Java nests this as <c>HistoryStateQueryMatches.TypeMatches</c>.</remarks>
    public sealed class TypeMatch
    {
        /// <summary>
        /// Sorts <paramref name="queryMatchingKeys"/> into what changed, arrived and went.
        /// </summary>
        public TypeMatch(
            HollowHistoricalState historicalState,
            HollowHistoryRecordNamer recordNamer,
            string type,
            IntList queryMatchingKeys)
        {
            ArgumentNullException.ThrowIfNull(historicalState);
            ArgumentNullException.ThrowIfNull(queryMatchingKeys);

            Type = type;

            List<RecordDiff> modified = [];
            List<RecordDiff> removed = [];
            List<RecordDiff> added = [];

            ModifiedRecords = modified;
            RemovedRecords = removed;
            AddedRecords = added;

            HollowHistoricalStateTypeKeyOrdinalMapping? typeKeyMapping =
                historicalState.KeyOrdinalMapping.GetTypeMapping(type);

            if (typeKeyMapping is null)
            {
                return;
            }

            IHollowObjectTypeDataAccess? typeDataAccess =
                historicalState.DataAccess.GetTypeDataAccess(type) as IHollowObjectTypeDataAccess;

            for (int i = 0; i < queryMatchingKeys.Count; i++)
            {
                int matchingKey = queryMatchingKeys.Get(i);
                int removedOrdinal = typeKeyMapping.FindRemovedOrdinal(matchingKey);
                int addedOrdinal = typeKeyMapping.FindAddedOrdinal(matchingKey);

                List<RecordDiff>? into = (removedOrdinal, addedOrdinal) switch
                {
                    (not HollowConstants.OrdinalNone, not HollowConstants.OrdinalNone) => modified,
                    (not HollowConstants.OrdinalNone, _) => removed,
                    (_, not HollowConstants.OrdinalNone) => added,

                    // The key exists, but this state did not touch that record.
                    _ => null,
                };

                into?.Add(new RecordDiff(
                    historicalState,
                    recordNamer,
                    typeKeyMapping,
                    typeDataAccess,
                    matchingKey,
                    removedOrdinal,
                    addedOrdinal));
            }
        }

        /// <summary>The type searched.</summary>
        public string Type { get; }

        /// <summary>The matching records that changed.</summary>
        public IReadOnlyList<RecordDiff> ModifiedRecords { get; }

        /// <summary>The matching records that arrived.</summary>
        public IReadOnlyList<RecordDiff> AddedRecords { get; }

        /// <summary>The matching records that went.</summary>
        public IReadOnlyList<RecordDiff> RemovedRecords { get; }

        /// <summary>Whether anything of this type was found.</summary>
        public bool HasMatches =>
            ModifiedRecords.Count > 0 || AddedRecords.Count > 0 || RemovedRecords.Count > 0;
    }
}
