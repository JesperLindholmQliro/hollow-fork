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
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Hollow.Api.Consumer;

namespace Hollow.Reference.Infrastructure.Storage.Aws;

/// <summary>
/// The announcement store the Java reference implementation uses: one row in a DynamoDB table.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>DynamoDBAnnouncer</c> and <c>DynamoDBAnnouncementWatcher</c> between them. The table
/// has a string partition key called <c>namespace</c> and needs no other schema; the row holds
/// <c>version</c>, an optional <c>pin_version</c>, and — new here — the announcement metadata the
/// producer passed, which Hollow's own <c>Announce</c> signature carries and Java's announcer drops.
/// </para>
/// <para>
/// Written with the low-level client rather than the document model. The document model's
/// <c>Table.LoadTable</c> describes the table on every start-up to learn a key this code already knows,
/// and in the v4 SDK it is obsolete besides.
/// </para>
/// <para>
/// Nothing pushes: a reader polls. Making DynamoDB tell a consumer that a row changed means a stream, a
/// Lambda and a topic, which is a lot of infrastructure to provision before a quick-start guide gets to
/// its first cycle.
/// </para>
/// </remarks>
public sealed class DynamoDbAnnouncementStore : IHollowAnnouncementStore
{
    private const string NamespaceAttribute = "namespace";
    private const string VersionAttribute = "version";
    private const string PinVersionAttribute = "pin_version";
    private const string MetadataAttribute = "announcement_metadata";

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;
    private readonly string _blobNamespace;

    public DynamoDbAnnouncementStore(IAmazonDynamoDB dynamoDb, string tableName, string blobNamespace)
    {
        ArgumentNullException.ThrowIfNull(dynamoDb);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(blobNamespace);

        _dynamoDb = dynamoDb;
        _tableName = tableName;
        _blobNamespace = blobNamespace;
    }

    /// <inheritdoc />
    public string Description =>
        $"DynamoDB table '{_tableName}', the row with namespace '{_blobNamespace}'";

    public async Task AnnounceAsync(
        long version,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        UpdateItemRequest request = new()
        {
            TableName = _tableName,
            Key = PrimaryKey(),

            // An update rather than a put, so that a pin written by an operator survives the next
            // announcement instead of being overwritten by it.
            UpdateExpression = "SET #version = :version, #metadata = :metadata",
            ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // "version" is a DynamoDB reserved word and cannot appear in an expression unaliased.
                ["#version"] = VersionAttribute,
                ["#metadata"] = MetadataAttribute,
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
            {
                [":version"] = Number(version),
                [":metadata"] = new AttributeValue
                {
                    M = metadata.ToDictionary(
                        entry => entry.Key,
                        entry => new AttributeValue { S = entry.Value },
                        StringComparer.Ordinal),
                },
            },
        };

        await _dynamoDb.UpdateItemAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AnnouncedVersions> ReadAsync(CancellationToken cancellationToken = default)
    {
        GetItemRequest request = new()
        {
            TableName = _tableName,
            Key = PrimaryKey(),

            // The point of reading is to find out whether the producer has moved on, and an eventually
            // consistent read can say it has not when it has.
            ConsistentRead = true,
            ProjectionExpression = $"#version, {PinVersionAttribute}, {MetadataAttribute}",
            ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["#version"] = VersionAttribute,
            },
        };

        GetItemResponse response =
            await _dynamoDb.GetItemAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsItemSet)
        {
            // The producer has never announced. Not an error: a consumer may well start first.
            return AnnouncedVersions.None;
        }

        return new AnnouncedVersions(
            ReadNumber(response.Item, VersionAttribute),
            ReadNumber(response.Item, PinVersionAttribute),
            ReadMetadata(response.Item));
    }

    public IDisposable? Subscribe(Action onChanged) => null;

    private Dictionary<string, AttributeValue> PrimaryKey() =>
        new(StringComparer.Ordinal)
        {
            [NamespaceAttribute] = new AttributeValue { S = _blobNamespace },
        };

    private static AttributeValue Number(long value) =>
        new() { N = value.ToString(CultureInfo.InvariantCulture) };

    private static long ReadNumber(IReadOnlyDictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out AttributeValue? attribute)
            && attribute.N is { } text
            && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
                ? value
                : IAnnouncementWatcher.NoAnnouncementAvailable;

    private static IReadOnlyDictionary<string, string> ReadMetadata(
        IReadOnlyDictionary<string, AttributeValue> item)
    {
        if (!item.TryGetValue(MetadataAttribute, out AttributeValue? attribute) || attribute.M is null)
        {
            return AnnouncedVersions.None.Metadata;
        }

        Dictionary<string, string> metadata = new(StringComparer.Ordinal);

        foreach ((string name, AttributeValue value) in attribute.M)
        {
            if (value.S is { } text)
            {
                metadata[name] = text;
            }
        }

        return metadata;
    }
}
