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

using Hollow.Api.Consumer;
using Hollow.Api.Consumer.Data;
using Hollow.Core.Read.DataAccess;
using Hollow.Core.Read.Engine;
using Hollow.Core.Schema;

namespace Hollow.Core.Types.Accessor;

/// <summary>
/// What a transition did to one of the scalar types every dataset shares — the <c>String</c> holding
/// every loose string, the <c>Integer</c> holding every loose integer, and the rest.
/// </summary>
/// <typeparam name="TValue">The CLR value a record of the type reads back as.</typeparam>
/// <remarks>
/// <para>
/// Java writes one of these per scalar type, each a dozen lines around a different call on the
/// generated API's retriever interface. Here, as with the scalar type APIs themselves, the shared part
/// is a generic base and the named classes are what is left — a type name and one read.
/// </para>
/// <para>
/// The key is <c>value</c>, the single field a scalar wrapper has, so "the same record" across a
/// transition means "the same value" and nothing ever reads as replaced: a scalar record whose value
/// changed is a different record. What these answer is which values arrived and which went away.
/// </para>
/// </remarks>
public abstract class HollowScalarDataAccessor<TValue> : HollowDataAccessor<TValue>
{
    /// <summary>The one field a scalar wrapper type has.</summary>
    protected const string ValueFieldName = "value";

    /// <summary>Reads the records of <paramref name="type"/> that <paramref name="consumer"/> holds.</summary>
    protected HollowScalarDataAccessor(HollowConsumer consumer, string type)
        : this(
            (consumer ?? throw new ArgumentNullException(nameof(consumer))).StateEngine
                ?? throw new InvalidOperationException("the consumer holds no data yet"),
            type)
    {
    }

    /// <summary>Reads the records of <paramref name="type"/> in <paramref name="stateEngine"/>.</summary>
    /// <exception cref="ArgumentException">
    /// The dataset has no such type, or it is not one of the scalar wrapper types.
    /// </exception>
    protected HollowScalarDataAccessor(HollowReadStateEngine stateEngine, string type)
        : base(stateEngine, type, ValueFieldName)
    {
        ArgumentNullException.ThrowIfNull(stateEngine);

        if (stateEngine.GetTypeDataAccess(type) is not IHollowObjectTypeDataAccess dataAccess)
        {
            throw new ArgumentException($"{type} is not an object type of this dataset", nameof(type));
        }

        ValueFieldPosition = ((HollowObjectSchema)dataAccess.Schema).GetPosition(ValueFieldName);

        if (ValueFieldPosition == -1)
        {
            throw new ArgumentException(
                $"{type} has no {ValueFieldName} field, so it is not one of the scalar types", nameof(type));
        }

        DataAccess = dataAccess;
    }

    /// <summary>Where the records are read from.</summary>
    protected IHollowObjectTypeDataAccess DataAccess { get; }

    /// <summary>Which field of the record the value is, which is always the only one.</summary>
    protected int ValueFieldPosition { get; }
}

/// <summary>The dataset's shared <c>String</c> type.</summary>
public sealed class StringDataAccessor : HollowScalarDataAccessor<string?>
{
    /// <summary>The Hollow type this reads.</summary>
    public const string TypeName = "String";

    /// <summary>Reads what <paramref name="consumer"/> holds.</summary>
    public StringDataAccessor(HollowConsumer consumer)
        : base(consumer, TypeName)
    {
    }

    /// <summary>Reads what <paramref name="stateEngine"/> holds.</summary>
    public StringDataAccessor(HollowReadStateEngine stateEngine)
        : base(stateEngine, TypeName)
    {
    }

    /// <inheritdoc />
    public override string? GetRecord(int ordinal) => DataAccess.ReadString(ordinal, ValueFieldPosition);
}

/// <summary>The dataset's shared <c>Integer</c> type.</summary>
public sealed class IntegerDataAccessor : HollowScalarDataAccessor<int?>
{
    /// <summary>The Hollow type this reads.</summary>
    public const string TypeName = "Integer";

    /// <summary>Reads what <paramref name="consumer"/> holds.</summary>
    public IntegerDataAccessor(HollowConsumer consumer)
        : base(consumer, TypeName)
    {
    }

    /// <summary>Reads what <paramref name="stateEngine"/> holds.</summary>
    public IntegerDataAccessor(HollowReadStateEngine stateEngine)
        : base(stateEngine, TypeName)
    {
    }

    /// <inheritdoc />
    public override int? GetRecord(int ordinal) =>
        DataAccess.IsNull(ordinal, ValueFieldPosition)
            ? null
            : DataAccess.ReadInt(ordinal, ValueFieldPosition);
}

