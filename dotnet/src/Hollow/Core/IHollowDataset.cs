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
using Hollow.Core.Schema;

namespace Hollow.Core;

/// <summary>
/// A set of strongly typed schemas describing a dataset.
/// </summary>
/// <remarks>
/// Named <c>HollowDataset</c> in Java; the <c>I</c> prefix follows the .NET interface naming
/// convention.
/// </remarks>
public interface IHollowDataset
{
    /// <summary>The schemas for all types in this dataset.</summary>
    IReadOnlyList<HollowSchema> Schemas { get; }

    /// <summary>
    /// Gets the schema for <paramref name="typeName"/>, or <see langword="null"/> when this dataset has
    /// no such type.
    /// </summary>
    HollowSchema? GetSchema(string typeName);

    /// <summary>
    /// Gets the schema for <paramref name="typeName"/>.
    /// </summary>
    /// <exception cref="SchemaNotFoundException">This dataset has no such type.</exception>
    HollowSchema GetNonNullSchema(string typeName);
}

/// <summary>
/// Comparisons over any <see cref="IHollowDataset"/>.
/// </summary>
/// <remarks>
/// Java declares this as a <c>default</c> method on the <c>HollowDataset</c> interface. It is an
/// extension method here so that it remains callable on concrete types: a C# default interface member
/// is only reachable through the interface, which would force a cast at every call site.
/// </remarks>
public static class HollowDatasetExtensions
{
    /// <summary>
    /// Returns whether two datasets have an identical set of schemas.
    /// </summary>
    public static bool HasIdenticalSchemas(this IHollowDataset dataset, IHollowDataset other)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(other);

        IReadOnlyList<HollowSchema> theseSchemas = dataset.Schemas;
        if (theseSchemas.Count != other.Schemas.Count)
        {
            return false;
        }

        foreach (HollowSchema schema in theseSchemas)
        {
            if (!schema.Equals(other.GetSchema(schema.Name)))
            {
                return false;
            }
        }

        return true;
    }
}
