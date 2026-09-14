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
using Hollow.Core.Index;
using Hollow.Core.Index.Key;
using Hollow.Core.Read;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Engine.Object;
using Hollow.Core.Schema;
using Hollow.Core.Util;

namespace Hollow.Core.Tools.Util;

/// <summary>
/// Finding a record from a key typed into a search box.
/// </summary>
/// <remarks>
/// A key is one string even when the record's key spans several fields, so the whole of this is about
/// the two directions of that: splitting the string into typed field values, and building the string
/// back from a record.
/// </remarks>
public static class SearchUtils
{
    /// <summary>What separates one field of a key from the next.</summary>
    public const string MultiFieldKeyDelimiter = ":";

    /// <summary>The delimiter as it appears inside a field value that contains one.</summary>
    public const string EscapedMultiFieldKeyDelimiter = @"\:";

    /// <summary>
    /// Splits <paramref name="keyString"/> into the values <paramref name="primaryKey"/>'s fields take.
    /// </summary>
    /// <remarks>
    /// Split into exactly as many parts as the key has fields, so that a trailing empty field survives;
    /// a delimiter the caller escaped with a backslash is part of the value rather than a separator.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// A part cannot be read as the type its field holds, or the key includes a bytes field.
    /// </exception>
    public static object?[] ParseKey(
        HollowReadStateEngine readStateEngine, PrimaryKey primaryKey, string keyString)
    {
        ArgumentNullException.ThrowIfNull(readStateEngine);
        ArgumentNullException.ThrowIfNull(primaryKey);
        ArgumentNullException.ThrowIfNull(keyString);

        string[] fields = SplitOnUnescapedDelimiters(keyString, primaryKey.FieldCount);
        object?[] key = new object?[fields.Length];

        for (int i = 0; i < fields.Length; i++)
        {
            FieldType fieldType = primaryKey.GetFieldType(readStateEngine, i);

            try
            {
                key[i] = fieldType switch
                {
                    FieldType.Boolean => bool.Parse(fields[i]),
                    FieldType.String => Unescape(fields[i]),
                    FieldType.Int or FieldType.Reference => int.Parse(fields[i], CultureInfo.InvariantCulture),
                    FieldType.Long => long.Parse(fields[i], CultureInfo.InvariantCulture),
                    FieldType.Double => double.Parse(fields[i], CultureInfo.InvariantCulture),
                    FieldType.Float => float.Parse(fields[i], CultureInfo.InvariantCulture),
                    FieldType.Decimal => decimal.Parse(fields[i], CultureInfo.InvariantCulture),
                    _ => throw new ArgumentException(
                        $"the key of {primaryKey.Type} has a field of type {fieldType}, which cannot be "
                        + "written as text",
                        nameof(primaryKey)),
                };
            }
            catch (Exception e) when (e is FormatException or OverflowException)
            {
                throw new ArgumentException(
                    $"\"{fields[i]}\" is not a {fieldType}, which is what field {i} of "
                    + $"{primaryKey.Type}'s key holds",
                    nameof(keyString),
                    e);
            }
        }

        return key;
    }

    /// <summary>
    /// Resolves each of <paramref name="primaryKey"/>'s field paths to the field positions it walks,
    /// or <see langword="null"/> when there is no key.
    /// </summary>
    public static int[][]? GetFieldPathIndexes(
        HollowReadStateEngine readStateEngine, PrimaryKey? primaryKey)
    {
        ArgumentNullException.ThrowIfNull(readStateEngine);

        if (primaryKey is null)
        {
            return null;
        }

        int[][] fieldPathIndexes = new int[primaryKey.FieldCount][];

        for (int i = 0; i < primaryKey.FieldCount; i++)
        {
            fieldPathIndexes[i] = primaryKey.GetFieldPathIndex(readStateEngine, i);
        }

        return fieldPathIndexes;
    }

