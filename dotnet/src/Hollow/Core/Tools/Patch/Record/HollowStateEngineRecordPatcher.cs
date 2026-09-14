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

using Hollow.Core.Index.Traversal;
using Hollow.Core.Read.Engine;
using Hollow.Core.Tools.Combine;
using Hollow.Core.Tools.Traverse;
using Hollow.Core.Util;
using Hollow.Core.Write;

namespace Hollow.Core.Tools.Patch.Record;

/// <summary>
/// Which records a patch is about: a type, the field paths that identify a record, and the values to
/// match on.
/// </summary>
/// <remarks>
/// The paths are traversal paths rather than a primary key, so they may reach through references and
/// through collections — a spec can say "every film whose cast includes this actor" as readily as
/// "the film with this id".
/// </remarks>
public sealed class TypeMatchSpec(string typeName, params string[] keyPaths)
{
    private readonly List<object?[]> _keyMatchingValues = [];

    /// <summary>The type whose records are matched.</summary>
    public string TypeName { get; } = typeName;

    /// <summary>The field paths that identify a record.</summary>
    public IReadOnlyList<string> KeyPaths { get; } = keyPaths;

    /// <summary>The values to match, one set of them per record wanted.</summary>
    public IReadOnlyList<object?[]> KeyMatchingValues => _keyMatchingValues;

    /// <summary>
    /// Matches the record whose <see cref="KeyPaths"/> hold <paramref name="matchValues"/>.
    /// </summary>
    public void AddMatchingValue(params object?[] matchValues) => _keyMatchingValues.Add(matchValues);
}

/// <summary>
/// Replaces some records of one state with the same records from another.
/// </summary>
/// <remarks>
/// <para>
/// What comes out is a whole new state: the base with the matched records, and everything only they
/// referenced, taken out, and the matching records from the other state put in their place. There is
/// no in-place edit of a Hollow state, so a patch is a combine with a director that says which side
/// each record comes from.
/// </para>
/// <para>
/// Named <c>HollowStateEngineRecordPatcher</c> in Java.
/// </para>
/// </remarks>
public sealed class HollowStateEngineRecordPatcher
{
    private readonly HollowReadStateEngine _base;
    private readonly HollowReadStateEngine _patchFrom;
    private readonly List<TypeMatchSpec> _matchSpecs = [];

    private string[] _ignoredTypes = [];

    /// <summary>
    /// Patches <paramref name="baseState"/> with records taken from <paramref name="patchFrom"/>.
    /// </summary>
    public HollowStateEngineRecordPatcher(HollowReadStateEngine baseState, HollowReadStateEngine patchFrom)
    {
        ArgumentNullException.ThrowIfNull(baseState);
        ArgumentNullException.ThrowIfNull(patchFrom);

        _base = baseState;
        _patchFrom = patchFrom;
    }

    /// <summary>Adds a set of records to replace.</summary>
    public void AddTypeMatchSpec(TypeMatchSpec matchSpec)
    {
        ArgumentNullException.ThrowIfNull(matchSpec);

        _matchSpecs.Add(matchSpec);
    }

    /// <summary>Says not to copy these types at all.</summary>
    public void SetIgnoredTypes(params string[] ignoredTypes)
    {
        ArgumentNullException.ThrowIfNull(ignoredTypes);

        _ignoredTypes = ignoredTypes;
    }

    /// <summary>
    /// Produces the patched state.
    /// </summary>
    /// <remarks>
    /// The two traversals on the base side are what makes this a replacement rather than an addition.
    /// The first grows the matched set to everything those records reference; the second takes back
    /// out whatever something <em>outside</em> the set still references, because that is shared data
    /// rather than part of the records being replaced.
    /// </remarks>
    public HollowWriteStateEngine Patch()
    {
        Dictionary<string, BitSet> baseMatches = FindMatches(_base);

        TransitiveSetTraverser.AddTransitiveMatches(_base, baseMatches);
        TransitiveSetTraverser.RemoveReferencedOutsideClosure(_base, baseMatches);

        Dictionary<string, BitSet> patchFromMatches = FindMatches(_patchFrom);

        HollowCombiner combiner = new(
            new HollowPatcherCombinerCopyDirector(_base, baseMatches, _patchFrom, patchFromMatches),
            _base,
            _patchFrom);

        combiner.AddIgnoredTypes(_ignoredTypes);
        combiner.Combine();

        return combiner.Output;
    }

