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

namespace Hollow.Reference.Infrastructure;

/// <summary>Which of the three infrastructures the producer and consumer run against.</summary>
public enum HollowInfrastructureMode
{
    /// <summary>A directory on this machine, with a watching folder in place of an announcement table.</summary>
    Local,

    /// <summary>Amazon S3 and DynamoDB, as in the Java reference implementation.</summary>
    Aws,

    /// <summary>Azure Blob Storage and Azure Table Storage.</summary>
    Azure,
}

/// <summary>
/// Everything the reference implementation is configured by, bound from the <c>Hollow</c> section of
/// <c>appsettings.json</c>.
/// </summary>
/// <remarks>
/// Each mode has its own branch, and they all stay in the file: switching infrastructure is a change
/// to <see cref="Mode"/> and nothing else. The branch for the mode in force is the only one read, so
/// the others may hold placeholders without the application caring.
/// </remarks>
public sealed class HollowReferenceOptions
{
    /// <summary>The configuration section these are bound from.</summary>
    public const string SectionName = "Hollow";

    /// <summary>Which infrastructure to use. Local unless something says otherwise.</summary>
    public HollowInfrastructureMode Mode { get; set; } = HollowInfrastructureMode.Local;

    /// <summary>
    /// The prefix every key is written under, so one bucket or one directory can hold several datasets.
    /// </summary>
    /// <remarks>Called the blob namespace in the Java original, which is where the key layout comes from.</remarks>
    public string Namespace { get; set; } = "hollow-reference";

    public LocalOptions Local { get; set; } = new();

    public AwsOptions Aws { get; set; } = new();

    public AzureOptions Azure { get; set; } = new();

    public ProducerOptions Producer { get; set; } = new();

    public ConsumerOptions Consumer { get; set; } = new();

    /// <summary>
    /// Checks that the branch for <see cref="Mode"/> holds what that mode needs, and throws naming the
    /// setting if it does not.
    /// </summary>
    /// <remarks>
    /// Run at startup rather than on first use, so a missing bucket name is a message before the first
    /// cycle rather than an exception in the middle of one.
    /// </remarks>
    public void Validate()
    {
        Require(Namespace, $"{SectionName}:{nameof(Namespace)}");

        switch (Mode)
        {
            case HollowInfrastructureMode.Local:
                Require(Local.RootPath, $"{SectionName}:{nameof(Local)}:{nameof(LocalOptions.RootPath)}");
                break;

            case HollowInfrastructureMode.Aws:
                Require(Aws.BucketName, $"{SectionName}:{nameof(Aws)}:{nameof(AwsOptions.BucketName)}");
                Require(Aws.TableName, $"{SectionName}:{nameof(Aws)}:{nameof(AwsOptions.TableName)}");
                break;

            case HollowInfrastructureMode.Azure:
                Require(Azure.ContainerName, $"{SectionName}:{nameof(Azure)}:{nameof(AzureOptions.ContainerName)}");
                Require(Azure.TableName, $"{SectionName}:{nameof(Azure)}:{nameof(AzureOptions.TableName)}");

                if (string.IsNullOrWhiteSpace(Azure.ConnectionString)
                    && (string.IsNullOrWhiteSpace(Azure.BlobServiceUri)
                        || string.IsNullOrWhiteSpace(Azure.TableServiceUri)))
                {
                    throw new InvalidOperationException(
                        $"{SectionName}:{nameof(Azure)} needs either a "
                        + $"{nameof(AzureOptions.ConnectionString)}, or both a "
                        + $"{nameof(AzureOptions.BlobServiceUri)} and a "
                        + $"{nameof(AzureOptions.TableServiceUri)} to go with the ambient credential.");
                }

                break;

            default:
                throw new InvalidOperationException(
                    $"{SectionName}:{nameof(Mode)} is not one of Local, Aws or Azure.");
        }

        if (Producer.CycleInterval < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(Producer)}:{nameof(ProducerOptions.CycleInterval)} cannot be negative.");
        }

        static void Require(string? value, string setting)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"{setting} has to be set.");
            }
        }
    }
}

/// <summary>The local filesystem mode: a directory, and a pretence that reaching it costs something.</summary>
public sealed class LocalOptions
{
    /// <summary>
    /// The directory blobs and announcements are kept under. Blobs go in <c>blobs/</c> and announcements
    /// in <c>watching/</c>.
    /// </summary>
    public string RootPath { get; set; } =
        Path.Combine(Path.GetTempPath(), "hollow-reference");

    /// <summary>
    /// How many announcement files to keep in the watching folder. Older ones are deleted as new ones
    /// arrive, because a producer cycling every ten seconds would otherwise leave a directory nobody
    /// can list.
    /// </summary>
    public int AnnouncementsToKeep { get; set; } = 50;

    public LatencyOptions Latency { get; set; } = new();
}

