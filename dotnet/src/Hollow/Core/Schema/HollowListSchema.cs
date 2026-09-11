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

using Hollow.Core.Read.Engine;
using Hollow.Core.Write;

namespace Hollow.Core.Schema;

/// <summary>
/// The schema of a List record type.
/// </summary>
/// <seealso cref="HollowSchema" />
public sealed class HollowListSchema : HollowCollectionSchema
{
    /// <summary>
    /// Initialises a list schema whose elements are records of <paramref name="elementType"/>.
    /// </summary>
    public HollowListSchema(string schemaName, string elementType)
        : base(schemaName)
    {
        ElementType = elementType;
    }

    /// <inheritdoc />
    public override string ElementType { get; }

    /// <inheritdoc />
    public override HollowTypeReadState? ElementTypeState { get; set; }

    /// <inheritdoc />
    public override SchemaType SchemaType => SchemaType.List;

    /// <inheritdoc />
    public override void WriteTo(HollowBlobOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);

        output.WriteByte(SchemaType.List.GetTypeId());
        output.WriteUtf(Name);
        output.WriteUtf(ElementType);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        ReferenceEquals(this, obj)
        || (obj is HollowListSchema other && Name == other.Name && ElementType == other.ElementType);

    /// <inheritdoc />
    public override int GetHashCode() => System.HashCode.Combine(Name, SchemaType, ElementType);

    /// <inheritdoc />
    public override string ToString() => $"{Name} List<{ElementType}>;";
}