    /// <summary>
    /// The ordinals of <paramref name="stateEngine"/> that any spec matches.
    /// </summary>
    /// <remarks>
    /// A type the state does not have simply matches nothing. Java reads its maximum ordinal before
    /// checking whether it is there at all, and fails with a null reference.
    /// </remarks>
    private Dictionary<string, BitSet> FindMatches(HollowReadStateEngine stateEngine)
    {
        Dictionary<string, BitSet> matches = new(StringComparer.Ordinal);

        foreach (TypeMatchSpec spec in _matchSpecs)
        {
            if (stateEngine.GetTypeState(spec.TypeName) is not { } typeState)
            {
                continue;
            }

            if (!matches.TryGetValue(spec.TypeName, out BitSet? foundMatches))
            {
                foundMatches = new BitSet(Math.Max(typeState.MaxOrdinal + 1, 0));
                matches[spec.TypeName] = foundMatches;
            }

            HollowIndexerValueTraverser traverser =
                new(stateEngine, spec.TypeName, [.. spec.KeyPaths]);

            foreach (int ordinal in typeState.PopulatedOrdinals.EnumerateSetBits())
            {
                traverser.Traverse(ordinal);

                if (AnyMatch(traverser, spec))
                {
                    foundMatches.Set(ordinal);
                }
            }
        }

        return matches;
    }

    /// <summary>
    /// Whether any of the values the traverser reached is one the spec asked for.
    /// </summary>
    /// <remarks>
    /// A record reaches several sets of values when a path runs through a collection, and matching
    /// any one of them matches the record.
    /// </remarks>
    private static bool AnyMatch(HollowIndexerValueTraverser traverser, TypeMatchSpec spec)
    {
        for (int match = 0; match < traverser.MatchCount; match++)
        {
            foreach (object?[] wanted in spec.KeyMatchingValues)
            {
                bool matched = true;

                for (int field = 0; field < traverser.FieldPathCount; field++)
                {
                    if (!traverser.IsMatchedValueEqual(match, field, wanted[field]))
                    {
                        matched = false;

                        break;
                    }
                }

                if (matched)
                {
                    return true;
                }
            }
        }

        return false;
    }
}

/// <summary>
/// Takes the unmatched records from the base and the matched ones from the patch source.
/// </summary>
/// <remarks>
/// The two answers are opposites on purpose. On the base side everything is copied <em>except</em>
/// the matched closure; on the patch side <em>only</em> the matches are. A type neither side said
/// anything about is copied from the base and skipped from the patch, so nothing arrives twice.
/// </remarks>
public sealed class HollowPatcherCombinerCopyDirector(
    HollowReadStateEngine baseState,
    IReadOnlyDictionary<string, BitSet> baseMatchesClosure,
    HollowReadStateEngine patchFrom,
    IReadOnlyDictionary<string, BitSet> patchFromMatchesClosure) : IHollowCombinerCopyDirector
{
    /// <inheritdoc />
    public bool ShouldCopy(HollowTypeReadState typeState, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(typeState);

        if (ReferenceEquals(typeState.StateEngine, baseState))
        {
            return baseMatchesClosure.GetValueOrDefault(typeState.Schema.Name) is not { } matched
                || !matched.Get(ordinal);
        }

        if (ReferenceEquals(typeState.StateEngine, patchFrom))
        {
            return patchFromMatchesClosure.GetValueOrDefault(typeState.Schema.Name) is { } matched
                && matched.Get(ordinal);
        }

        // A state that is neither of the two this director was built for.
        return false;
    }
}
