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

using Hollow.Explorer.Diff.Effigy.Pairer;

namespace Hollow.Explorer.Diff;

/// <summary>
/// One line of a side-by-side record view, and the branch hanging beneath it.
/// </summary>
/// <remarks>
/// <para>
/// The rows form a tree mirroring the paired records, but the page draws them as a flat list — one
/// line per visible row, indented by depth. A row's children are built the first time they are asked
/// for, so opening one branch of a large record does not pair the rest of it.
/// </para>
/// <para>
/// The tree is stateful on purpose: which rows are visible is what the reader is manipulating by
/// opening and closing branches, so it lives on the rows rather than being recomputed per request.
/// </para>
/// </remarks>
/// <param name="fieldPair">The two fields this row shows.</param>
/// <param name="rowPath">The child index at each level from the root, which identifies the row.</param>
/// <param name="parent">The row above, or nothing at the root.</param>
/// <param name="viewGenerator">What builds this row's children when they are first needed.</param>
public sealed class HollowDiffViewRow(
    EffigyFieldPair fieldPair,
    int[] rowPath,
    HollowDiffViewRow? parent,
    HollowObjectDiffViewGenerator viewGenerator)
{
    private IReadOnlyList<HollowDiffViewRow>? _children;
    private long _moreFromRowsBits = -1;
    private long _moreToRowsBits = -1;

    /// <summary>The two fields this row shows.</summary>
    public EffigyFieldPair FieldPair { get; } = fieldPair;

    /// <summary>The child index at each level from the root.</summary>
    public int[] RowPath { get; } = rowPath;

    /// <summary>The row above, or nothing at the root.</summary>
    public HollowDiffViewRow? Parent { get; } = parent;

    /// <summary>How far to indent this row, which is how deep it sits.</summary>
    public int Indentation => RowPath.Length;

    /// <summary>Whether the page is currently showing this row.</summary>
    public bool IsVisible { get; set; }

    /// <summary>Whether this row's children have been built.</summary>
    public bool AreChildrenPopulated => _children is not null;

    /// <summary>The rows beneath this one, built the first time they are asked for.</summary>
    public IReadOnlyList<HollowDiffViewRow> Children =>
        _children ??= viewGenerator.TraverseEffigyToCreateViewRows(this);

    /// <summary>
    /// What the reader can do to this row: open it, close it, or finish opening it.
    /// </summary>
    public DiffViewRowAction AvailableAction
    {
        get
        {
            if (Children.Count == 0)
            {
                return DiffViewRowAction.None;
            }

            bool foundVisible = false;
            bool foundInvisible = false;

            foreach (HollowDiffViewRow child in Children)
            {
                if (child.IsVisible)
                {
                    // Some shown and some not: the reader opened part of this branch, so the useful
                    // offer is to open the rest rather than to close what they opened.
                    if (foundInvisible)
                    {
                        return DiffViewRowAction.PartialUncollapse;
                    }

                    foundVisible = true;
                }
                else
                {
                    if (foundVisible)
                    {
                        return DiffViewRowAction.PartialUncollapse;
                    }

                    foundInvisible = true;
                }
            }

            return foundVisible ? DiffViewRowAction.Collapse : DiffViewRowAction.Uncollapse;
        }
    }

    /// <summary>
    /// Whether a row further down still has something on the <c>from</c> side at
    /// <paramref name="indentation"/>.
    /// </summary>
    /// <remarks>
    /// This is what draws the vertical rules down the left of the two columns. A rule is continued past
    /// this row only where a later sibling at that depth has a field on that side — otherwise the
    /// branch ends here and the rule should stop.
    /// </remarks>
    public bool HasMoreFromRows(int indentation)
    {
        EnsureMoreRowsBits();

        return (_moreFromRowsBits & (1L << indentation)) != 0;
    }

    /// <summary>The same for the <c>to</c> side.</summary>
    public bool HasMoreToRows(int indentation)
    {
        EnsureMoreRowsBits();

        return (_moreToRowsBits & (1L << indentation)) != 0;
    }

    private void EnsureMoreRowsBits()
    {
        if (_moreFromRowsBits != -1)
        {
            return;
        }

        _moreFromRowsBits = 0;
        _moreToRowsBits = 0;

        HollowDiffViewRow? ancestor = Parent;

        // Walked from this row up to the root, asking at each level whether that ancestor has a later
        // child with anything on the side in question.
        for (int i = RowPath.Length; i >= 1 && ancestor is not null; i--)
        {
            if (HasLaterSibling(ancestor, RowPath[i - 1], fromSide: true))
            {
                _moreFromRowsBits |= 1L << i;
            }

            if (HasLaterSibling(ancestor, RowPath[i - 1], fromSide: false))
            {
                _moreToRowsBits |= 1L << i;
            }

            ancestor = ancestor.Parent;
        }
    }

    private static bool HasLaterSibling(HollowDiffViewRow parent, int childIndex, bool fromSide)
    {
        for (int i = childIndex + 1; i < parent.Children.Count; i++)
        {
            EffigyFieldPair pair = parent.Children[i].FieldPair;

            if (fromSide ? pair.From is not null : pair.To is not null)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>What a reader can do to a row that has something beneath it.</summary>
public enum DiffViewRowAction
{
    /// <summary>Its branch is open; it can be closed.</summary>
    Collapse,

    /// <summary>Its branch is closed; it can be opened.</summary>
    Uncollapse,

    /// <summary>Part of its branch is open; the rest can be.</summary>
    PartialUncollapse,

    /// <summary>Nothing beneath it.</summary>
    None,
}
