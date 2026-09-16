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

using Hollow.Core.Read.Filter;
using Hollow.Core.Schema;

namespace Hollow.Api.Sampling;

/// <summary>
/// Counts reads of one object type, a counter per field.
/// </summary>
/// <remarks>
/// <para>
/// The counters are plain increments rather than interlocked ones, as Java's are. A read of a field is
/// about as cheap as an operation gets, so an interlocked increment on every one would cost more than
/// the reads being measured — and a sample count that is short by a few out of millions answers the
/// question just as well.
/// </para>
/// <para>
/// <see cref="RecordFieldAccess"/> checks one field before touching a director, so a sampler nobody has
/// enabled costs a predictable branch per read and nothing else.
/// </para>
/// </remarks>
public sealed class HollowObjectSampler : IHollowSampler
{
    private readonly string _typeName;
    private readonly string[] _fieldNames;
    private readonly long[] _sampleCounts;
    private readonly HollowSamplingDirector[] _directors;
    private readonly bool _isNull;

    private bool _isSamplingDisabled;

    /// <summary>Counts reads of <paramref name="schema"/>'s fields under <paramref name="director"/>.</summary>
    public HollowObjectSampler(HollowObjectSchema schema, HollowSamplingDirector director)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(director);

        _typeName = schema.Name;
        _fieldNames = [.. Enumerable.Range(0, schema.FieldCount).Select(schema.GetFieldName)];
        _sampleCounts = new long[schema.FieldCount];
        _directors = [.. Enumerable.Repeat(director, schema.FieldCount)];
        _isSamplingDisabled = ReferenceEquals(director, DisabledSamplingDirector.Instance);
    }

    private HollowObjectSampler()
    {
        _typeName = string.Empty;
        _fieldNames = [];
        _sampleCounts = [];
        _directors = [];
        _isNull = true;
        _isSamplingDisabled = true;
    }

    /// <summary>
    /// A sampler for a type state that has no type, which counts nothing and cannot be turned on.
    /// </summary>
    /// <remarks>
    /// Java calls this <c>NULL_SAMPLER</c> and marks it by an empty type name, which the list, set and
    /// map samplers then test for. The object one is built from a schema named <c>test</c> instead, so
    /// that test never fires and its director is replaceable — harmless only because the schema has no
    /// fields to count. A flag says what is meant, for all four.
    /// </remarks>
    public static HollowObjectSampler Null { get; } = new();

    /// <inheritdoc />
    public bool HasSampleResults => Array.Exists(_sampleCounts, count => count > 0);

    /// <summary>Counts one read of the field at <paramref name="fieldPosition"/>.</summary>
    public void RecordFieldAccess(int fieldPosition)
    {
        if (_isSamplingDisabled)
        {
            return;
        }

        if (_directors[fieldPosition].ShouldRecord())
        {
            _sampleCounts[fieldPosition]++;
        }
    }

    /// <inheritdoc />
    public void SetSamplingDirector(HollowSamplingDirector director)
    {
        ArgumentNullException.ThrowIfNull(director);

        if (_isNull)
        {
            return;
        }

        _isSamplingDisabled = ReferenceEquals(director, DisabledSamplingDirector.Instance);

        Array.Fill(_directors, director);
    }

    /// <inheritdoc />
    public void SetFieldSpecificSamplingDirector(ITypeFilter fieldSpec, HollowSamplingDirector director)
    {
        ArgumentNullException.ThrowIfNull(fieldSpec);
        ArgumentNullException.ThrowIfNull(director);

        if (_isNull)
        {
            return;
        }

        for (int i = 0; i < _fieldNames.Length; i++)
        {
            if (fieldSpec.Includes(_typeName, _fieldNames[i]))
            {
                _isSamplingDisabled = false;
                _directors[i] = director;
            }
        }
    }

    /// <inheritdoc />
    public void SetUpdateThread(Thread? thread)
    {
        foreach (HollowSamplingDirector director in _directors)
        {
            director.SetUpdateThread(thread);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<SampleResult> GetSampleResults() =>
        [.. _fieldNames.Select((name, i) => new SampleResult($"{_typeName}.{name}", _sampleCounts[i]))];

    /// <inheritdoc />
    public void Reset() => Array.Clear(_sampleCounts);
}

/// <summary>
/// Counts the three things one can do with a collection record: ask its size, read an element, walk it.
/// </summary>
/// <remarks>
/// Java writes <c>HollowListSampler</c> and <c>HollowSetSampler</c> out twice, identically, and
/// <c>HollowMapSampler</c> a third time with one extra counter. The shared part lives here instead, and
/// the three names below stay so that each read state still names the sampler it holds.
/// </remarks>
public abstract class HollowCollectionSampler : IHollowSampler
{
    private readonly string _typeName;
    private readonly bool _isNull;

    private HollowSamplingDirector _director;

    private long _sizeSamples;
    private long _getSamples;
    private long _iteratorSamples;

    /// <summary>Counts reads of <paramref name="typeName"/> under <paramref name="director"/>.</summary>
    protected HollowCollectionSampler(string typeName, HollowSamplingDirector director)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        ArgumentNullException.ThrowIfNull(director);

        _typeName = typeName;
        _director = director;
    }

    /// <summary>Creates a sampler that counts nothing and cannot be turned on.</summary>
    private protected HollowCollectionSampler()
    {
        _typeName = string.Empty;
        _director = DisabledSamplingDirector.Instance;
        _isNull = true;
    }

    /// <inheritdoc />
    public virtual bool HasSampleResults => _sizeSamples > 0 || _getSamples > 0 || _iteratorSamples > 0;

    /// <summary>The type whose reads are counted.</summary>
    protected string TypeName => _typeName;

    /// <summary>The director deciding which reads to count.</summary>
    protected HollowSamplingDirector Director => _director;

    /// <summary>Counts one read of the record's size.</summary>
    public void RecordSize()
    {
        if (_director.ShouldRecord())
        {
            _sizeSamples++;
        }
    }

    /// <summary>Counts one read of an element.</summary>
    public void RecordGet()
    {
        if (_director.ShouldRecord())
        {
            _getSamples++;
        }
    }

    /// <summary>Counts one walk of the record.</summary>
    public void RecordIterator()
    {
        if (_director.ShouldRecord())
        {
            _iteratorSamples++;
        }
    }

    /// <inheritdoc />
    public void SetSamplingDirector(HollowSamplingDirector director)
    {
        ArgumentNullException.ThrowIfNull(director);

        if (!_isNull)
        {
            _director = director;
        }
    }

    /// <inheritdoc />
    public void SetFieldSpecificSamplingDirector(ITypeFilter fieldSpec, HollowSamplingDirector director)
    {
        ArgumentNullException.ThrowIfNull(fieldSpec);
        ArgumentNullException.ThrowIfNull(director);

        if (!_isNull && fieldSpec.Includes(_typeName))
        {
            _director = director;
        }
    }

    /// <inheritdoc />
    public void SetUpdateThread(Thread? thread) => _director.SetUpdateThread(thread);

    /// <inheritdoc />
    public virtual IReadOnlyList<SampleResult> GetSampleResults() =>
    [
        new SampleResult($"{_typeName}.Count", _sizeSamples),
        new SampleResult($"{_typeName}.Get()", _getSamples),
        new SampleResult($"{_typeName}.Enumerate()", _iteratorSamples),
    ];

    /// <inheritdoc />
    public virtual void Reset()
    {
        _sizeSamples = 0;
        _getSamples = 0;
        _iteratorSamples = 0;
    }
}

