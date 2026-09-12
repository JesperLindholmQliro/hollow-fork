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
using Hollow.Core.Index;
using Hollow.Core.Read.Engine;

namespace Hollow.Api.Consumer.Index;

/// <summary>
/// Starts building a <see cref="HashIndex{T, TQuery}"/> or a
/// <see cref="HashIndexSelect{T, TSelect, TQuery}"/>.
/// </summary>
public static class HashIndex
{
    /// <summary>
    /// Indexes the type <typeparamref name="T"/> reads, named as the object mapper would name it.
    /// </summary>
    public static HashIndexBuilder<T> From<T>(HollowConsumer consumer)
        where T : IHollowRecord =>
        new(consumer, typeof(T), typeName: null);

    /// <summary>
    /// Indexes <paramref name="typeName"/>, read as <typeparamref name="T"/>.
    /// </summary>
    /// <remarks>
    /// For a wrapper that is not named after the type it reads, such as a generic record. Java has no
    /// equivalent, since it takes the type name from the class.
    /// </remarks>
    public static HashIndexBuilder<T> From<T>(HollowConsumer consumer, string typeName)
        where T : IHollowRecord =>
        new(consumer, typeof(T), typeName);
}

/// <summary>
/// Configures and creates a hash index over <typeparamref name="T"/>.
/// </summary>
/// <typeparam name="T">The root type, which is what a query matches against.</typeparam>
public sealed class HashIndexBuilder<T>
    where T : IHollowRecord
{
    private readonly HollowConsumer _consumer;
    private readonly string _typeName;

    internal HashIndexBuilder(HollowConsumer consumer, Type rootType, string? typeName)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        _consumer = consumer;
        _typeName = typeName ?? Core.Write.ObjectMapper.HollowObjectMapper.DefaultTypeName(rootType);
    }

    /// <summary>
    /// Matches on the field paths declared by the <see cref="FieldPathAttribute"/> members of
    /// <typeparamref name="TQuery"/>, and returns the root records that match.
    /// </summary>
    /// <typeparam name="TQuery">The query type.</typeparam>
    public HashIndex<T, TQuery> UsingBean<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties
            | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields)]
        TQuery>() =>
        new(_consumer, _typeName, string.Empty, matchPath: null, typeof(TQuery));

    /// <summary>
    /// Matches on <paramref name="queryFieldPath"/>, where the query is the value the path arrives at,
    /// and returns the root records that match.
    /// </summary>
    /// <remarks>
    /// The query type comes from the path, so there is nothing to state twice and nothing to get
    /// wrong: <c>UsingPath(CataloguePaths.Movie.Tags.Element.Value)</c> is a
    /// <see cref="HashIndex{T, TQuery}"/> of <c>string</c> because the path is.
    /// </remarks>
    public HashIndex<T, TQuery> UsingPath<TQuery>(FieldPath<T, TQuery> queryFieldPath)
    {
        ArgumentNullException.ThrowIfNull(queryFieldPath);

        queryFieldPath.RequireRoot(_typeName);

        return UsingPathRaw<TQuery>(queryFieldPath.Path);
    }

    /// <summary>
    /// Matches on one field path written out as text, for a path the generated ones cannot express.
    /// </summary>
    /// <typeparam name="TQuery">The query type.</typeparam>
    public HashIndex<T, TQuery> UsingPathRaw<TQuery>(string queryFieldPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(queryFieldPath);

        return new HashIndex<T, TQuery>(_consumer, _typeName, string.Empty, queryFieldPath, typeof(TQuery));
    }

    /// <summary>
    /// Returns the records at <paramref name="selectFieldPath"/> rather than the root records that
    /// match.
    /// </summary>
    /// <remarks>
    /// The path has to arrive at a record rather than at a value, which the generated paths make plain:
    /// <c>CataloguePaths.Movie.Cast.Element</c> is an <c>Actor</c>, and its <c>.Name</c> is not.
    /// </remarks>
    public HashIndexSelectBuilder<T, TSelect> SelectField<TSelect>(FieldPath<T, TSelect> selectFieldPath)
        where TSelect : IHollowRecord
    {
        ArgumentNullException.ThrowIfNull(selectFieldPath);

        selectFieldPath.RequireRoot(_typeName);

        return SelectFieldRaw<TSelect>(selectFieldPath.Path);
    }

    /// <summary>
    /// Returns the records at a select path written out as text, for a path the generated ones cannot
    /// express.
    /// </summary>
    /// <typeparam name="TSelect">The type the path resolves to.</typeparam>
    /// <remarks>
    /// The path has to name a record — <c>"cast.element"</c>, not <c>"cast.element.name"</c>. Java
    /// takes the select type as a <c>Class</c> argument; here it is the type argument.
    /// </remarks>
    public HashIndexSelectBuilder<T, TSelect> SelectFieldRaw<TSelect>(string selectFieldPath)
        where TSelect : IHollowRecord
    {
        ArgumentNullException.ThrowIfNull(selectFieldPath);

        return new HashIndexSelectBuilder<T, TSelect>(_consumer, _typeName, selectFieldPath);
    }
}

