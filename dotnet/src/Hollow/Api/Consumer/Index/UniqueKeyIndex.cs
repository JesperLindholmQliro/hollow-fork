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

using System.Diagnostics.CodeAnalysis;
using Hollow.Api.Custom;
using Hollow.Api.Objects;
using Hollow.Core;
using Hollow.Core.Index;
using Hollow.Core.Index.Key;
using Hollow.Core.Read.Engine;
using Hollow.Core.Schema;

namespace Hollow.Api.Consumer.Index;

/// <summary>
/// Starts building a <see cref="UniqueKeyIndex{T, TKey}"/>.
/// </summary>
/// <remarks>
/// Java puts this as a static <c>from</c> on the index itself; a C# generic class cannot host a static
/// method with its own type parameters in a way that reads well, so the entry point is a separate
/// non-generic class.
/// </remarks>
public static class UniqueKeyIndex
{
    /// <summary>
    /// Indexes the type <typeparamref name="T"/> reads, named as the object mapper would name it.
    /// </summary>
    public static UniqueKeyIndexBuilder<T> From<T>(HollowConsumer consumer)
        where T : HollowObject =>
        new(consumer, typeof(T), typeName: null);

    /// <summary>
    /// Indexes <paramref name="typeName"/>, read as <typeparamref name="T"/>.
    /// </summary>
    /// <remarks>
    /// For a wrapper that is not named after the type it reads — a
    /// <see cref="Objects.Generic.GenericHollowObject"/>, which reads any of them. Java has no
    /// equivalent, since it takes the type name from the class.
    /// </remarks>
    public static UniqueKeyIndexBuilder<T> From<T>(HollowConsumer consumer, string typeName)
        where T : HollowObject =>
        new(consumer, typeof(T), typeName);
}

/// <summary>
/// Configures and creates a <see cref="UniqueKeyIndex{T, TKey}"/>.
/// </summary>
/// <typeparam name="T">The indexed type.</typeparam>
public sealed class UniqueKeyIndexBuilder<T>
    where T : HollowObject
{
    private readonly HollowConsumer _consumer;
    private readonly string _typeName;

    private PrimaryKey? _primaryTypeKey;

    internal UniqueKeyIndexBuilder(HollowConsumer consumer, Type uniqueType, string? typeName)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        _consumer = consumer;
        _typeName = typeName ?? Core.Write.ObjectMapper.HollowObjectMapper.DefaultTypeName(uniqueType);
    }

    /// <summary>
    /// Requires the key to be the primary key the indexed type declares.
    /// </summary>
    /// <remarks>
    /// The key type then has to name exactly the declared key's field paths; what it gains is that the
    /// index matches the uniqueness the producer enforces, so a query cannot silently be against a key
    /// that is not unique.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The dataset has no schema for the indexed type, or the schema declares no primary key.
    /// </exception>
    public UniqueKeyIndexBuilder<T> BindToPrimaryKey()
    {
        HollowReadStateEngine stateEngine = _consumer.StateEngine
            ?? throw new InvalidOperationException("the consumer holds no data yet");

        if (stateEngine.GetNonNullSchema(_typeName) is not HollowObjectSchema schema)
        {
            throw new InvalidOperationException($"{_typeName} is not an object type in this dataset");
        }

        _primaryTypeKey = schema.PrimaryKey
            ?? throw new InvalidOperationException($"{_typeName} declares no primary key");

        return this;
    }

    /// <summary>
    /// Matches on the field paths declared by the <see cref="FieldPathAttribute"/> members of
    /// <typeparamref name="TKey"/>.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <exception cref="ArgumentException">
    /// <typeparamref name="TKey"/> declares no field path, one of them is invalid, or a member's type
    /// cannot match the field its path resolves to.
    /// </exception>
    public UniqueKeyIndex<T, TKey> UsingBean<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties
            | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields)]
        TKey>() =>
        new(_consumer,
            _typeName,
            _primaryTypeKey,
            MatchFieldPathArgumentExtractor<TKey>.FromHolderType(
                RequireStateEngine(), typeof(TKey), _typeName, FieldPathResolvers.PrimaryKey));

    /// <summary>
    /// Matches on one field path, where the key is the value itself.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <remarks>
    /// Java takes the key type as a <c>Class</c> argument alongside the path; here it is the method's
    /// type argument, so the two cannot disagree.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="keyFieldPath"/> starts at another type.
    /// </exception>
    public UniqueKeyIndex<T, TKey> UsingPath<TKey>(FieldPath<T, TKey> keyFieldPath)
    {
        ArgumentNullException.ThrowIfNull(keyFieldPath);

        keyFieldPath.RequireRoot(_typeName);

        return UsingPathRaw<TKey>(keyFieldPath.Path);
    }

    /// <summary>
    /// Matches on one field path written out as text, for a path the generated ones cannot express.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <exception cref="ArgumentException">
    /// <paramref name="keyFieldPath"/> is empty or invalid, or <typeparamref name="TKey"/> cannot match
    /// the field it resolves to.
    /// </exception>
    public UniqueKeyIndex<T, TKey> UsingPathRaw<TKey>(string keyFieldPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyFieldPath);

        return new UniqueKeyIndex<T, TKey>(
            _consumer,
            _typeName,
            _primaryTypeKey,
            [
                MatchFieldPathArgumentExtractor<TKey>.FromPathAndType(
                    RequireStateEngine(), _typeName, keyFieldPath, FieldPathResolvers.PrimaryKey),
            ]);
    }

    private HollowReadStateEngine RequireStateEngine() =>
        _consumer.StateEngine ?? throw new InvalidOperationException("the consumer holds no data yet");
}

