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
using Hollow.Core.Index.Key;
using Hollow.Core.Memory.Encoding;
using Hollow.Core.Read.Engine;
using Hollow.Core.Write;

namespace Hollow.Core.Schema;

/// <summary>
/// The schema of a Map record type.
/// </summary>
/// <seealso cref="HollowSchema" />
public sealed class HollowMapSchema : HollowSchema
{
    /// <summary>
    /// Initialises a map schema from <paramref name="keyType"/> to <paramref name="valueType"/>,
    /// optionally hashed by <paramref name="hashKeyFieldPaths"/>.
    /// </summary>
    public HollowMapSchema(
        string schemaName, string keyType, string valueType, params string[]? hashKeyFieldPaths)
        : base(schemaName)
    {
        KeyType = keyType;
        ValueType = valueType;
        HashKey = hashKeyFieldPaths is null || hashKeyFieldPaths.Length == 0
            ? null
            : new PrimaryKey(keyType, hashKeyFieldPaths);
    }

    /// <summary>The name of the type of this map's keys.</summary>
    public string KeyType { get; }

    /// <summary>The name of the type of this map's values.</summary>
    public string ValueType { get; }

    /// <summary>The key entries are hashed by, or <see langword="null"/> for identity hashing.</summary>
    public PrimaryKey? HashKey { get; }

    /// <summary>The read state of this map's key type, populated during deserialisation.</summary>
    public HollowTypeReadState? KeyTypeState { get; set; }

    /// <summary>The read state of this map's value type, populated during deserialisation.</summary>
    public HollowTypeReadState? ValueTypeState { get; set; }

    /// <inheritdoc />
    public override SchemaType SchemaType => SchemaType.Map;

    /// <inheritdoc />
    public override void WriteTo(HollowBlobOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);

        output.WriteByte(HashKey is null
            ? SchemaType.Map.GetTypeId()
            : SchemaType.Map.GetTypeIdWithPrimaryKey());

        output.WriteUtf(Name);
        output.WriteUtf(KeyType);
        output.WriteUtf(ValueType);

        if (HashKey is not null)
        {
            VarInt.WriteVInt(output, HashKey.FieldCount);
            for (int i = 0; i < HashKey.FieldCount; i++)
            {
                output.WriteUtf(HashKey.GetFieldPath(i));
            }
        }
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        ReferenceEquals(this, obj)
        || (obj is HollowMapSchema other
            && Name == other.Name
            && KeyType == other.KeyType
            && ValueType == other.ValueType
            && IsNullableObjectEquals(HashKey, other.HashKey));

    /// <inheritdoc />
    public override int GetHashCode() =>
        System.HashCode.Combine(Name, SchemaType, KeyType, ValueType, HashKey);

    /// <inheritdoc />
    public override string ToString()
    {
        StringBuilder builder = new(Name);
        builder.Append(" Map<").Append(KeyType).Append(',').Append(ValueType).Append('>');

        if (HashKey is not null)
        {
            builder.Append(" @HashKey(").AppendJoin(", ", HashKey.FieldPaths).Append(')');
        }

        return builder.Append(';').ToString();
    }
}
