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

using System.Text;
using Hollow.Api.Objects;
using Hollow.Core.Read;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Iterator;
using Hollow.Core.Schema;
using Hollow.Core.Util;

namespace Hollow.Core.Tools.Stringifier;

/// <summary>
/// Writing a record out, in whatever form the caller wants to read it in.
/// </summary>
/// <remarks>
/// Java makes this a self-referential generic interface so that <c>addExcludeObjectTypes</c> can
/// return the concrete stringifier for chaining. Nothing here needs to chain, so it is an abstract
/// class and the shared state lives once rather than in each implementation.
/// </remarks>
public abstract class HollowStringifier
{
    /// <summary>What separates one line of output from the next.</summary>
    protected const string Newline = "\n";

    /// <summary>One step of indentation.</summary>
    protected const string Indent = "  ";

    private readonly HashSet<string> _excludeObjectTypes = new(StringComparer.Ordinal);

    /// <summary>The types being written as nothing rather than expanded.</summary>
    protected IReadOnlySet<string> ExcludedObjectTypes => _excludeObjectTypes;

    /// <summary>
    /// Writes records of <paramref name="types"/> as null rather than expanding them.
    /// </summary>
    /// <remarks>
    /// For a type whose records are large and beside the point of whatever is being read — so that what
    /// is left is short enough to take in.
    /// </remarks>
    public void AddExcludeObjectTypes(params string[] types)
    {
        ArgumentNullException.ThrowIfNull(types);

        foreach (string type in types)
        {
            _excludeObjectTypes.Add(type);
        }
    }

    /// <summary>The record at <paramref name="ordinal"/> of <paramref name="type"/>, as text.</summary>
    public string Stringify(IHollowDataAccess dataAccess, string type, int ordinal)
    {
        StringWriter writer = new(new StringBuilder());
        Stringify(writer, dataAccess, type, ordinal);

        return writer.ToString();
    }

    /// <summary><paramref name="record"/> as text.</summary>
    public string Stringify(IHollowRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return Stringify(record.TypeDataAccess.DataAccess, record.Schema.Name, record.Ordinal);
    }

    /// <summary>Writes <paramref name="record"/> to <paramref name="writer"/>.</summary>
    public void Stringify(TextWriter writer, IHollowRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        Stringify(writer, record.TypeDataAccess.DataAccess, record.Schema.Name, record.Ordinal);
    }

    /// <summary>
    /// Writes the record at <paramref name="ordinal"/> of <paramref name="type"/> to
    /// <paramref name="writer"/>.
    /// </summary>
    public abstract void Stringify(
        TextWriter writer, IHollowDataAccess dataAccess, string type, int ordinal);

    /// <summary>Writes <paramref name="records"/> to <paramref name="writer"/> as a list.</summary>
    public virtual void Stringify(TextWriter writer, IEnumerable<IHollowRecord> records)
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

        writer.Write(Newline + "]");
    }

    /// <summary>Writes <paramref name="indentation"/> steps of indentation.</summary>
    protected static void AppendIndentation(TextWriter writer, int indentation)
    {
        for (int i = 0; i < indentation; i++)
        {
            writer.Write(Indent);
        }
    }

    /// <summary>
    /// The ordinals of <paramref name="ordinal"/>'s set, put in a settled order where they can be.
    /// </summary>
    /// <remarks>
    /// A set has no order of its own — where its elements land comes from their hashes — so two states
    /// holding the same elements can iterate them differently. Sorting by value where the element type
    /// holds a single field makes the output something that can be compared between states.
    /// </remarks>
    protected static List<int> SetElementOrdinals(
        IHollowDataAccess dataAccess,
        IHollowSetTypeDataAccess typeDataAccess,
        int ordinal,
        bool sortSingleFieldSetElements)
    {
        IHollowOrdinalIterator iterator = typeDataAccess.OrdinalIterator(ordinal);
        List<int> elementOrdinals = [];

        for (int element = iterator.Next();
            element != IHollowOrdinalIterator.NoMoreOrdinals;
            element = iterator.Next())
        {
            elementOrdinals.Add(element);
        }

        if (sortSingleFieldSetElements
            && dataAccess.GetTypeDataAccess(typeDataAccess.Schema.ElementType)
                is IHollowObjectTypeDataAccess { Schema.FieldCount: 1 } elementAccess)
        {
            elementOrdinals.Sort(
                (left, right) => HollowReadFieldUtils.CompareFieldValues(elementAccess, 0, left, right));
        }

        return elementOrdinals;
    }
}