/// <summary>
/// Finds the one record of a type that holds a given key.
/// </summary>
/// <remarks>
/// <para>
/// A typed façade over <see cref="HollowPrimaryKeyIndex"/>: the key is an object of the caller's own
/// type rather than a loose <c>object[]</c>, and the match comes back as a record wrapper rather than
/// an ordinal. Both ends are checked against the schemas when the index is built.
/// </para>
/// <code>
/// UniqueKeyIndex&lt;Movie, int&gt; byId = UniqueKeyIndex.From&lt;Movie&gt;(consumer)
///     .BindToPrimaryKey()
///     .UsingPath&lt;int&gt;("id");
///
/// consumer.AddRefreshListener(byId);
///
/// Movie? found = byId.FindMatch(42);
/// </code>
/// <para>
/// Add it to the consumer as a refresh listener to have it follow the data, and remove it when it is no
/// longer wanted: an index that is still registered is still being rebuilt, and still holds the state it
/// was built over.
/// </para>
/// </remarks>
/// <typeparam name="T">The indexed type.</typeparam>
/// <typeparam name="TKey">The key type.</typeparam>
public sealed class UniqueKeyIndex<T, TKey> : IRefreshListener, IRefreshRegistrationListener, IDisposable
    where T : HollowObject
{
    private readonly HollowConsumer _consumer;
    private readonly string _typeName;
    private readonly List<MatchFieldPathArgumentExtractor<TKey>> _matchFields;
    private readonly SelectFieldPathResultExtractor<T> _select;
    private readonly string[] _matchFieldPaths;

    /// <summary>
    /// The index and the API it was built alongside, replaced together so that a query cannot pair one
    /// refresh's index with another's API.
    /// </summary>
    private volatile Bound _bound;

    internal UniqueKeyIndex(
        HollowConsumer consumer,
        string typeName,
        PrimaryKey? primaryTypeKey,
        List<MatchFieldPathArgumentExtractor<TKey>> matchFields)
    {
        _consumer = consumer;
        _typeName = typeName;

        using (consumer.AcquireRefreshLock())
        {
            HollowReadStateEngine stateEngine = consumer.StateEngine
                ?? throw new InvalidOperationException("the consumer holds no data yet");

            _select = SelectFieldPathResultExtractor<T>.From(
                (consumer.Api ?? throw new InvalidOperationException("the consumer holds no data yet")).GetType(),
                stateEngine,
                typeName,
                string.Empty);

            _matchFields = primaryTypeKey is null
                ? matchFields
                : OrderByPrimaryKey(stateEngine, typeName, primaryTypeKey, matchFields);

            // ToString rather than Text: a path bound without auto-expansion keeps its trailing "!",
            // which is what tells the index underneath to stop at the reference rather than read
            // through it.
            _matchFieldPaths = [.. _matchFields.Select(field => field.FieldPath.ToString())];

            _bound = new Bound(
                new HollowPrimaryKeyIndex(stateEngine, typeName, _matchFieldPaths), consumer.Api);
        }
    }

    /// <summary>
    /// The record holding <paramref name="key"/>, or <see langword="null"/> if no record does.
    /// </summary>
    /// <remarks>
    /// A key member left null is not matched on at all, which is how an optional part of a composite key
    /// is expressed. A key with every member null matches nothing.
    /// </remarks>
    public T? FindMatch(TKey key)
    {
        object?[] keyValues = new object?[_matchFields.Count];
        int given = 0;

        foreach (MatchFieldPathArgumentExtractor<TKey> matchField in _matchFields)
        {
            if (matchField.Extract(key) is { } value)
            {
                keyValues[given++] = value;
            }
        }

        if (given == 0)
        {
            return null;
        }

        Bound bound = _bound;
        int ordinal = bound.Index.GetMatchingOrdinal(given == keyValues.Length ? keyValues : keyValues[..given]);

        return ordinal == HollowConstants.OrdinalNone || bound.Api is null
            ? null
            : _select.Extract(bound.Api, ordinal);
    }

    /// <summary>
    /// Puts the match fields in the order the declared primary key lists them, and refuses a key that is
    /// not the declared one.
    /// </summary>
    private static List<MatchFieldPathArgumentExtractor<TKey>> OrderByPrimaryKey(
        HollowReadStateEngine stateEngine,
        string typeName,
        PrimaryKey primaryTypeKey,
        List<MatchFieldPathArgumentExtractor<TKey>> matchFields)
    {
        List<BoundFieldPath> declared =
        [
            .. primaryTypeKey.FieldPaths.Select(
                path => FieldPathResolvers.PrimaryKey(stateEngine, typeName, path)),
        ];

        List<MatchFieldPathArgumentExtractor<TKey>> ordered =
        [
            .. declared
                .Select(path => matchFields.FirstOrDefault(field => field.FieldPath.Equals(path)))
                .Where(field => field is not null)
                .Select(field => field!),
        ];

        if (ordered.Count != declared.Count)
        {
            throw new ArgumentException(
                $"the key given does not match the primary key of {typeName}, which is "
                + $"({string.Join(", ", declared.Select(path => path.ToString()))})",
                nameof(matchFields));
        }

        return ordered;
    }

    /// <inheritdoc />
    public void RefreshStarted(long currentVersion, long requestedVersion)
    {
    }

    /// <inheritdoc />
    public void SnapshotUpdateOccurred(HollowReadStateEngine stateEngine, long version)
    {
        Bound previous = _bound;
        previous.Index.DetachFromDeltaUpdates();

        HollowPrimaryKeyIndex rebuilt = new(stateEngine, previous.Index.PrimaryKey);
        rebuilt.ListenForDeltaUpdates();

        _bound = new Bound(rebuilt, _consumer.Api);
        previous.Index.Dispose();
    }

    /// <inheritdoc />
    public void DeltaUpdateOccurred(HollowReadStateEngine stateEngine, long version)
    {
        // The index followed the delta itself, and the API reads through the same state engine, so the
        // pair this index holds is still the right one.
    }

    /// <inheritdoc />
    public void BlobLoaded(Blob transition)
    {
    }

    /// <inheritdoc />
    public void RefreshSuccessful(long beforeVersion, long afterVersion, long requestedVersion)
    {
    }

    /// <inheritdoc />
    public void RefreshFailed(
        long beforeVersion, long afterVersion, long requestedVersion, Exception failureCause)
    {
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// <paramref name="consumer"/> is not the consumer this index was built over.
    /// </exception>
    public void OnBeforeAddition(HollowConsumer consumer)
    {
        if (consumer != _consumer)
        {
            throw new InvalidOperationException(
                "this index reads a different consumer than the one it is being added to");
        }

        _bound.Index.ListenForDeltaUpdates();
    }

    /// <inheritdoc />
    public void OnAfterRemoval(HollowConsumer consumer) => _bound.Index.DetachFromDeltaUpdates();

    /// <summary>
    /// Releases the index's storage. Remove it from the consumer first, or it will be rebuilt.
    /// </summary>
    public void Dispose() => _bound.Index.Dispose();

    /// <inheritdoc />
    public override string ToString() =>
        $"UniqueKeyIndex({_typeName}: {string.Join(", ", _matchFieldPaths)})";

    private sealed record Bound(HollowPrimaryKeyIndex Index, HollowApi? Api);
}
