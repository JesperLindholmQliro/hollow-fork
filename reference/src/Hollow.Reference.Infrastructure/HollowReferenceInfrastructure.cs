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

using Amazon;
using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Amazon.S3;
using global::Azure.Data.Tables;
using global::Azure.Identity;
using global::Azure.Storage.Blobs;
using Hollow.Api.Consumer;
using Hollow.Api.Producer;
using Hollow.Reference.Infrastructure.Adapters;
using Hollow.Reference.Infrastructure.Storage;
using Hollow.Reference.Infrastructure.Storage.Aws;
using Hollow.Reference.Infrastructure.Storage.Azure;
using Hollow.Reference.Infrastructure.Storage.Local;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Hollow.Reference.Infrastructure;

/// <summary>
/// The four things Hollow asks for — a publisher, an announcer, a blob retriever and an announcement
/// watcher — built from configuration and pointed at one of the three infrastructures.
/// </summary>
/// <remarks>
/// <para>
/// This is the only place the three modes are mentioned together, and the only place the choice between
/// them is made. Everything downstream sees interfaces and cannot tell which mode it is in, which is
/// the point: switching is a configuration change, not a different build and not a different code path
/// in the application.
/// </para>
/// <para>
/// The Java reference implementation makes this choice by editing <c>Producer.main</c> and
/// <c>Consumer.main</c> and recompiling; the filesystem and S3 wirings are alternative lines of code,
/// only one of which is ever live.
/// </para>
/// </remarks>
public sealed class HollowReferenceInfrastructure : IDisposable
{
    private readonly List<IDisposable> _ownedResources = [];

    private HollowReferenceInfrastructure(
        HollowReferenceOptions options, ILoggerFactory? loggerFactory)
    {
        Options = options;

        BlobStore = Own(CreateBlobStore(options));
        AnnouncementStore = Own(CreateAnnouncementStore(options));

        Publisher = Own(new HollowBlobStorePublisher(BlobStore, options.Namespace));
        BlobRetriever = new HollowBlobStoreBlobRetriever(BlobStore, options.Namespace);
        Announcer = new HollowAnnouncementStoreAnnouncer(AnnouncementStore);

        AnnouncementWatcher = Own(new HollowAnnouncementStoreWatcher(
            AnnouncementStore,
            PollIntervalFor(options),
            loggerFactory?.CreateLogger<HollowAnnouncementStoreWatcher>()));
    }

    public HollowReferenceOptions Options { get; }

    public IHollowBlobStore BlobStore { get; }

    public IHollowAnnouncementStore AnnouncementStore { get; }

    public IPublisher Publisher { get; }

    public IAnnouncer Announcer { get; }

    public IBlobRetriever BlobRetriever { get; }

    public IAnnouncementWatcher AnnouncementWatcher { get; }

    /// <summary>
    /// Reads the <c>Hollow</c> section and builds the infrastructure it names, failing immediately if
    /// the branch for the chosen mode is not filled in.
    /// </summary>
    public static HollowReferenceInfrastructure Create(
        IConfiguration configuration, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        HollowReferenceOptions options =
            configuration.GetSection(HollowReferenceOptions.SectionName).Get<HollowReferenceOptions>()
            ?? new HollowReferenceOptions();

        // Eagerly, so that a missing bucket name is a message at start-up rather than an exception in
        // the middle of the first cycle.
        options.Validate();

        return new HollowReferenceInfrastructure(options, loggerFactory);
    }

    public void Dispose()
    {
        // In reverse, so the watcher stops before the store it reads from goes away.
        for (int i = _ownedResources.Count - 1; i >= 0; i--)
        {
            _ownedResources[i].Dispose();
        }

        _ownedResources.Clear();
    }

    private static IHollowBlobStore CreateBlobStore(HollowReferenceOptions options) => options.Mode switch
    {
        HollowInfrastructureMode.Local =>
            new LocalBlobStore(options.Local.RootPath, new SimulatedLatency(options.Local.Latency)),

        HollowInfrastructureMode.Aws =>
            new S3BlobStore(CreateS3Client(options.Aws), options.Aws.BucketName),

        HollowInfrastructureMode.Azure =>
            new AzureBlobStore(CreateBlobContainerClient(options.Azure)),

        _ => throw new InvalidOperationException($"Unknown mode {options.Mode}."),
    };