/// <summary>Counts reads of one list type.</summary>
public sealed class HollowListSampler : HollowCollectionSampler
{
    /// <summary>Counts reads of <paramref name="typeName"/> under <paramref name="director"/>.</summary>
    public HollowListSampler(string typeName, HollowSamplingDirector director)
        : base(typeName, director)
    {
    }

    private HollowListSampler()
    {
    }

    /// <summary>A sampler for a type state that has no type, which counts nothing.</summary>
    public static HollowListSampler Null { get; } = new();
}

/// <summary>Counts reads of one set type.</summary>
public sealed class HollowSetSampler : HollowCollectionSampler
{
    /// <summary>Counts reads of <paramref name="typeName"/> under <paramref name="director"/>.</summary>
    public HollowSetSampler(string typeName, HollowSamplingDirector director)
        : base(typeName, director)
    {
    }

    private HollowSetSampler()
    {
    }

    /// <summary>A sampler for a type state that has no type, which counts nothing.</summary>
    public static HollowSetSampler Null { get; } = new();
}

/// <summary>
/// Counts reads of one map type, including the bucket reads a key lookup makes.
/// </summary>
/// <remarks>
/// A lookup that reads many buckets to find one key is a hash key doing badly, which is worth seeing
/// separately from the lookup itself.
/// </remarks>
public sealed class HollowMapSampler : HollowCollectionSampler
{
    private long _bucketRetrievalSamples;

    /// <summary>Counts reads of <paramref name="typeName"/> under <paramref name="director"/>.</summary>
    public HollowMapSampler(string typeName, HollowSamplingDirector director)
        : base(typeName, director)
    {
    }