/// <summary>
/// The delay the local mode inserts before each operation, so that a laptop behaves a little like a
/// network.
/// </summary>
/// <remarks>
/// Without it the local mode is misleadingly well-behaved: a consumer that races the producer, or a
/// refresh that overlaps the next cycle, never happens on a directory that answers in microseconds.
/// Set both to zero to turn it off, which is what the tests do.
/// </remarks>
public sealed class LatencyOptions
{
    /// <summary>A fixed cost per request, standing in for the round trip.</summary>
    public TimeSpan PerOperation { get; set; } = TimeSpan.FromMilliseconds(25);

    /// <summary>A cost per megabyte moved, standing in for the bandwidth.</summary>
    public TimeSpan PerMegabyte { get; set; } = TimeSpan.FromMilliseconds(40);
}

/// <summary>The AWS mode: S3 for blobs and DynamoDB for the announcement, as in the Java original.</summary>
public sealed class AwsOptions
{
    /// <summary>
    /// The region, for example <c>eu-north-1</c>. Left empty, the SDK works it out from the environment
    /// the usual way.
    /// </summary>
    public string? Region { get; set; }

    /// <summary>
    /// A named profile from the shared credentials file. Left empty, the default credential chain is
    /// used — environment, profile, instance role — which is what a deployed service wants.
    /// </summary>
    /// <remarks>
    /// The Java original takes an <c>AWSCredentials</c> in every constructor and has no way to fall
    /// back to the chain, so it can only run somewhere a key has been pasted in.
    /// </remarks>
    public string? Profile { get; set; }

    /// <summary>The bucket blobs are written to.</summary>
    public string BucketName { get; set; } = string.Empty;

    /// <summary>
    /// The DynamoDB table the announced version is kept in, with a string partition key called
    /// <c>namespace</c>.
    /// </summary>
    public string TableName { get; set; } = string.Empty;

    /// <summary>
    /// An endpoint to talk to instead of the real service — LocalStack, MinIO, DynamoDB Local. Only for
    /// running the AWS mode without an AWS account.
    /// </summary>
    public string? ServiceUrl { get; set; }

    /// <summary>
    /// Whether to address buckets as a path segment rather than as a subdomain, which most S3-compatible
    /// servers require.
    /// </summary>
    public bool ForcePathStyle { get; set; }

    /// <summary>How often to ask what the announced version is. DynamoDB has nothing to push with.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// The Azure mode: Blob Storage for blobs and Table Storage for the announcement.
/// </summary>
/// <remarks>
/// Table Storage rather than Cosmos DB deliberately. Cosmos is the closer match to DynamoDB in shape,
/// but it bills for provisioned throughput whether or not a row is read, and this table holds one row
/// that changes once a cycle. Table Storage lives in the same storage account as the blobs, bills per
/// operation, and is the cheapest thing in Azure that can do a conditional write.
/// </remarks>
public sealed class AzureOptions
{
    /// <summary>
    /// A storage account connection string, which covers both blobs and tables. Simplest to get
    /// running; a managed identity is better once it is deployed.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// The blob endpoint, for example <c>https://account.blob.core.windows.net</c>, used with the
    /// ambient credential when no connection string is given.
    /// </summary>
    public string? BlobServiceUri { get; set; }

    /// <summary>The table endpoint, for example <c>https://account.table.core.windows.net</c>.</summary>
    public string? TableServiceUri { get; set; }

    /// <summary>The container blobs are written to. Created if it is not there.</summary>
    public string ContainerName { get; set; } = string.Empty;

    /// <summary>The table the announced version is kept in. Created if it is not there.</summary>
    public string TableName { get; set; } = string.Empty;

    /// <summary>How often to ask what the announced version is.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>What the producer does, beyond where it writes.</summary>
public sealed class ProducerOptions
{
    /// <summary>
    /// The least time between the start of one cycle and the start of the next. The Java original
    /// hard-codes ten seconds.
    /// </summary>
    public TimeSpan CycleInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Whether to restore the last announced version before the first cycle, so that the first blob
    /// published after a restart is a delta rather than an unrelated snapshot.
    /// </summary>
    public bool RestoreOnStartup { get; set; } = true;

    /// <summary>How many cycles to run before stopping, or zero to run until interrupted.</summary>
    public int MaxCycles { get; set; }

    public SourceOptions Source { get; set; } = new();
}

/// <summary>The shape of the fake catalogue the producer publishes.</summary>
public sealed class SourceOptions
{
    public int ActorCount { get; set; } = 999;

    public int MovieCount { get; set; } = 10_000;

    /// <summary>A seed, for a run that produces the same catalogue every time. Null for a random one.</summary>
    public int? Seed { get; set; }
}

/// <summary>What the consumer does, beyond where it reads.</summary>
public sealed class ConsumerOptions
{
    /// <summary>Where the explorer is mounted in the consumer's web application.</summary>
    public string ExplorerBasePath { get; set; } = "/explorer";

    /// <summary>Where the history is mounted.</summary>
    public string HistoryBasePath { get; set; } = "/history";

    /// <summary>
    /// How many past versions the history keeps before dropping the oldest. Each one costs whatever
    /// that transition changed, so this is a memory budget rather than a retention policy.
    /// </summary>
    public int MaxHistoricalStates { get; set; } = 1024;
}