    /// <summary>The primary key <paramref name="schema"/> declares, if it is an object type that does.</summary>
    public static PrimaryKey? GetPrimaryKey(HollowSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        return schema is HollowObjectSchema objectSchema ? objectSchema.PrimaryKey : null;
    }

    /// <summary>
    /// An index already attached to <paramref name="typeState"/> for its declared key, if one is.
    /// </summary>
    /// <remarks>
    /// A type someone has already indexed can answer a key lookup without a scan. Nothing builds one
    /// for the sake of a single search, which on a large type would cost more than the scan it saves.
    /// </remarks>
    public static HollowPrimaryKeyIndex? FindPrimaryKeyIndex(HollowTypeReadState typeState)
    {
        ArgumentNullException.ThrowIfNull(typeState);

        if (GetPrimaryKey(typeState.Schema) is not { } primaryKey)
        {
            return null;
        }

        return typeState.Listeners
            .OfType<HollowPrimaryKeyIndex>()
            .FirstOrDefault(index => index.PrimaryKey.Equals(primaryKey));
    }

    /// <summary>
    /// The ordinal a search should show: the one asked for when it still matches, and otherwise the
    /// one the key finds.
    /// </summary>
    /// <param name="readStateEngine">The state being searched.</param>
    /// <param name="query">The key as it was typed, empty when there is none.</param>
    /// <param name="parsedKey">That key split into field values.</param>
    /// <param name="ordinal">The ordinal the caller asked for, or none.</param>
    /// <param name="selectedOrdinals">The ordinals in scope, which a query may have narrowed.</param>
    /// <param name="fieldPathIndexes">The key's resolved field paths.</param>
    /// <param name="keyTypeState">The type being searched.</param>
    /// <returns>The ordinal to display, or <see cref="HollowConstants.OrdinalNone"/>.</returns>
    public static int GetOrdinalToDisplay(
        HollowReadStateEngine readStateEngine,
        string query,
        object?[]? parsedKey,
        int ordinal,
        BitSet selectedOrdinals,
        int[][]? fieldPathIndexes,
        HollowTypeReadState keyTypeState)
    {
        ArgumentNullException.ThrowIfNull(readStateEngine);
        ArgumentNullException.ThrowIfNull(selectedOrdinals);
        ArgumentNullException.ThrowIfNull(keyTypeState);

        // Nothing typed, so the ordinal stands on its own.
        if (query.Length == 0)
        {
            return ordinal;
        }

        if (parsedKey is null || fieldPathIndexes is null)
        {
            return HollowConstants.OrdinalNone;
        }

        // The ordinal came from the last request, so it is only still right if the key agrees with it.
        if (ordinal != HollowConstants.OrdinalNone
            && selectedOrdinals.Get(ordinal)
            && RecordKeyEquals(keyTypeState, ordinal, parsedKey, fieldPathIndexes))
        {
            return ordinal;
        }

        if (FindPrimaryKeyIndex(keyTypeState) is { } index)
        {
            return index.GetMatchingOrdinal(parsedKey);
        }

        for (int candidate = selectedOrdinals.NextSetBit(0);
            candidate != HollowConstants.OrdinalNone;
            candidate = selectedOrdinals.NextSetBit(candidate + 1))
        {
            if (RecordKeyEquals(keyTypeState, candidate, parsedKey, fieldPathIndexes))
            {
                return candidate;
            }
        }

        return HollowConstants.OrdinalNone;
    }

