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
using Hollow.Core.Read.Engine;
using Hollow.Core.Tools.Traverse;
using Hollow.Core.Util;

namespace Hollow.Core.Tools.Combine;

/// <summary>
/// Says which of an input's records a <see cref="HollowCombiner"/> should copy.
/// </summary>
/// <remarks>
/// Answering no only stops the record being copied <em>directly</em>. A record another copied record
/// references is still pulled across, because leaving it out would leave a reference pointing at
/// nothing.
/// <para>
/// Named <c>HollowCombinerCopyDirector</c> in Java; the leading I is the .NET convention for an
/// interface.
/// </para>
/// </remarks>
public interface IHollowCombinerCopyDirector
{
    /// <summary>Copies everything, which is what a combiner does when given no director.</summary>
    static IHollowCombinerCopyDirector Default { get; } = new CopyEverythingDirector();

    /// <summary>Whether to copy <paramref name="ordinal"/> of <paramref name="typeState"/>.</summary>
    bool ShouldCopy(HollowTypeReadState typeState, int ordinal);

    private sealed class CopyEverythingDirector : IHollowCombinerCopyDirector
    {
        public bool ShouldCopy(HollowTypeReadState typeState, int ordinal) => true;
    }
}

/// <summary>
/// Copies everything but the ordinals named, per type.
/// </summary>
/// <remarks>A type the map says nothing about is copied whole.</remarks>
public sealed class HollowCombinerExcludeOrdinalsCopyDirector(
    IReadOnlyDictionary<string, BitSet> excludedOrdinals) : IHollowCombinerCopyDirector
{
    /// <inheritdoc />
    public bool ShouldCopy(HollowTypeReadState typeState, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(typeState);

        return excludedOrdinals.GetValueOrDefault(typeState.Schema.Name) is not { } excluded
            || !excluded.Get(ordinal);
    }
}

/// <summary>
/// Copies only the ordinals named, per type.
/// </summary>
/// <remarks>The opposite default to the exclude director: a type the map says nothing about is skipped.</remarks>
public sealed class HollowCombinerIncludeOrdinalsCopyDirector(
    IReadOnlyDictionary<string, BitSet> includedOrdinals) : IHollowCombinerCopyDirector
{
    /// <inheritdoc />
    public bool ShouldCopy(HollowTypeReadState typeState, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(typeState);

        return includedOrdinals.GetValueOrDefault(typeState.Schema.Name) is { } included
            && included.Get(ordinal);
    }
}

/// <summary>
/// Copies everything but the records named by primary key.
/// </summary>
/// <remarks>
/// The usual way to direct a combine: a key is the only stable way to name a record across two states,
/// since ordinals mean nothing between them.
/// </remarks>
public sealed class HollowCombinerExcludePrimaryKeysCopyDirector : IHollowCombinerCopyDirector
{
    private readonly IHollowCombinerCopyDirector _baseDirector;
    private readonly Dictionary<HollowTypeReadState, BitSet> _excludedOrdinals = [];

    /// <summary>Excludes nothing else, so everything not named by a key is copied.</summary>
    public HollowCombinerExcludePrimaryKeysCopyDirector()
        : this(IHollowCombinerCopyDirector.Default)
    {
    }

    /// <summary>
    /// Falls back to <paramref name="baseDirector"/> for a record no excluded key names.
    /// </summary>
    public HollowCombinerExcludePrimaryKeysCopyDirector(IHollowCombinerCopyDirector baseDirector)
    {
        ArgumentNullException.ThrowIfNull(baseDirector);

        _baseDirector = baseDirector;
    }

    /// <summary>Excludes the record <paramref name="index"/> finds under <paramref name="key"/>.</summary>
    public void ExcludeKey(HollowPrimaryKeyIndex index, params object?[] key)
    {
        ArgumentNullException.ThrowIfNull(index);

        int excludeOrdinal = index.GetMatchingOrdinal(key);

        if (excludeOrdinal < 0)
        {
            return;
        }

        HollowTypeReadState typeState = index.TypeState!;

        if (!_excludedOrdinals.TryGetValue(typeState, out BitSet? excluded))
        {
            excluded = new BitSet(typeState.MaxOrdinal + 1);
            _excludedOrdinals[typeState] = excluded;
        }

        excluded.Set(excludeOrdinal);
    }

    /// <summary>
    /// Also excludes whatever the already-excluded records reference.
    /// </summary>
    public void ExcludeReferencedObjects()
    {
        // The state engines are collected before anything is added, because growing the closure adds
        // entries to the very dictionary being read.
        HashSet<HollowReadStateEngine> stateEngines =
            [.. _excludedOrdinals.Keys.Select(typeState => typeState.StateEngine)];

        foreach (HollowReadStateEngine stateEngine in stateEngines)
        {
            Dictionary<string, BitSet> matches = new(StringComparer.Ordinal);

            foreach ((HollowTypeReadState typeState, BitSet excluded) in _excludedOrdinals)
            {
                if (ReferenceEquals(typeState.StateEngine, stateEngine))
                {
                    matches[typeState.Schema.Name] = excluded.Clone();
                }
            }

            TransitiveSetTraverser.AddTransitiveMatches(stateEngine, matches);

            foreach ((string typeName, BitSet excluded) in matches)
            {
                _excludedOrdinals[stateEngine.GetTypeState(typeName)!] = excluded;
            }
        }
    }

    /// <inheritdoc />
    public bool ShouldCopy(HollowTypeReadState typeState, int ordinal) =>
        (!_excludedOrdinals.TryGetValue(typeState, out BitSet? excluded) || !excluded.Get(ordinal))
        && _baseDirector.ShouldCopy(typeState, ordinal);
}

/// <summary>
/// Copies only the records named by primary key.
/// </summary>
/// <remarks>
/// The exclude director inverted, exactly as in Java: the same keys are collected and the answer is
/// negated.
/// </remarks>
public sealed class HollowCombinerIncludePrimaryKeysCopyDirector : IHollowCombinerCopyDirector
{
    private readonly HollowCombinerExcludePrimaryKeysCopyDirector _inverse;

    /// <summary>Copies only the keyed records.</summary>
    public HollowCombinerIncludePrimaryKeysCopyDirector() =>
        _inverse = new HollowCombinerExcludePrimaryKeysCopyDirector();

    /// <summary>
    /// Copies only the keyed records, with <paramref name="baseDirector"/> deciding the rest — which,
    /// because the answer is inverted, means a record it would copy is one this director will not.
    /// </summary>
    public HollowCombinerIncludePrimaryKeysCopyDirector(IHollowCombinerCopyDirector baseDirector) =>
        _inverse = new HollowCombinerExcludePrimaryKeysCopyDirector(baseDirector);

    /// <summary>Includes the record <paramref name="index"/> finds under <paramref name="key"/>.</summary>
    public void IncludeKey(HollowPrimaryKeyIndex index, params object?[] key) =>
        _inverse.ExcludeKey(index, key);

    /// <inheritdoc />
    public bool ShouldCopy(HollowTypeReadState typeState, int ordinal) =>
        !_inverse.ShouldCopy(typeState, ordinal);
}
