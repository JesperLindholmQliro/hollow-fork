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

using Hollow.Api.Error;

namespace Hollow.Core.Schema;

/// <summary>
/// An <see cref="IHollowDataset"/> which only describes the set of schemas comprising a dataset.
/// </summary>
public sealed class SimpleHollowDataset : IHollowDataset
{
    private readonly Dictionary<string, HollowSchema> _schemas;

    /// <summary>
    /// Initialises a dataset from schemas keyed by type name.
    /// </summary>
    public SimpleHollowDataset(IReadOnlyDictionary<string, HollowSchema> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        _schemas = new Dictionary<string, HollowSchema>(schemas, StringComparer.Ordinal);
    }

    /// <summary>
    /// Initialises a dataset from a list of schemas.
    /// </summary>
    public SimpleHollowDataset(IEnumerable<HollowSchema> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        _schemas = schemas.ToDictionary(schema => schema.Name, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public IReadOnlyList<HollowSchema> Schemas => [.. _schemas.Values];

    /// <inheritdoc />
    public HollowSchema? GetSchema(string typeName) =>
        _schemas.GetValueOrDefault(typeName);

    /// <inheritdoc />
    public HollowSchema GetNonNullSchema(string typeName) =>
        GetSchema(typeName) ?? throw new SchemaNotFoundException(typeName, _schemas.Keys);
}
