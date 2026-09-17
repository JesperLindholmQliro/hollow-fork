/*
 *  Copyright 2016 Netflix, Inc.
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

using System.Globalization;
using System.Text.Json;
using global::Azure;
using global::Azure.Data.Tables;
using Hollow.Api.Consumer;

namespace Hollow.Reference.Infrastructure.Storage.Azure;

/// <summary>
/// The Azure counterpart of <see cref="Aws.DynamoDbAnnouncementStore"/>: one entity in a Table Storage
/// table.
/// </summary>
/// <remarks>
/// <para>
/// Table Storage rather than Cosmos DB, on purpose. Cosmos is the closer match to DynamoDB in shape and
/// the one an Azure-native design would reach for, but its cheapest serverless container still bills
/// per request against a provisioned account, while this table holds a single entity that is written
/// once a cycle and read once a second. Table Storage sits in the same storage account as the blobs,
/// costs a fraction of a cent a month at that rate, and does everything an announcement needs.
/// </para>
/// <para>
/// One entity: partition key <c>hollow</c>, row key the blob namespace. Announcing merges
/// <c>Version</c> and <c>Metadata</c> into it and leaves <c>PinVersion</c> alone, so an operator's pin
/// survives the producer's next cycle — the same reason the DynamoDB store updates rather than puts.
/// </para>
/// <para>
/// The metadata goes in as one JSON string rather than as a property each. A Table Storage entity is
/// flat and capped at 255 properties, and announcement metadata is the producer's to shape; keeping it
/// in one property means nothing the producer writes can collide with <c>Version</c>.
/// </para>
/// </remarks>
public sealed class AzureTableAnnouncementStore : IHollowAnnouncementStore, IDisposable
{
    /// <summary>The partition every announcement entity lives in.</summary>
    public const string PartitionKey = "hollow";

    private const string VersionProperty = "Version";
    private const string PinVersionProperty = "PinVersion";
    private const string MetadataProperty = "Metadata";

    private static readonly JsonSerializerOptions MetadataJson = new() { WriteIndented = false };

    private readonly TableClient _table;
    private readonly string _blobNamespace;
    private readonly SemaphoreSlim _tableLock = new(1, 1);

    private bool _tableChecked;

    public AzureTableAnnouncementStore(TableClient table, string blobNamespace)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(blobNamespace);

        _table = table;
        _blobNamespace = blobNamespace;
    }

    public async Task AnnounceAsync(
        long version,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        await EnsureTableAsync(cancellationToken).ConfigureAwait(false);

        TableEntity entity = new(PartitionKey, _blobNamespace)
        {
            [VersionProperty] = version,
            [MetadataProperty] = JsonSerializer.Serialize(metadata, MetadataJson),
        };

        // Merge, so that a pin written into the same entity is not erased by the next announcement.
        await _table
            .UpsertEntityAsync(entity, TableUpdateMode.Merge, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AnnouncedVersions> ReadAsync(CancellationToken cancellationToken = default)
    {
        NullableResponse<TableEntity> response;

        try
        {
            response = await _table
                .GetEntityIfExistsAsync<TableEntity>(
                    PartitionKey, _blobNamespace, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            // The table itself is not there yet, which is what a consumer started before its producer
            // sees. Nothing has been announced, which is exactly true.
            return AnnouncedVersions.None;
        }

        if (!response.HasValue || response.Value is not { } entity)
        {
            return AnnouncedVersions.None;
        }

        return new AnnouncedVersions(
            ReadVersion(entity, VersionProperty),
            ReadVersion(entity, PinVersionProperty),
            ReadMetadata(entity));
    }

    public IDisposable? Subscribe(Action onChanged) => null;

    /// <summary>Pins consumers to <paramref name="version"/>, or lifts the pin if it is null.</summary>
    /// <remarks>
    /// As with the local store, nothing in the producer or consumer calls this; it is here so a pin has
    /// one definition rather than being a row somebody edits in the portal and hopes they typed right.
    /// </remarks>
    public async Task PinAsync(long? version, CancellationToken cancellationToken = default)
    {
        await EnsureTableAsync(cancellationToken).ConfigureAwait(false);

        TableEntity entity = new(PartitionKey, _blobNamespace)
        {
            [PinVersionProperty] = version,
        };

        await _table
            .UpsertEntityAsync(entity, TableUpdateMode.Merge, cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose() => _tableLock.Dispose();

    private static long ReadVersion(TableEntity entity, string property) =>
        entity.TryGetValue(property, out object? value) && value is not null
            ? Convert.ToInt64(value, CultureInfo.InvariantCulture)
            : IAnnouncementWatcher.NoAnnouncementAvailable;

    private static IReadOnlyDictionary<string, string> ReadMetadata(TableEntity entity)
    {
        if (entity.GetString(MetadataProperty) is not { } json)
        {
            return AnnouncedVersions.None.Metadata;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, MetadataJson)
                ?? AnnouncedVersions.None.Metadata;
        }
        catch (JsonException)
        {
            return AnnouncedVersions.None.Metadata;
        }
    }

    private async Task EnsureTableAsync(CancellationToken cancellationToken)
    {
        if (_tableChecked)
        {
            return;
        }

        await _tableLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_tableChecked)
            {
                return;
            }

            await _table.CreateIfNotExistsAsync(cancellationToken).ConfigureAwait(false);

            _tableChecked = true;
        }
        finally
        {
            _tableLock.Release();
        }
    }
}