    /// <summary>
    /// The key of the record at <paramref name="ordinal"/>, as the text a search box takes.
    /// </summary>
    /// <remarks>
    /// A field whose own value contains the delimiter is escaped, so that reading the key back splits
    /// it the way it was built. A null along the path is written as <c>null</c> rather than dropping
    /// the record, so that it can still be seen and acted on.
    /// </remarks>
    public static string BuildKey(
        HollowTypeReadState typeState, int ordinal, int[][] fieldPathIndexes, bool escapeDelimiters)
    {
        ArgumentNullException.ThrowIfNull(typeState);
        ArgumentNullException.ThrowIfNull(fieldPathIndexes);

        HollowObjectTypeReadState objectState = (HollowObjectTypeReadState)typeState;
        StringBuilder key = new();

        for (int i = 0; i < fieldPathIndexes.Length; i++)
        {
            if (i > 0)
            {
                key.Append(MultiFieldKeyDelimiter);
            }

            (HollowObjectTypeReadState state, int fieldOrdinal) = WalkTo(objectState, ordinal, fieldPathIndexes[i]);

            if (fieldOrdinal == HollowConstants.OrdinalNone)
            {
                key.Append("null");
                continue;
            }

            object? value = HollowReadFieldUtils.FieldValueObject(
                state, fieldOrdinal, fieldPathIndexes[i][^1]);

            key.Append(
                escapeDelimiters && value is string text
                    ? text.Replace(MultiFieldKeyDelimiter, EscapedMultiFieldKeyDelimiter, StringComparison.Ordinal)
                    : value.Invariant());
        }

        return key.ToString();
    }

    /// <summary>
    /// Splits on delimiters that are not escaped, into at most <paramref name="limit"/> parts.
    /// </summary>
    /// <remarks>
    /// Java writes this as a lookbehind regex. Walking the string says the same thing without one, and
    /// keeps a trailing empty field the way Java's limited split does.
    /// </remarks>
    /// <summary>
    /// Splits <paramref name="value"/> on delimiters that are not escaped, into at most
    /// <paramref name="limit"/> parts.
    /// </summary>
    /// <remarks>
    /// Public because the history's key index parses the same composite-key text the explorer's search
    /// box does, and the two must agree on what an escaped delimiter is.
    /// </remarks>
    public static string[] SplitOnUnescapedDelimiters(string value, int limit)
    {
        List<string> parts = [];
        StringBuilder current = new();

        for (int i = 0; i < value.Length; i++)
        {
            bool escaped = i > 0 && value[i - 1] == '\\';

            if (value[i] == ':' && !escaped && parts.Count < limit - 1)
            {
                parts.Add(current.ToString());
                current.Clear();

                continue;
            }

            current.Append(value[i]);
        }

        parts.Add(current.ToString());

        return [.. parts];
    }

    private static string Unescape(string value) =>
        value.Replace(EscapedMultiFieldKeyDelimiter, MultiFieldKeyDelimiter, StringComparison.Ordinal);

    /// <summary>
    /// Follows all but the last step of <paramref name="fieldPath"/>, returning where it arrives.
    /// </summary>
    private static (HollowObjectTypeReadState State, int Ordinal) WalkTo(
        HollowObjectTypeReadState typeState, int ordinal, int[] fieldPath)
    {
        HollowObjectTypeReadState state = typeState;
        int current = ordinal;

        for (int i = 0; i < fieldPath.Length - 1 && current != HollowConstants.OrdinalNone; i++)
        {
            current = state.ReadOrdinal(current, fieldPath[i]);
            state = (HollowObjectTypeReadState)state.Schema.GetReferencedTypeState(fieldPath[i])!;
        }

        return (state, current);
    }

    private static bool RecordKeyEquals(
        HollowTypeReadState typeState, int ordinal, object?[] key, int[][] fieldPathIndexes)
    {
        HollowObjectTypeReadState objectState = (HollowObjectTypeReadState)typeState;

        for (int i = 0; i < fieldPathIndexes.Length; i++)
        {
            (HollowObjectTypeReadState state, int fieldOrdinal) = WalkTo(objectState, ordinal, fieldPathIndexes[i]);

            if (fieldOrdinal == HollowConstants.OrdinalNone)
            {
                return false;
            }

            if (!HollowReadFieldUtils.FieldValueEquals(
                state, fieldOrdinal, fieldPathIndexes[i][^1], key[i]))
            {
                return false;
            }
        }

        return true;
    }
}
