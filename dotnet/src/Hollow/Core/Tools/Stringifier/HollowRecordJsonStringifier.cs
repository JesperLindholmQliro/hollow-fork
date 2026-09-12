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
using Hollow.Api.Objects;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Iterator;
using Hollow.Core.Schema;
using Hollow.Core.Util;

namespace Hollow.Core.Tools.Stringifier;

/// <summary>
/// Writes a record out as JSON, for reading it into something that is not Hollow.
/// </summary>
/// <remarks>
/// The shape is the record's rather than the blob's: a record holding one field stands in for that
/// field, a field holding nothing is left out rather than written as null, and a map keyed by a value
/// becomes an object rather than a list of pairs. All three are lossy, and all three are what makes
/// the output read like the data instead of like its storage.
/// </remarks>
/// <param name="prettyPrint">Whether to break lines and indent.</param>
/// <param name="collapseAllSingleFieldObjects">
/// Whether a record holding one field should stand in for that field's value.
/// </param>
/// <param name="sortSingleFieldSetElements">Whether to put a set's elements in a settled order.</param>
public sealed class HollowRecordJsonStringifier(
    bool prettyPrint = true,
    bool collapseAllSingleFieldObjects = true,
    bool sortSingleFieldSetElements = false) : HollowStringifier
{
    private readonly HashSet<string> _collapseObjectTypes = new(StringComparer.Ordinal);

    /// <summary>
    /// Collapses only the named types, rather than every type holding a single field.
    /// </summary>
    /// <param name="prettyPrint">Whether to break lines and indent.</param>
    /// <param name="collapseObjectTypes">The types to collapse.</param>
    public HollowRecordJsonStringifier(bool prettyPrint, params string[] collapseObjectTypes)
        : this(prettyPrint, collapseAllSingleFieldObjects: false)
    {
        ArgumentNullException.ThrowIfNull(collapseObjectTypes);

        foreach (string type in collapseObjectTypes)
        {
            _collapseObjectTypes.Add(type);
        }
    }

    /// <inheritdoc />
    public override void Stringify(
        TextWriter writer, IHollowDataAccess dataAccess, string type, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(dataAccess);

        AppendStringify(writer, dataAccess, type, ordinal, 0);
    }

    /// <inheritdoc />
    public override void Stringify(TextWriter writer, IEnumerable<IHollowRecord> records)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(records);

        writer.Write("[");

        bool first = true;

        foreach (IHollowRecord record in records)
        {
            if (!first)
            {
                writer.Write(",");
            }

            first = false;
            Stringify(writer, record);
        }

        // Unlike the text form, the closing bracket is not put on a line of its own — this is JSON, and
        // a reader of it is a program.
        writer.Write("]");
    }

    private void AppendStringify(
        TextWriter writer, IHollowDataAccess dataAccess, string type, int ordinal, int indentation)
    {
        if (ExcludedObjectTypes.Contains(type))
        {
            writer.Write("null");

            return;
        }

        switch (dataAccess.GetTypeDataAccess(type))
        {
            case null:
                // A type the dataset does not have is an empty object rather than an error, so that what
                // comes out still parses.
                writer.Write("{ }");

                break;

            case { } when ordinal == HollowConstants.OrdinalNone:
                writer.Write("null");

                break;

            case IHollowObjectTypeDataAccess objectAccess:
                AppendObject(writer, dataAccess, objectAccess, ordinal, indentation);

                break;

            case IHollowListTypeDataAccess listAccess:
                AppendList(writer, dataAccess, listAccess, ordinal, indentation);

                break;

            case IHollowSetTypeDataAccess setAccess:
                AppendSet(writer, dataAccess, setAccess, ordinal, indentation);

                break;

            case IHollowMapTypeDataAccess mapAccess:
                AppendMap(writer, dataAccess, mapAccess, ordinal, indentation);

                break;
        }
    }

    private void AppendMap(
        TextWriter writer,
        IHollowDataAccess dataAccess,
        IHollowMapTypeDataAccess typeDataAccess,
        int ordinal,
        int indentation)
    {
        indentation++;

        HollowMapSchema schema = typeDataAccess.Schema;

        if (typeDataAccess.Size(ordinal) == 0)
        {
            writer.Write("{ }");

            return;
        }

        IHollowMapEntryOrdinalIterator iterator = typeDataAccess.OrdinalIterator(ordinal);

        // A JSON key can only be a string, so only a key that is itself a value can become one — a key
        // that is a record has to become a list of pairs instead.
        if (dataAccess.GetTypeDataAccess(schema.KeyType) is IHollowObjectTypeDataAccess keyAccess
            && IsPrimitiveWrapper(keyAccess.Schema))
        {
            KeyValueAsObject(writer, dataAccess, indentation, keyAccess, schema.ValueType, iterator);
        }
        else
        {
            KeyValueAsList(writer, dataAccess, indentation, schema.KeyType, schema.ValueType, iterator);
        }
    }

    private void KeyValueAsObject(
        TextWriter writer,
        IHollowDataAccess dataAccess,
        int indentation,
        IHollowObjectTypeDataAccess keyTypeDataAccess,
        string valueType,
        IHollowMapEntryOrdinalIterator iterator)
    {
        HollowObjectSchema keySchema = keyTypeDataAccess.Schema;

        writer.Write("{");
        AppendNewline(writer);

        bool firstEntry = true;

        while (iterator.Next())
        {
            if (!firstEntry)
            {
                writer.Write(",");
                AppendNewline(writer);
            }

            firstEntry = false;

            if (prettyPrint)
            {
                AppendIndentation(writer, indentation);
            }

            // A key that is not already text has to be wrapped in quotes to be a JSON key at all.
            bool needToQuoteKey = keySchema.GetFieldType(0) != FieldType.String;

            if (needToQuoteKey)
            {
                writer.Write("\"");
            }

            AppendField(writer, dataAccess, keyTypeDataAccess, keySchema, iterator.Key, 0, indentation);

            if (needToQuoteKey)
            {
                writer.Write("\"");
            }

            writer.Write(": ");
            AppendStringify(writer, dataAccess, valueType, iterator.Value, indentation);
        }

        if (prettyPrint && !firstEntry)
        {
            writer.Write(Newline);
            AppendIndentation(writer, indentation - 1);
        }

        writer.Write("}");
    }

    private void KeyValueAsList(
        TextWriter writer,
        IHollowDataAccess dataAccess,
        int indentation,
        string keyType,
        string valueType,
        IHollowMapEntryOrdinalIterator iterator)
    {
        writer.Write("[");
        AppendNewline(writer);

        bool firstEntry = true;

        while (iterator.Next())
        {
            if (!firstEntry)
            {
                writer.Write(",");
                AppendNewline(writer);
            }

            firstEntry = false;

            if (prettyPrint)
            {
                AppendIndentation(writer, indentation - 1);
            }

            writer.Write("{");

            if (prettyPrint)
            {
                writer.Write(Newline);
                AppendIndentation(writer, indentation);
            }

            writer.Write("\"key\":");
            AppendStringify(writer, dataAccess, keyType, iterator.Key, indentation + 1);
            writer.Write(",");

            if (prettyPrint)
            {
                writer.Write(Newline);
                AppendIndentation(writer, indentation);
            }

            writer.Write("\"value\":");
            AppendStringify(writer, dataAccess, valueType, iterator.Value, indentation + 1);

            if (prettyPrint)
            {
                writer.Write(Newline);
                AppendIndentation(writer, indentation - 1);
            }

            writer.Write("}");
        }

        writer.Write("]");
    }

    private void AppendSet(
        TextWriter writer,
        IHollowDataAccess dataAccess,
        IHollowSetTypeDataAccess typeDataAccess,
        int ordinal,
        int indentation)
    {
        indentation++;

        List<int> elementOrdinals =
            SetElementOrdinals(dataAccess, typeDataAccess, ordinal, sortSingleFieldSetElements);

        if (elementOrdinals.Count == 0)
        {
            writer.Write("[]");

            return;
        }

        writer.Write("[");
        AppendNewline(writer);

        bool firstElement = true;

        foreach (int elementOrdinal in elementOrdinals)
        {
            if (!firstElement)
            {
                writer.Write(",");

                // Java breaks the line between a list's elements but not a set's, so a pretty-printed
                // set comes out with every element after the first on one line. Nothing reads the
                // output but a person, and it is the same JSON either way.
                AppendNewline(writer);
            }

            firstElement = false;

            if (prettyPrint)
            {
                AppendIndentation(writer, indentation);
            }

            AppendStringify(
                writer, dataAccess, typeDataAccess.Schema.ElementType, elementOrdinal, indentation);
        }

        if (prettyPrint)
        {
            writer.Write(Newline);
            AppendIndentation(writer, indentation - 1);
        }

        writer.Write("]");
    }

    private void AppendList(
        TextWriter writer,
        IHollowDataAccess dataAccess,
        IHollowListTypeDataAccess typeDataAccess,
        int ordinal,
        int indentation)
    {
        indentation++;

        int size = typeDataAccess.Size(ordinal);

        if (size == 0)
        {
            writer.Write("[]");

            return;
        }

        writer.Write("[");
        AppendNewline(writer);

        for (int i = 0; i < size; i++)
        {
            if (prettyPrint)
            {
                AppendIndentation(writer, indentation);
            }

            AppendStringify(
                writer,
                dataAccess,
                typeDataAccess.Schema.ElementType,
                typeDataAccess.GetElementOrdinal(ordinal, i),
                indentation);

            if (i < size - 1)
            {
                writer.Write(",");
                AppendNewline(writer);
            }
        }

        if (prettyPrint)
        {
            writer.Write(Newline);
            AppendIndentation(writer, indentation - 1);
        }

        writer.Write("]");
    }

    private void AppendObject(
        TextWriter writer,
        IHollowDataAccess dataAccess,
        IHollowObjectTypeDataAccess typeDataAccess,
        int ordinal,
        int indentation)
    {
        HollowObjectSchema schema = typeDataAccess.Schema;

        if (schema.FieldCount == 1
            && (collapseAllSingleFieldObjects || _collapseObjectTypes.Contains(schema.Name)))
        {
            AppendField(writer, dataAccess, typeDataAccess, schema, ordinal, 0, indentation);

            return;
        }

        writer.Write("{");
        indentation++;

        bool firstField = true;

        for (int i = 0; i < schema.FieldCount; i++)
        {
            // A field holding nothing is absent rather than null, which is what a reader of JSON
            // expects — and what makes a sparse record short.
            if (typeDataAccess.IsNull(ordinal, i))
            {
                continue;
            }

            if (!firstField)
            {
                writer.Write(",");
            }

            firstField = false;

            if (prettyPrint)
            {
                writer.Write(Newline);
                AppendIndentation(writer, indentation);
            }

            writer.Write($"\"{schema.GetFieldName(i)}\": ");
            AppendField(writer, dataAccess, typeDataAccess, schema, ordinal, i, indentation);
        }

        if (prettyPrint && !firstField)
        {
            writer.Write(Newline);
            AppendIndentation(writer, indentation - 1);
        }

        writer.Write("}");
    }

    private void AppendField(
        TextWriter writer,
        IHollowDataAccess dataAccess,
        IHollowObjectTypeDataAccess typeDataAccess,
        HollowObjectSchema schema,
        int ordinal,
        int fieldIndex,
        int indentation)
    {
        switch (schema.GetFieldType(fieldIndex))
        {
            case FieldType.Boolean:
                writer.Write(typeDataAccess.ReadBoolean(ordinal, fieldIndex) == true ? "true" : "false");

                break;

            case FieldType.Bytes:
                writer.Write($"[{string.Join(", ", typeDataAccess.ReadBytes(ordinal, fieldIndex) ?? [])}]");

                break;

            case FieldType.Double:
                writer.Write(typeDataAccess.ReadDouble(ordinal, fieldIndex).Invariant());

                break;

            case FieldType.Float:
                writer.Write(typeDataAccess.ReadFloat(ordinal, fieldIndex).Invariant());

                break;

            case FieldType.Int:
                writer.Write(typeDataAccess.ReadInt(ordinal, fieldIndex).Invariant());

                break;

            case FieldType.Long:
                writer.Write(typeDataAccess.ReadLong(ordinal, fieldIndex).Invariant());

                break;

            case FieldType.Decimal:
                writer.Write(typeDataAccess.ReadDecimal(ordinal, fieldIndex)?.Invariant());

                break;

            case FieldType.String:
                writer.Write("\"");
                AppendEscaped(writer, typeDataAccess.ReadString(ordinal, fieldIndex));
                writer.Write("\"");

                break;

            case FieldType.Reference:
                AppendStringify(
                    writer,
                    dataAccess,
                    schema.GetReferencedType(fieldIndex)!,
                    typeDataAccess.ReadOrdinal(ordinal, fieldIndex),
                    indentation);

                break;
        }
    }

    private void AppendNewline(TextWriter writer)
    {
        if (prettyPrint)
        {
            writer.Write(Newline);
        }
    }

    /// <summary>
    /// Writes <paramref name="value"/> as the body of a JSON string.
    /// </summary>
    /// <remarks>
    /// A record holds whatever text someone put in it, so escaping is what keeps the output JSON at
    /// all. Anything below a space that has no short escape is written as its code point, since a raw
    /// control character in a JSON string is not valid.
    /// </remarks>
    private static void AppendEscaped(TextWriter writer, string? value)
    {
        if (value is null)
        {
            return;
        }

        foreach (char c in value)
        {
            switch (c)
            {
                case '"':
                    writer.Write("\\\"");

                    break;

                case '\\':
                    writer.Write("\\\\");

                    break;

                case '\n':
                    writer.Write("\\n");

                    break;

                case '\r':
                    writer.Write("\\r");

                    break;

                case '\t':
                    writer.Write("\\t");

                    break;

                case '\b':
                    writer.Write("\\b");

                    break;

                case '\f':
                    writer.Write("\\f");

                    break;

                default:
                    if (c < 0x20)
                    {
                        writer.Write(
                            string.Create(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}"));
                    }
                    else
                    {
                        writer.Write(c);
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="schema"/> is a record standing in for a single value.
    /// </summary>
    /// <remarks>
    /// One field, and that field a value rather than another reference — which is what the shared
    /// <c>String</c>, <c>Integer</c> and <c>Long</c> types are, and what can therefore be a JSON key.
    /// </remarks>
    private static bool IsPrimitiveWrapper(HollowSchema schema) =>
        schema is HollowObjectSchema { FieldCount: 1 } objectSchema
        && objectSchema.GetFieldType(0) != FieldType.Reference;
}
