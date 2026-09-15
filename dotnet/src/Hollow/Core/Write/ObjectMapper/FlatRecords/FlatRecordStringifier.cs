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
using Hollow.Core.Schema;
using Hollow.Core.Write.ObjectMapper.FlatRecords.Traversal;

namespace Hollow.Core.Write.ObjectMapper.FlatRecords;

/// <summary>
/// Writes a <see cref="FlatRecord"/> out as indented text, for a person to read.
/// </summary>
/// <remarks>
/// <para>
/// A one-field object prints as its value rather than as a record with a field: the wrapper types the
/// object mapper generates around a string or an int are an artefact of the model, and printing them
/// as records buries the data three lines deep in nothing.
/// </para>
/// <para>
/// Numbers are formatted invariantly, where Java's version uses the default locale — a dump whose
/// meaning changes with the machine that produced it is no use for comparing two of them.
/// </para>
/// </remarks>
public sealed class FlatRecordStringifier
{
    private const string Indent = "  ";

    private readonly HashSet<string> _excludedObjectTypes = new(StringComparer.Ordinal);

    /// <summary>
    /// Leaves the named object types out of what is printed, wherever they appear.
    /// </summary>
    /// <returns>This stringifier, so the calls chain.</returns>
    public FlatRecordStringifier ExcludeObjectTypes(params string[] typeNames)
    {
        ArgumentNullException.ThrowIfNull(typeNames);

        foreach (string typeName in typeNames)
        {
            _excludedObjectTypes.Add(typeName);
        }

        return this;
    }

    /// <summary>Writes out <paramref name="record"/>'s top record and everything below it.</summary>
    public string Stringify(FlatRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return Stringify(FlatRecordTraversal.From(record));
    }

    /// <summary>Writes out one node of a flat record and everything below it.</summary>
    public string Stringify(IFlatRecordTraversalNode? node)
    {
        StringBuilder text = new();

        Append(text, node, 0);

        return text.ToString();
    }

    private void Append(StringBuilder text, IFlatRecordTraversalNode? node, int depth)
    {
        switch (node)
        {
            case null:
                text.AppendLine("null");
                break;

            case FlatRecordTraversalObjectNode objectNode:
                AppendObject(text, objectNode, depth);
                break;

            case FlatRecordTraversalListNode listNode:
                AppendElements(text, listNode.Schema.Name, listNode, listNode.Count, depth);
                break;

            case FlatRecordTraversalSetNode setNode:
                AppendElements(text, setNode.Schema.Name, setNode, setNode.Count, depth);
                break;

            case FlatRecordTraversalMapNode mapNode:
                AppendMap(text, mapNode, depth);
                break;

            default:
                throw new InvalidOperationException($"unknown node {node.GetType().Name}");
        }
    }

    private void AppendObject(StringBuilder text, FlatRecordTraversalObjectNode node, int depth)
    {
        HollowObjectSchema schema = node.Schema;

        if (_excludedObjectTypes.Contains(schema.Name))
        {
            text.AppendLine();

            return;
        }

        // A wrapper around a single value is that value; anything else would print the model rather
        // than the data.
        if (schema.FieldCount == 1 && schema.GetFieldType(0) != FieldType.Reference)
        {
            AppendFieldValue(text, node, schema, 0);
            text.AppendLine();

            return;
        }

        text.Append('(').Append(schema.Name).Append(')').AppendLine();

        for (int field = 0; field < schema.FieldCount; field++)
        {
            AppendIndent(text, depth + 1);
            text.Append(schema.GetFieldName(field)).Append(": ");

            if (schema.GetFieldType(field) == FieldType.Reference)
            {
                Append(text, node.GetFieldNode(schema.GetFieldName(field)), depth + 1);
            }
            else
            {
                AppendFieldValue(text, node, schema, field);
                text.AppendLine();
            }
        }
    }

    private void AppendElements(
        StringBuilder text,
        string typeName,
        IEnumerable<IFlatRecordTraversalNode?> elements,
        int count,
        int depth)
    {
        text.Append('(').Append(typeName).Append(')').AppendLine();

        int index = 0;

        foreach (IFlatRecordTraversalNode? element in elements)
        {
            AppendIndent(text, depth + 1);
            text.Append(CultureInfo.InvariantCulture, $"e{index}: ");
            Append(text, element, depth + 1);
            index++;
        }

        if (count == 0)
        {
            AppendIndent(text, depth + 1);
            text.AppendLine("(empty)");
        }
    }

    private void AppendMap(StringBuilder text, FlatRecordTraversalMapNode node, int depth)
    {
        text.Append('(').Append(node.Schema.Name).Append(')').AppendLine();

        int index = 0;

        foreach (FlatRecordMapEntry entry in node)
        {
            AppendIndent(text, depth + 1);
            text.Append(CultureInfo.InvariantCulture, $"k{index}: ");
            Append(text, entry.Key, depth + 1);

            AppendIndent(text, depth + 1);
            text.Append(CultureInfo.InvariantCulture, $"v{index}: ");
            Append(text, entry.Value, depth + 1);

            index++;
        }

        if (node.Count == 0)
        {
            AppendIndent(text, depth + 1);
            text.AppendLine("(empty)");
        }
    }

    private static void AppendFieldValue(
        StringBuilder text, FlatRecordTraversalObjectNode node, HollowObjectSchema schema, int field)
    {
        string name = schema.GetFieldName(field);

        switch (schema.GetFieldType(field))
        {
            case FieldType.Boolean:
                text.Append(node.GetBoolean(name)?.ToString(CultureInfo.InvariantCulture) ?? "null");
                break;

            case FieldType.Int:
                text.Append(node.GetInt(name)?.ToString(CultureInfo.InvariantCulture) ?? "null");
                break;

            case FieldType.Long:
                text.Append(node.GetLong(name)?.ToString(CultureInfo.InvariantCulture) ?? "null");
                break;

            case FieldType.Float:
                text.Append(node.GetFloat(name)?.ToString(CultureInfo.InvariantCulture) ?? "null");
                break;

            case FieldType.Double:
                text.Append(node.GetDouble(name)?.ToString(CultureInfo.InvariantCulture) ?? "null");
                break;

            case FieldType.Decimal:
                text.Append(node.GetDecimal(name)?.ToString(CultureInfo.InvariantCulture) ?? "null");
                break;

            case FieldType.String:
                text.Append(node.GetString(name) ?? "null");
                break;

            case FieldType.Bytes:
                text.Append(node.GetBytes(name) is { } bytes ? Convert.ToHexString(bytes) : "null");
                break;

            default:
                throw new InvalidOperationException(
                    $"unknown field type {schema.GetFieldType(field)} on {schema.Name}.{name}");
        }
    }

    private static void AppendIndent(StringBuilder text, int depth) =>
        text.Insert(text.Length, Indent, depth);
}