/// <summary>
/// Configures and creates a hash index that returns records from a select path.
/// </summary>
/// <typeparam name="T">The root type, which is what a query matches against.</typeparam>
/// <typeparam name="TSelect">The type the select path resolves to.</typeparam>
public sealed class HashIndexSelectBuilder<T, TSelect>
    where T : IHollowRecord
    where TSelect : IHollowRecord
{
    private readonly HollowConsumer _consumer;
    private readonly string _typeName;
    private readonly string _selectFieldPath;

    internal HashIndexSelectBuilder(HollowConsumer consumer, string typeName, string selectFieldPath)
    {
        _consumer = consumer;
        _typeName = typeName;
        _selectFieldPath = selectFieldPath;
    }

    /// <summary>
    /// Matches on the field paths declared by the <see cref="FieldPathAttribute"/> members of
    /// <typeparamref name="TQuery"/>.
    /// </summary>
    /// <typeparam name="TQuery">The query type.</typeparam>
    public HashIndexSelect<T, TSelect, TQuery> UsingBean<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties
            | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields)]
        TQuery>() =>
        new(_consumer, _typeName, _selectFieldPath, matchPath: null, typeof(TQuery));

    /// <summary>
    /// Matches on one field path, where the query is the value itself.
    /// </summary>
    /// <typeparam name="TQuery">The query type.</typeparam>
    public HashIndexSelect<T, TSelect, TQuery> UsingPath<TQuery>(FieldPath<T, TQuery> queryFieldPath)
    {
        ArgumentNullException.ThrowIfNull(queryFieldPath);

        queryFieldPath.RequireRoot(_typeName);

        return UsingPathRaw<TQuery>(queryFieldPath.Path);
    }

    /// <summary>
    /// Matches on one field path written out as text, for a path the generated ones cannot express.
    /// </summary>
    /// <typeparam name="TQuery">The query type.</typeparam>
    public HashIndexSelect<T, TSelect, TQuery> UsingPathRaw<TQuery>(string queryFieldPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(queryFieldPath);

        return new HashIndexSelect<T, TSelect, TQuery>(
            _consumer, _typeName, _selectFieldPath, queryFieldPath, typeof(TQuery));
    }
}

