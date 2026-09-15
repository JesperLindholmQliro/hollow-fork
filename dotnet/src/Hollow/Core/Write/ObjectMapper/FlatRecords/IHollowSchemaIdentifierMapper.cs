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

using Hollow.Core.Index.Key;
using Hollow.Core.Schema;

namespace Hollow.Core.Write.ObjectMapper.FlatRecords;

/// <summary>
/// Turns a schema into the number a flat record names it by, and back again.
/// </summary>
/// <remarks>
/// <para>
/// A flat record carries no schemas of its own — a record that did would be mostly schema. It names
/// each one by a number, and whoever reads the record has to agree with whoever wrote it about what
/// those numbers mean. That agreement is this interface, and it is the one thing a caller has to
/// provide.
/// </para>
/// <para>
/// Named <c>HollowSchemaIdentifierMapper</c> in Java, where it has no implementation at all outside
/// the tests: Netflix's own registry lives elsewhere, so the feature cannot be used as shipped.
/// <see cref="HollowDatasetSchemaIdentifierMapper"/> is this port's answer to that — good enough for
/// two processes that share a data model, and the thing to replace when they do not.
/// </para>
/// </remarks>
public interface IHollowSchemaIdentifierMapper
{
    /// <summary>The schema <paramref name="identifier"/> names, or null if it names none.</summary>
    HollowSchema? GetSchema(int identifier);

    /// <summary>
    /// The field types of the primary key of the type <paramref name="identifier"/> names, in key
    /// order, or an empty array where the type declares no key.
    /// </summary>
    /// <remarks>
    /// Kept separate from the schema because a key may step through references, and the flat record
    /// stores each key field's value rather than the path to it.
    /// </remarks>
    FieldType[] GetPrimaryKeyFieldTypes(int identifier);

    /// <summary>The number <paramref name="schema"/> is named by.</summary>
    int GetSchemaId(HollowSchema schema);
}

/// <summary>
/// Names each of a dataset's schemas by where it appears in that dataset.
/// </summary>
/// <remarks>
/// <para>
/// Java ships no implementation of <see cref="IHollowSchemaIdentifierMapper"/>, which leaves flat
/// records unusable without writing one. This is the obvious one: both ends agree on a data model, so
/// both ends can agree to number its schemas in the order the model declares them.
/// </para>
/// <para>
/// <strong>What that costs.</strong> The numbers move when the model changes — adding a type in the
/// middle renumbers everything after it — so a flat record written against one version of a model
/// cannot be read against another. That is fine for a record handed straight from one process to
/// another, which is what flat records are for, and wrong for one written to storage and read back
/// later. For that, keep a registry that never reuses or reorders a number, and implement the
/// interface over it.
/// </para>
/// </remarks>
public sealed class HollowDatasetSchemaIdentifierMapper : IHollowSchemaIdentifierMapper
{
    private readonly IReadOnlyList<HollowSchema> _schemas;
    private readonly Dictionary<string, int> _idsByName;
    private readonly FieldType[][] _primaryKeyFieldTypes;

    /// <summary>Numbers the schemas of <paramref name="dataset"/> in the order it declares them.</summary>
    public HollowDatasetSchemaIdentifierMapper(IHollowDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        _schemas = dataset.Schemas;
        _idsByName = new Dictionary<string, int>(_schemas.Count, StringComparer.Ordinal);
        _primaryKeyFieldTypes = new FieldType[_schemas.Count][];

        for (int id = 0; id < _schemas.Count; id++)
        {
            _idsByName[_schemas[id].Name] = id;

            _primaryKeyFieldTypes[id] = _schemas[id] is HollowObjectSchema { PrimaryKey: { } key }
                ? [.. Enumerable.Range(0, key.FieldCount).Select(field => key.GetFieldType(dataset, field))]
                : [];
        }
    }

    /// <inheritdoc />
    public HollowSchema? GetSchema(int identifier) =>
        identifier >= 0 && identifier < _schemas.Count ? _schemas[identifier] : null;

    /// <inheritdoc />
    public FieldType[] GetPrimaryKeyFieldTypes(int identifier) =>
        identifier >= 0 && identifier < _primaryKeyFieldTypes.Length
            ? _primaryKeyFieldTypes[identifier]
            : [];

    /// <inheritdoc />
    public int GetSchemaId(HollowSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        return _idsByName.TryGetValue(schema.Name, out int id)
            ? id
            : throw new ArgumentException(
                $"{schema.Name} is not a type of the dataset these identifiers were taken from",
                nameof(schema));
    }
}
