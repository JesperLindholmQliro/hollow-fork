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

using Hollow.Core.Schema;
using Hollow.Core.Write;

namespace Hollow.Api.TestData;

/// <summary>An object record being built by hand, field by field.</summary>
/// <typeparam name="TParent">What <see cref="HollowTestRecord{TParent}.Up"/> returns.</typeparam>
public abstract class HollowTestObjectRecord<TParent>(TParent parent)
    : HollowTestRecord<TParent>(parent)
{
    private readonly Dictionary<string, object> _fields = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public abstract override HollowObjectSchema Schema { get; }

    /// <summary>
    /// Sets a field, or clears it where <paramref name="value"/> is <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// A null clears rather than storing a null, so that describing a record and then overriding one
    /// of its fields with nothing leaves the field unset — which is what a Hollow null is.
    /// </remarks>
    protected void SetField(string fieldName, object? value)
    {
        ArgumentNullException.ThrowIfNull(fieldName);

        if (value is null)
        {
            _fields.Remove(fieldName);
        }
        else
        {
            _fields[fieldName] = value;
        }
    }

    /// <summary>What a field is set to, or <see langword="null"/> where it is unset.</summary>
    protected object? GetField(string fieldName) => _fields.GetValueOrDefault(fieldName);

    /// <inheritdoc />
    protected override IHollowWriteRecord ToWriteRecord(HollowWriteStateEngine writeEngine)
    {
        HollowObjectWriteRecord record = new(Schema);

        foreach ((string name, object value) in _fields)
        {
            switch (value)
            {
                case int number:
                    record.SetInt(name, number);
                    break;

                case long number:
                    record.SetLong(name, number);
                    break;

                case float number:
                    record.SetFloat(name, number);
                    break;

                // Java has no case for a double, so a model with one cannot be built with its test
                // data at all — the chain falls through and throws "Unknown field type".
                case double number:
                    record.SetDouble(name, number);
                    break;

                // This port's own field type, which Java has no name for.
                case decimal number:
                    record.SetDecimal(name, number);
                    break;

                case bool flag:
                    record.SetBoolean(name, flag);
                    break;

                case string text:
                    record.SetString(name, text);
                    break;

                case char character:
                    record.SetString(name, character.ToString());
                    break;

                case byte[] bytes:
                    record.SetBytes(name, bytes);
                    break;

                case IHollowTestRecord referenced:
                    record.SetReference(name, referenced.AddTo(writeEngine));
                    break;

                default:
                    throw new InvalidOperationException(
                        $"field {name} of {Schema.Name} is a {value.GetType().Name}, which is not a Hollow field type");
            }
        }

        return record;
    }
}

/// <summary>
/// An object record of a schema given at construction, needing no subclass.
/// </summary>
/// <remarks>
/// Java has no equivalent: every test-data record there is a generated subclass, so a test without a
/// generator has nothing to use. This is the same thing with the schema passed in, which is all a
/// generated subclass adds beyond its typed setters.
/// </remarks>
/// <typeparam name="TParent">What <see cref="HollowTestRecord{TParent}.Up"/> returns.</typeparam>
public sealed class HollowTestObject<TParent>(TParent parent, HollowObjectSchema schema)
    : HollowTestObjectRecord<TParent>(parent)
{
    /// <inheritdoc />
    public override HollowObjectSchema Schema { get; } =
        schema ?? throw new ArgumentNullException(nameof(schema));

    /// <summary>Sets a field, and returns this record so the calls chain.</summary>
    public HollowTestObject<TParent> With(string fieldName, object? value)
    {
        SetField(fieldName, value);

        return this;
    }
}

/// <summary>A list record being built by hand.</summary>
/// <typeparam name="TParent">What <see cref="HollowTestRecord{TParent}.Up"/> returns.</typeparam>
public abstract class HollowTestListRecord<TParent>(TParent parent)
    : HollowTestRecord<TParent>(parent)
{
    private readonly List<IHollowTestRecord> _elements = [];

    /// <summary>How many elements the list holds.</summary>
    public int Count => _elements.Count;

    /// <summary>The element at <paramref name="index"/>.</summary>
    public IHollowTestRecord this[int index] => _elements[index];

    /// <summary>Appends an element.</summary>
    protected void AddElement(IHollowTestRecord element)
    {
        ArgumentNullException.ThrowIfNull(element);

        _elements.Add(element);
    }

    /// <inheritdoc />
    protected override IHollowWriteRecord ToWriteRecord(HollowWriteStateEngine writeEngine)
    {
        HollowListWriteRecord record = new();

        foreach (IHollowTestRecord element in _elements)
        {
            record.AddElement(element.AddTo(writeEngine));
        }

        return record;
    }
}

/// <summary>A set record being built by hand.</summary>
/// <remarks>
/// The elements are kept in the order they were added, which Java's <c>HashSet</c> does not. Two
/// identical elements still collapse — they take the same ordinal — so the only thing that changes is
/// that a test producing a set twice produces the same bytes twice.
/// </remarks>
/// <typeparam name="TParent">What <see cref="HollowTestRecord{TParent}.Up"/> returns.</typeparam>
public abstract class HollowTestSetRecord<TParent>(TParent parent)
    : HollowTestRecord<TParent>(parent)
{
    private readonly List<IHollowTestRecord> _elements = [];

    /// <summary>How many elements the set holds.</summary>
    public int Count => _elements.Count;

    /// <summary>Adds an element.</summary>
    protected void AddElement(IHollowTestRecord element)
    {
        ArgumentNullException.ThrowIfNull(element);

        _elements.Add(element);
    }

    /// <inheritdoc />
    protected override IHollowWriteRecord ToWriteRecord(HollowWriteStateEngine writeEngine)
    {
        HollowSetWriteRecord record = new();

        foreach (IHollowTestRecord element in _elements)
        {
            record.AddElement(element.AddTo(writeEngine));
        }

        return record;
    }
}

/// <summary>One key and value of a map record being built by hand.</summary>
public readonly record struct HollowTestMapEntry(IHollowTestRecord Key, IHollowTestRecord Value);

/// <summary>A map record being built by hand.</summary>
/// <typeparam name="TParent">What <see cref="HollowTestRecord{TParent}.Up"/> returns.</typeparam>
public abstract class HollowTestMapRecord<TParent>(TParent parent)
    : HollowTestRecord<TParent>(parent)
{
    private readonly List<HollowTestMapEntry> _entries = [];

    /// <summary>How many entries the map holds.</summary>
    public int Count => _entries.Count;

    /// <summary>The entry at <paramref name="index"/>.</summary>
    /// <remarks>
    /// Java makes its entry a <c>HollowTestRecord</c> subclass whose <c>getSchema</c> and
    /// <c>toWriteRecord</c> both throw, because an entry is not a record and has no schema of its
    /// own. A pair is a pair.
    /// </remarks>
    public HollowTestMapEntry this[int index] => _entries[index];

    /// <summary>Adds an entry.</summary>
    protected void AddEntry(IHollowTestRecord key, IHollowTestRecord value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        _entries.Add(new HollowTestMapEntry(key, value));
    }

    /// <inheritdoc />
    protected override IHollowWriteRecord ToWriteRecord(HollowWriteStateEngine writeEngine)
    {
        HollowMapWriteRecord record = new();

        foreach ((IHollowTestRecord key, IHollowTestRecord value) in _entries)
        {
            record.AddEntry(key.AddTo(writeEngine), value.AddTo(writeEngine));
        }

        return record;
    }
}