    private static IHollowAnnouncementStore CreateAnnouncementStore(HollowReferenceOptions options) =>
        options.Mode switch
        {
            HollowInfrastructureMode.Local => new LocalAnnouncementStore(
                options.Local.RootPath,
                new SimulatedLatency(options.Local.Latency),
                options.Local.AnnouncementsToKeep),

            HollowInfrastructureMode.Aws => new DynamoDbAnnouncementStore(
                CreateDynamoDbClient(options.Aws), options.Aws.TableName, options.Namespace),

            HollowInfrastructureMode.Azure => new AzureTableAnnouncementStore(
                CreateTableClient(options.Azure), options.Namespace),

            _ => throw new InvalidOperationException($"Unknown mode {options.Mode}."),
        };

    /// <summary>
    /// How often to read the announcement when the store cannot say that it changed. Zero for the local
    /// mode, which can.
    /// </summary>
    private static TimeSpan PollIntervalFor(HollowReferenceOptions options) => options.Mode switch
    {
        HollowInfrastructureMode.Aws => options.Aws.PollInterval,
        HollowInfrastructureMode.Azure => options.Azure.PollInterval,
        _ => TimeSpan.Zero,
    };

    private static AmazonS3Client CreateS3Client(AwsOptions options)
    {
        // Path style addresses a bucket as /bucket/key rather than as a subdomain, which every
        // S3-compatible server other than S3 itself needs.
        AmazonS3Config config = new() { ForcePathStyle = options.ForcePathStyle };

        Configure(config, options);

        return Credentials(options) is { } credentials
            ? new AmazonS3Client(credentials, config)
            : new AmazonS3Client(config);
    }

    private static AmazonDynamoDBClient CreateDynamoDbClient(AwsOptions options)
    {
        AmazonDynamoDBConfig config = new();

        Configure(config, options);

        return Credentials(options) is { } credentials
            ? new AmazonDynamoDBClient(credentials, config)
            : new AmazonDynamoDBClient(config);
    }

    /// <summary>Points a client at a region, or at a stand-in service if one is configured.</summary>
    /// <remarks>
    /// A <c>ServiceUrl</c> beats a region: it is only ever set to run against LocalStack, MinIO or
    /// DynamoDB Local, and those do not have regions to be in.
    /// </remarks>
    private static void Configure(ClientConfig config, AwsOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            config.ServiceURL = options.ServiceUrl;
            return;
        }

        if (!string.IsNullOrWhiteSpace(options.Region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }
    }

    /// <summary>
    /// The credentials for a named profile, or <see langword="null"/> to let the SDK find its own.
    /// </summary>
    /// <remarks>
    /// The Java original takes an <c>AWSCredentials</c> in every constructor and has no fallback, so it
    /// only runs where a key has been handed to it. Leaving this null uses the default chain —
    /// environment, profile, container role, instance role — which is what a deployed service should do
    /// and what a developer who has run <c>aws configure</c> already has.
    /// </remarks>
    private static AWSCredentials? Credentials(AwsOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Profile))
        {
            return null;
        }

        CredentialProfileStoreChain chain = new();

        return chain.TryGetAWSCredentials(options.Profile, out AWSCredentials? credentials)
            ? credentials
            : throw new InvalidOperationException(
                $"The AWS profile '{options.Profile}' is not in the shared credentials file.");
    }

    private static BlobContainerClient CreateBlobContainerClient(AzureOptions options) =>
        string.IsNullOrWhiteSpace(options.ConnectionString)
            ? new BlobContainerClient(
                new Uri(new Uri(options.BlobServiceUri! + "/"), options.ContainerName),
                new DefaultAzureCredential())
            : new BlobContainerClient(options.ConnectionString, options.ContainerName);

    private static TableClient CreateTableClient(AzureOptions options) =>
        string.IsNullOrWhiteSpace(options.ConnectionString)
            ? new TableClient(
                new Uri(options.TableServiceUri!), options.TableName, new DefaultAzureCredential())
            : new TableClient(options.ConnectionString, options.TableName);

    private T Own<T>(T resource)
    {
        if (resource is IDisposable disposable)
        {
            _ownedResources.Add(disposable);
        }

        return resource;
    }
}