/// <summary>
/// Writes a record out for a person to read: a line per field, nested by indentation.
/// </summary>
/// <param name="showOrdinals">Whether to name the ordinal each record lives at.</param>
/// <param name="showTypes">Whether to name each record's type.</param>
/// <param name="collapseSingleFieldObjects">
/// Whether a record holding one field should stand in for that field's value, which is what keeps the
/// shared <c>String</c> type from putting a layer around every string in the dataset.
/// </param>
/// <param name="sortSingleFieldSetElements">Whether to put a set's elements in a settled order.</param>
public sealed class HollowRecordStringifier(
    bool showOrdinals = false,
    bool showTypes = false,
    bool collapseSingleFieldObjects = true,
    bool sortSingleFieldSetElements = false) : HollowStringifier
{
    /// <inheritdoc />
    public override void Stringify(
        TextWriter writer, IHollowDataAccess dataAccess, string type, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(dataAccess);

        AppendStringify(writer, dataAccess, type, ordinal, 0);
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
                // A stringifier is usually looking at data it was not written for, so a type the dataset
                // does not have is said rather than thrown.
                writer.Write($"[missing type {type}]");

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
        HollowMapSchema schema = typeDataAccess.Schema;

        AppendHeader(writer, schema.Name, ordinal);
        indentation++;

        IHollowMapEntryOrdinalIterator iterator = typeDataAccess.OrdinalIterator(ordinal);

        while (iterator.Next())
        {
            writer.Write(Newline);
            AppendIndentation(writer, indentation);
            writer.Write("k: ");
            AppendStringify(writer, dataAccess, schema.KeyType, iterator.Key, indentation);

            writer.Write(Newline);
            AppendIndentation(writer, indentation);
            writer.Write("v: ");
            AppendStringify(writer, dataAccess, schema.ValueType, iterator.Value, indentation);
        }
    }

    private void AppendSet(
        TextWriter writer,
        IHollowDataAccess dataAccess,
        IHollowSetTypeDataAccess typeDataAccess,
        int ordinal,
        int indentation)
    {
        HollowSetSchema schema = typeDataAccess.Schema;

        AppendHeader(writer, schema.Name, ordinal);
        indentation++;

        foreach (int elementOrdinal in
            SetElementOrdinals(dataAccess, typeDataAccess, ordinal, sortSingleFieldSetElements))
        {
            writer.Write(Newline);
            AppendIndentation(writer, indentation);

            // A set's elements have no positions, so unlike a list's they are not numbered.
            writer.Write("e: ");
            AppendStringify(writer, dataAccess, schema.ElementType, elementOrdinal, indentation);
        }
    }

    private void AppendList(
        TextWriter writer,
        IHollowDataAccess dataAccess,
        IHollowListTypeDataAccess typeDataAccess,
        int ordinal,
        int indentation)
    {
        HollowListSchema schema = typeDataAccess.Schema;

        AppendHeader(writer, schema.Name, ordinal);
        indentation++;

        int size = typeDataAccess.Size(ordinal);

        for (int i = 0; i < size; i++)
        {
            writer.Write(Newline);
            AppendIndentation(writer, indentation);
            writer.Write($"e{i.Invariant()}: ");

            AppendStringify(
                writer,
                dataAccess,
                schema.ElementType,
                typeDataAccess.GetElementOrdinal(ordinal, i),
                indentation);
        }
    }

    private void AppendObject(
        TextWriter writer,
        IHollowDataAccess dataAccess,
        IHollowObjectTypeDataAccess typeDataAccess,
        int ordinal,
        int indentation)
    {
        HollowObjectSchema schema = typeDataAccess.Schema;

        if (collapseSingleFieldObjects && schema.FieldCount == 1)
        {
            AppendField(writer, dataAccess, typeDataAccess, schema, ordinal, 0, indentation);

            return;
        }

        AppendHeader(writer, schema.Name, ordinal);
        indentation++;

        for (int i = 0; i < schema.FieldCount; i++)
        {
            writer.Write(Newline);
            AppendIndentation(writer, indentation);
            writer.Write($"{schema.GetFieldName(i)}: ");

            AppendField(writer, dataAccess, typeDataAccess, schema, ordinal, i, indentation);
        }
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
        if (typeDataAccess.IsNull(ordinal, fieldIndex))
        {
            // Unlike the JSON form, a field holding nothing is still shown — the point of the text form
            // is to see the shape of a record, and a missing line hides part of it.
            writer.Write("null");

            return;
        }

        switch (schema.GetFieldType(fieldIndex))
        {
            case FieldType.Boolean:
                writer.Write(typeDataAccess.ReadBoolean(ordinal, fieldIndex) == true ? "true" : "false");

                break;

            case FieldType.Bytes:
                writer.Write(
                    $"[{string.Join(", ", typeDataAccess.ReadBytes(ordinal, fieldIndex) ?? [])}]");

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
                writer.Write(typeDataAccess.ReadString(ordinal, fieldIndex));

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

    private void AppendHeader(TextWriter writer, string typeName, int ordinal)
    {
        if (showTypes)
        {
            writer.Write($"({typeName})");
        }

        if (showOrdinals)
        {
            writer.Write($" (ordinal {ordinal.Invariant()})");
        }
    }
}