    private HollowMapSampler()
    {
    }

    /// <summary>A sampler for a type state that has no type, which counts nothing.</summary>
    public static HollowMapSampler Null { get; } = new();

    /// <inheritdoc />
    public override bool HasSampleResults => base.HasSampleResults || _bucketRetrievalSamples > 0;

    /// <summary>Counts one bucket read.</summary>
    public void RecordBucketRetrieval()
    {
        if (Director.ShouldRecord())
        {
            _bucketRetrievalSamples++;
        }
    }

    /// <inheritdoc />
    public override IReadOnlyList<SampleResult> GetSampleResults() =>
        [.. base.GetSampleResults(), new SampleResult($"{TypeName}.BucketValue()", _bucketRetrievalSamples)];

    /// <inheritdoc />
    public override void Reset()
    {
        base.Reset();

        _bucketRetrievalSamples = 0;
    }
}

/// <summary>
/// Counts how many record wrappers a generated API hands out, per type.
/// </summary>
/// <remarks>
/// Not a read count but an allocation count: it says which types a caller is materialising, which is
/// what decides whether caching them or moving to the performance API would pay.
/// </remarks>
public sealed class HollowObjectCreationSampler : IHollowSampler
{
    private readonly string[] _typeNames;
    private readonly long[] _creationSamples;
    private readonly HollowSamplingDirector[] _directors;

    /// <summary>Counts creations of each of <paramref name="typeNames"/>.</summary>
    public HollowObjectCreationSampler(params string[] typeNames)
    {
        ArgumentNullException.ThrowIfNull(typeNames);

        _typeNames = typeNames;
        _creationSamples = new long[typeNames.Length];
        _directors = [.. Enumerable.Repeat(
            (HollowSamplingDirector)DisabledSamplingDirector.Instance, typeNames.Length)];
    }

    /// <inheritdoc />
    public bool HasSampleResults => Array.Exists(_creationSamples, count => count > 0);

    /// <summary>Counts one creation of the type at <paramref name="index"/>.</summary>
    public void RecordCreation(int index)
    {
        if (_directors[index].ShouldRecord())
        {
            _creationSamples[index]++;
        }
    }

    /// <inheritdoc />
    public void SetSamplingDirector(HollowSamplingDirector director)
    {
        ArgumentNullException.ThrowIfNull(director);

        Array.Fill(_directors, director);
    }

    /// <inheritdoc />
    public void SetFieldSpecificSamplingDirector(ITypeFilter fieldSpec, HollowSamplingDirector director)
    {
        ArgumentNullException.ThrowIfNull(fieldSpec);
        ArgumentNullException.ThrowIfNull(director);

        for (int i = 0; i < _typeNames.Length; i++)
        {
            if (fieldSpec.Includes(_typeNames[i]))
            {
                _directors[i] = director;
            }
        }
    }

    /// <inheritdoc />
    public void SetUpdateThread(Thread? thread)
    {
        foreach (HollowSamplingDirector director in _directors)
        {
            director.SetUpdateThread(thread);
        }
    }

    /// <inheritdoc />
    /// <remarks>Sorted, so that the type being materialised most comes first.</remarks>
    public IReadOnlyList<SampleResult> GetSampleResults()
    {
        SampleResult[] results =
            [.. _typeNames.Select((name, i) => new SampleResult(name, _creationSamples[i]))];

        Array.Sort(results);

        return results;
    }

    /// <inheritdoc />
    public void Reset() => Array.Clear(_creationSamples);
}

/// <summary>
/// A sampler for something that has no reads to count.
/// </summary>
/// <remarks>
/// What a data access that answers from somewhere other than a type state reports — a missing type, a
/// disabled one, a historical one. Java gives each of those the null sampler of its own record kind;
/// one kind-agnostic instance says the same thing once.
/// </remarks>
public sealed class NullSampler : IHollowSampler
{
    private NullSampler()
    {
    }

    /// <summary>The one instance.</summary>
    public static NullSampler Instance { get; } = new();

    /// <inheritdoc />
    public bool HasSampleResults => false;

    /// <inheritdoc />
    public void SetSamplingDirector(HollowSamplingDirector director)
    {
    }

    /// <inheritdoc />
    public void SetFieldSpecificSamplingDirector(ITypeFilter fieldSpec, HollowSamplingDirector director)
    {
    }

    /// <inheritdoc />
    public void SetUpdateThread(Thread? thread)
    {
    }

    /// <inheritdoc />
    public IReadOnlyList<SampleResult> GetSampleResults() => [];

    /// <inheritdoc />
    public void Reset()
    {
    }
}
