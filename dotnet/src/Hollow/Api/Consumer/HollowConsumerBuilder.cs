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

using Hollow.Api.Client;
using Hollow.Api.Consumer.Fs;
using Hollow.Core.Memory;
using Hollow.Core.Read.Filter;

namespace Hollow.Api.Consumer;

/// <summary>
/// Configures and creates a <see cref="HollowConsumer"/>.
/// </summary>
/// <remarks>
/// A blob retriever is the only thing required. Java nests this as <c>HollowConsumer.Builder</c> and
/// makes it generic so that a subclass can keep the fluent return type; C# extension methods cover that
/// case without the type parameter, so this is a plain sealed class.
/// </remarks>
public sealed class HollowConsumerBuilder
{
    private readonly List<IRefreshListener> _refreshListeners = [];

    /// <summary>Where the blobs come from.</summary>
    internal IBlobRetriever? BlobRetriever { get; private set; }

    /// <summary>What tracks the announced version, if anything does.</summary>
    internal IAnnouncementWatcher? AnnouncementWatcher { get; private set; }

    /// <summary>The listeners to attach before the first refresh.</summary>
    internal IReadOnlyList<IRefreshListener> RefreshListeners => _refreshListeners;

    /// <summary>When the consumer may abandon its delta chain for a snapshot.</summary>
    internal IDoubleSnapshotConfig? DoubleSnapshotConfig { get; private set; }

    /// <summary>Whether a fallback snapshot has to have been announced.</summary>
    internal IUpdatePlanBlobVerifier? UpdatePlanBlobVerifier { get; private set; }

    /// <summary>Which types and fields to keep, if not all of them.</summary>
    internal ITypeFilter? TypeFilter { get; private set; }

    /// <summary>Where the consumer's record storage lives.</summary>
    internal MemoryMode MemoryMode { get; private set; } = MemoryMode.OnHeap;

    /// <summary>What builds the typed API, if anything beyond the default does.</summary>
    internal IHollowApiFactory? ApiFactory { get; private set; }

    /// <summary>
    /// Reads blobs through <paramref name="blobRetriever"/>.
    /// </summary>
    public HollowConsumerBuilder WithBlobRetriever(IBlobRetriever blobRetriever)
    {
        ArgumentNullException.ThrowIfNull(blobRetriever);

        BlobRetriever = blobRetriever;

        return this;
    }

    /// <summary>
    /// Reads blobs from the directory a filesystem-backed producer publishes to.
    /// </summary>
    public HollowConsumerBuilder WithLocalBlobStore(string blobStoreDirectory)
    {
        ArgumentNullException.ThrowIfNull(blobStoreDirectory);

        BlobRetriever = new HollowFilesystemBlobRetriever(blobStoreDirectory);

        return this;
    }

    /// <summary>
    /// Follows the versions <paramref name="announcementWatcher"/> announces.
    /// </summary>
    /// <remarks>
    /// A consumer with a watcher decides its own version, so
    /// <see cref="HollowConsumer.TriggerRefreshTo(long)"/> stops being available on it.
    /// </remarks>
    public HollowConsumerBuilder WithAnnouncementWatcher(IAnnouncementWatcher announcementWatcher)
    {
        ArgumentNullException.ThrowIfNull(announcementWatcher);

        AnnouncementWatcher = announcementWatcher;

        return this;
    }

    /// <summary>
    /// Attaches refresh listeners before the first refresh.
    /// </summary>
    public HollowConsumerBuilder WithRefreshListeners(params IRefreshListener[] refreshListeners)
    {
        ArgumentNullException.ThrowIfNull(refreshListeners);

        _refreshListeners.AddRange(refreshListeners);

        return this;
    }

    /// <summary>
    /// Sets when the consumer may abandon its delta chain for a snapshot.
    /// </summary>
    public HollowConsumerBuilder WithDoubleSnapshotConfig(IDoubleSnapshotConfig doubleSnapshotConfig)
    {
        ArgumentNullException.ThrowIfNull(doubleSnapshotConfig);

        DoubleSnapshotConfig = doubleSnapshotConfig;

        return this;
    }

    /// <summary>
    /// Requires that a snapshot the planner falls back to was actually announced.
    /// </summary>
    public HollowConsumerBuilder WithUpdatePlanBlobVerifier(IUpdatePlanBlobVerifier updatePlanBlobVerifier)
    {
        ArgumentNullException.ThrowIfNull(updatePlanBlobVerifier);

        UpdatePlanBlobVerifier = updatePlanBlobVerifier;

        return this;
    }

    /// <summary>
    /// Keeps only the types and fields <paramref name="typeFilter"/> selects, so that a consumer
    /// interested in part of a dataset does not pay for the rest.
    /// </summary>
    public HollowConsumerBuilder WithTypeFilter(ITypeFilter typeFilter)
    {
        ArgumentNullException.ThrowIfNull(typeFilter);

        TypeFilter = typeFilter;

        return this;
    }

    /// <summary>
    /// Sets where the consumer's record storage lives.
    /// </summary>
    public HollowConsumerBuilder WithMemoryMode(MemoryMode memoryMode)
    {
        MemoryMode = memoryMode;

        return this;
    }

    /// <summary>
    /// Builds the typed API the consumer hands out through <see cref="HollowConsumer.Api"/>.
    /// </summary>
    /// <remarks>
    /// Without this the consumer's API is a plain <see cref="Custom.HollowApi"/>, which carries no type
    /// APIs. A generated API goes in here:
    /// <code>
    /// .WithApiFactory(new DelegateHollowApiFactory(access =&gt; new MovieApi(access)))
    /// </code>
    /// Java takes the generated class itself and reflects over its constructors; see
    /// <see cref="GeneratedHollowApiFactory{TApi}"/> for that form.
    /// </remarks>
    public HollowConsumerBuilder WithApiFactory(IHollowApiFactory apiFactory)
    {
        ArgumentNullException.ThrowIfNull(apiFactory);

        ApiFactory = apiFactory;

        return this;
    }

    /// <summary>
    /// Creates the consumer.
    /// </summary>
    /// <remarks>
    /// The consumer holds no data until it is refreshed — through
    /// <see cref="HollowConsumer.TriggerRefresh"/>, or by the announcement watcher noticing a version.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No blob retriever was configured.</exception>
    public HollowConsumer Build() => new(this);
}