/// <summary>
/// Finds the records that match a query, where a match is neither unique nor one-to-one.
/// </summary>
/// <remarks>
/// <para>
/// A typed façade over <see cref="HollowHashIndex"/>. The query is an object of the caller's own type
/// rather than a loose <c>object[]</c>, and the matches come back as record wrappers rather than
/// ordinals. One query may match many records, and a path that crosses a collection means one record
/// may match many queries.
/// </para>
/// <code>
/// HashIndexSelect&lt;Movie, Actor, string&gt; byActorName =
///     HashIndex.From&lt;Movie&gt;(consumer)
///         .SelectField&lt;Actor&gt;("cast.element")
///         .UsingPath&lt;string&gt;("cast.element.name.value");
///
/// consumer.AddRefreshListener(byActorName);
///
/// foreach (Actor actor in byActorName.FindMatches("Keanu Reeves"))
/// {
///     // …
/// }
/// </code>
/// <para>
/// Add it to the consumer as a refresh listener to have it follow the data, and remove it when it is no
/// longer wanted.
/// </para>
/// </remarks>
/// <typeparam name="T">The root type.</typeparam>
/// <typeparam name="TSelect">The type returned.</typeparam>
/// <typeparam name="TQuery">The query type.</typeparam>
public class HashIndexSelect<T, TSelect, TQuery> : IRefreshListener, IRefreshRegistrationListener
    where T : IHollowRecord
    where TSelect : IHollowRecord
{
    private readonly HollowConsumer _consumer;
    private readonly string _typeName;
    private readonly SelectFieldPathResultExtractor<TSelect> _select;
    private readonly List<MatchFieldPathArgumentExtractor<TQuery>> _matchFields;
    private readonly string _selectFieldPath;
    private readonly string[] _matchFieldPaths;

    /// <summary>
    /// The index and the API it was built alongside, replaced together so that a query cannot pair one
    /// refresh's index with another's API.
    /// </summary>
    private volatile Bound _bound;

    internal HashIndexSelect(
        HollowConsumer consumer,
        string typeName,
        string selectFieldPath,
        string? matchPath,
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties
            | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields)]
        Type queryType)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        _consumer = consumer;
        _typeName = typeName;

        using (consumer.AcquireRefreshLock())
        {
            HollowReadStateEngine stateEngine = consumer.StateEngine
                ?? throw new InvalidOperationException("the consumer holds no data yet");
            HollowApi api = consumer.Api
                ?? throw new InvalidOperationException("the consumer holds no data yet");

            _select = SelectFieldPathResultExtractor<TSelect>.From(
                api.GetType(), stateEngine, typeName, selectFieldPath);

            _matchFields = matchPath is null
                ? MatchFieldPathArgumentExtractor<TQuery>.FromHolderType(
                    stateEngine, queryType, typeName, FieldPathResolvers.HashIndex)
                :
                [
                    MatchFieldPathArgumentExtractor<TQuery>.FromPathAndType(
                        stateEngine, typeName, matchPath, FieldPathResolvers.HashIndex),
                ];

            _selectFieldPath = _select.FieldPath.Text;
            _matchFieldPaths = [.. _matchFields.Select(field => field.FieldPath.Text)];

            _bound = new Bound(
                new HollowHashIndex(stateEngine, typeName, _selectFieldPath, _matchFieldPaths), api);
        }
    }

    /// <summary>
    /// The records matching <paramref name="query"/>, which may be none.
    /// </summary>
    /// <remarks>
    /// The results are read lazily out of the index, so a caller taking only the first match pays for
    /// only that one.
    /// </remarks>
    public IEnumerable<TSelect> FindMatches(TQuery query)
    {
        object?[] queryValues = [.. _matchFields.Select(field => field.Extract(query))];

        Bound bound = _bound;
        HollowHashIndexResult? matches = bound.Index.FindMatches(queryValues);

        return matches is null || bound.Api is null
            ? []
            : matches.AsEnumerable().Select(ordinal => _select.Extract(bound.Api, ordinal));
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

        HollowHashIndex rebuilt = new(stateEngine, _typeName, _selectFieldPath, _matchFieldPaths);
        rebuilt.ListenForDeltaUpdates();

        _bound = new Bound(rebuilt, _consumer.Api);
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

    /// <inheritdoc />
    public override string ToString() =>
        $"HashIndex({_typeName}: select {(_selectFieldPath.Length == 0 ? "(root)" : _selectFieldPath)} "
        + $"matching {string.Join(", ", _matchFieldPaths)})";

    private sealed record Bound(HollowHashIndex Index, HollowApi? Api);
}

/// <summary>
/// Finds the records of one type that match a query, returning the root records themselves.
/// </summary>
/// <remarks>
/// <see cref="HashIndexSelect{T, TSelect, TQuery}"/> with the select path left empty, which is how Java
/// relates the two as well.
/// </remarks>
/// <typeparam name="T">The root, and result, type.</typeparam>
/// <typeparam name="TQuery">The query type.</typeparam>
public sealed class HashIndex<T, TQuery> : HashIndexSelect<T, T, TQuery>
    where T : IHollowRecord
{
    internal HashIndex(
        HollowConsumer consumer,
        string typeName,
        string selectFieldPath,
        string? matchPath,
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties
            | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields)]
        Type queryType)
        : base(consumer, typeName, selectFieldPath, matchPath, queryType)
    {
    }
}
