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
using System.Text;
using System.Text.Encodings.Web;
using Hollow.Explorer.Diff.Effigy;
using Hollow.Explorer.Diff.Effigy.Pairer;

namespace Hollow.Explorer.Diff;

/// <summary>
/// One visible row, worked out to the point where it can be written either as HTML or down the wire.
/// </summary>
/// <param name="RowPath">Which row this is, as the dotted child indexes the page identifies it by.</param>
/// <param name="Action">What the reader can do to its branch.</param>
/// <param name="FromMarginIndex">Its position on the earlier side, or empty where position means nothing.</param>
/// <param name="FromCellClass">How the earlier cell should be coloured.</param>
/// <param name="FromContent">The earlier cell, tree characters and all, ready to write.</param>
/// <param name="ToMarginIndex">Its position on the later side.</param>
/// <param name="ToCellClass">How the later cell should be coloured.</param>
/// <param name="ToContent">The later cell.</param>
public sealed record DiffViewRowDisplay(
    string RowPath,
    DiffViewRowAction Action,
    string FromMarginIndex,
    string FromCellClass,
    string FromContent,
    string ToMarginIndex,
    string ToCellClass,
    string ToContent);

/// <summary>
/// Turns the visible part of a row tree into rows a page can draw.
/// </summary>
/// <remarks>
/// <para>
/// The two columns are drawn with box-drawing characters rather than nested markup, which is what lets
/// a record a dozen levels deep stay readable in a table of two cells per row.
/// </para>
/// <para>
/// Java writes these rows as a pipe-delimited string and then tokenises that string back apart to make
/// the HTML. Here the rows are worked out once as values, and the delimited form is produced only for
/// the browser, which is the one place it is actually needed.
/// </para>
/// </remarks>
public static class DiffViewRowRenderer
{
    /// <summary>Every visible row beneath <paramref name="parentRow"/>, depth first.</summary>
    public static IEnumerable<DiffViewRowDisplay> VisibleRows(HollowDiffViewRow parentRow)
    {
        ArgumentNullException.ThrowIfNull(parentRow);

        foreach (HollowDiffViewRow row in parentRow.Children)
        {
            if (!row.IsVisible)
            {
                continue;
            }

            yield return Render(row);

            foreach (DiffViewRowDisplay descendant in VisibleRows(row))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>
    /// Writes rows in the pipe-delimited form the page's script reads.
    /// </summary>
    /// <remarks>
    /// Eight fields per row, rows run together, no row separator — the script counts fields rather than
    /// looking for a terminator. A value containing a pipe has already had it replaced, since there is
    /// no escape in this format.
    /// </remarks>
    public static void WriteDelimited(IEnumerable<DiffViewRowDisplay> rows, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(writer);

        bool first = true;

        foreach (DiffViewRowDisplay row in rows)
        {
            if (!first)
            {
                writer.Write('|');
            }

            first = false;

            writer.Write(row.RowPath);
            writer.Write('|');
            writer.Write(ActionName(row.Action));
            writer.Write('|');
            writer.Write(row.FromMarginIndex);
            writer.Write('|');
            writer.Write(row.FromCellClass);
            writer.Write('|');
            writer.Write(row.FromContent);
            writer.Write('|');
            writer.Write(row.ToMarginIndex);
            writer.Write('|');
            writer.Write(row.ToCellClass);
            writer.Write('|');
            writer.Write(row.ToContent);
        }
    }

    /// <summary>
    /// The action name the page's script expects, which is Java's enum spelling.
    /// </summary>
    internal static string ActionName(DiffViewRowAction action) =>
        action switch
        {
            DiffViewRowAction.Collapse => "COLLAPSE",
            DiffViewRowAction.Uncollapse => "UNCOLLAPSE",
            DiffViewRowAction.PartialUncollapse => "PARTIAL_UNCOLLAPSE",
            _ => "NONE",
        };

    /// <summary>The row at <paramref name="rowPath"/>, or <see langword="null"/> if there is none.</summary>
    /// <remarks>
    /// The path comes from a URL, so every step of it is checked rather than assumed — a stale page or
    /// a hand-edited request must not be able to walk off the end of a branch.
    /// </remarks>
    public static HollowDiffViewRow? FindRow(HollowDiffViewRow rootRow, string? rowPath)
    {
        ArgumentNullException.ThrowIfNull(rootRow);

        if (string.IsNullOrEmpty(rowPath))
        {
            return rootRow;
        }

        HollowDiffViewRow row = rootRow;

        foreach (string step in rowPath.Split('.'))
        {
            if (!int.TryParse(step, CultureInfo.InvariantCulture, out int index)
                || index < 0
                || index >= row.Children.Count)
            {
                return null;
            }

            row = row.Children[index];
        }

        return row;
    }

    private static DiffViewRowDisplay Render(HollowDiffViewRow row)
    {
        EffigyFieldPair pair = row.FieldPair;

        return new DiffViewRowDisplay(
            RowPath: string.Join('.', row.RowPath.Select(step => step.ToString(CultureInfo.InvariantCulture))),
            Action: row.AvailableAction,
            FromMarginIndex: MarginIndex(pair.FromIndex),
            FromCellClass: CellClass(pair, fromSide: true),
            FromContent: Content(row, fromSide: true),
            ToMarginIndex: MarginIndex(pair.ToIndex),
            ToCellClass: CellClass(pair, fromSide: false),
            ToContent: Content(row, fromSide: false));
    }

    private static string MarginIndex(int index) =>
        index == -1 ? "" : index.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// How a cell should be coloured: gone, arrived, changed, unchanged, or nothing there at all.
    /// </summary>
    private static string CellClass(EffigyFieldPair pair, bool fromSide)
    {
        // The one asymmetry: a row present on only one side reads as a removal on the left and an
        // addition on the right.
        if (pair.From is null)
        {
            return fromSide ? "empty" : "insert";
        }

        if (pair.To is null)
        {
            return fromSide ? "delete" : "empty";
        }

        if (pair.From.Value is null && pair.To.Value is null)
        {
            return "equal";
        }

        if (pair.From.Value is null || pair.To.Value is null)
        {
            return "replace";
        }

        return pair.IsLeafNode && !pair.From.Value.Equals(pair.To.Value) ? "replace" : "equal";
    }

    /// <summary>
    /// One cell: the tree drawing down to this row, then its name and value.
    /// </summary>
    private static string Content(HollowDiffViewRow row, bool fromSide)
    {
        bool[] moreRows = new bool[row.Indentation + 1];

        for (int i = 0; i <= row.Indentation; i++)
        {
            moreRows[i] = fromSide ? row.HasMoreFromRows(i) : row.HasMoreToRows(i);
        }

        HollowEffigyField? field = fromSide ? row.FieldPair.From : row.FieldPair.To;

        // Nothing on this side, so the cell is only the rules running past it.
        if (field is null)
        {
            StringBuilder empty = new();

            foreach (bool more in moreRows)
            {
                empty.Append(more ? " &#x2502;" : "  ");
            }

            return empty.ToString();
        }

        StringBuilder builder = new();

        for (int i = 0; i < row.Indentation; i++)
        {
            builder.Append(moreRows[i] ? ".&#x2502;" : "..");
        }

        // A branch gets a fork, a value gets a plain arm; either is a tee where more rows follow at
        // this depth and an elbow where this is the last.
        builder.Append(row.FieldPair.IsLeafNode
            ? moreRows[row.Indentation]
                ? ".&#x251C;&#x2500;&#x2500;&#x2500;&gt;"
                : ".&#x2514;&#x2500;&#x2500;&#x2500;&gt;"
            : moreRows[row.Indentation]
                ? ".&#x251D;&#x2501;&#x252F;&#x2501;&gt;"
                : ".&#x2515;&#x2501;&#x252F;&#x2501;&gt;");

        if (field.FieldName is { } fieldName)
        {
            builder.Append(Escape(fieldName)).Append(": ");
        }

        return builder.Append(FieldValue(row, field)).ToString();
    }

    /// <summary>
    /// A field's value as the cell should show it.
    /// </summary>
    /// <remarks>
    /// A branch shows the type it leads to rather than its contents, since its contents are the rows
    /// below. A value shows itself — escaped, because it is data somebody else wrote.
    /// </remarks>
    private static string FieldValue(HollowDiffViewRow row, HollowEffigyField field)
    {
        if (!row.FieldPair.IsLeafNode)
        {
            return $"({Escape(field.TypeName)}){(field.Value is null ? " [null]" : "")}";
        }

        return field.Value is null ? "null" : Escape(FormatValue(field.Value));
    }

    /// <summary>
    /// Renders a leaf value the same way on every machine, whatever its culture.
    /// </summary>
    private static string FormatValue(object value) =>
        value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : value.ToString() ?? "";

    /// <summary>
    /// Escapes text for the cell, and neutralises the delimiter the wire format cannot escape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Java replaces the pipe and stops there, so a record holding markup puts that markup straight
    /// into the page. This escapes first and then replaces, which is the same replacement Java makes —
    /// a pipe becomes the box-drawing bar it looks like — over text that can no longer be markup.
    /// </para>
    /// <para>
    /// The replacement is done on the escaped text so that the entity's own characters survive: a
    /// <c>&amp;</c> arriving in the data has already become <c>&amp;amp;</c> by this point.
    /// </para>
    /// </remarks>
    private static string Escape(string value) =>
        HtmlEncoder.Default.Encode(value).Replace("|", "&#x2502;", StringComparison.Ordinal);
}