/// <summary>The dataset's shared <c>Long</c> type.</summary>
public sealed class LongDataAccessor : HollowScalarDataAccessor<long?>
{
    /// <summary>The Hollow type this reads.</summary>
    public const string TypeName = "Long";

    /// <summary>Reads what <paramref name="consumer"/> holds.</summary>
    public LongDataAccessor(HollowConsumer consumer)
        : base(consumer, TypeName)
    {
    }

    /// <summary>Reads what <paramref name="stateEngine"/> holds.</summary>
    public LongDataAccessor(HollowReadStateEngine stateEngine)
        : base(stateEngine, TypeName)
    {
    }

    /// <inheritdoc />
    public override long? GetRecord(int ordinal) =>
        DataAccess.IsNull(ordinal, ValueFieldPosition)
            ? null
            : DataAccess.ReadLong(ordinal, ValueFieldPosition);
}

/// <summary>The dataset's shared <c>Float</c> type.</summary>
public sealed class FloatDataAccessor : HollowScalarDataAccessor<float?>
{
    /// <summary>The Hollow type this reads.</summary>
    public const string TypeName = "Float";

    /// <summary>Reads what <paramref name="consumer"/> holds.</summary>
    public FloatDataAccessor(HollowConsumer consumer)
        : base(consumer, TypeName)
    {
    }

    /// <summary>Reads what <paramref name="stateEngine"/> holds.</summary>
    public FloatDataAccessor(HollowReadStateEngine stateEngine)
        : base(stateEngine, TypeName)
    {
    }

    /// <inheritdoc />
    public override float? GetRecord(int ordinal) =>
        DataAccess.IsNull(ordinal, ValueFieldPosition)
            ? null
            : DataAccess.ReadFloat(ordinal, ValueFieldPosition);
}

/// <summary>The dataset's shared <c>Double</c> type.</summary>
public sealed class DoubleDataAccessor : HollowScalarDataAccessor<double?>
{
    /// <summary>The Hollow type this reads.</summary>
    public const string TypeName = "Double";

    /// <summary>Reads what <paramref name="consumer"/> holds.</summary>
    public DoubleDataAccessor(HollowConsumer consumer)
        : base(consumer, TypeName)
    {
    }

    /// <summary>Reads what <paramref name="stateEngine"/> holds.</summary>
    public DoubleDataAccessor(HollowReadStateEngine stateEngine)
        : base(stateEngine, TypeName)
    {
    }

    /// <inheritdoc />
    public override double? GetRecord(int ordinal) =>
        DataAccess.IsNull(ordinal, ValueFieldPosition)
            ? null
            : DataAccess.ReadDouble(ordinal, ValueFieldPosition);
}

/// <summary>The dataset's shared <c>Boolean</c> type.</summary>
public sealed class BooleanDataAccessor : HollowScalarDataAccessor<bool?>
{
    /// <summary>The Hollow type this reads.</summary>
    public const string TypeName = "Boolean";

    /// <summary>Reads what <paramref name="consumer"/> holds.</summary>
    public BooleanDataAccessor(HollowConsumer consumer)
        : base(consumer, TypeName)
    {
    }

    /// <summary>Reads what <paramref name="stateEngine"/> holds.</summary>
    public BooleanDataAccessor(HollowReadStateEngine stateEngine)
        : base(stateEngine, TypeName)
    {
    }

    /// <inheritdoc />
    public override bool? GetRecord(int ordinal) => DataAccess.ReadBoolean(ordinal, ValueFieldPosition);
}

/// <summary>
/// The dataset's shared <c>Decimal</c> type.
/// </summary>
/// <remarks>
/// Java has no such accessor, because it has no such type — see the format extension section of
/// <c>PORTING.md</c>. It is here so that the seven scalar types this port has are covered by seven
/// accessors rather than six.
/// </remarks>
public sealed class DecimalDataAccessor : HollowScalarDataAccessor<decimal?>
{
    /// <summary>The Hollow type this reads.</summary>
    public const string TypeName = "Decimal";

    /// <summary>Reads what <paramref name="consumer"/> holds.</summary>
    public DecimalDataAccessor(HollowConsumer consumer)
        : base(consumer, TypeName)
    {
    }

    /// <summary>Reads what <paramref name="stateEngine"/> holds.</summary>
    public DecimalDataAccessor(HollowReadStateEngine stateEngine)
        : base(stateEngine, TypeName)
    {
    }

    /// <inheritdoc />
    public override decimal? GetRecord(int ordinal) => DataAccess.ReadDecimal(ordinal, ValueFieldPosition);
}
