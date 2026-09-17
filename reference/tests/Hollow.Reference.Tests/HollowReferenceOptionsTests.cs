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

using Hollow.Reference.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace Hollow.Reference.Tests;

/// <summary>
/// The configuration, and the promise that it works out of the box and complains when it cannot.
/// </summary>
public sealed class HollowReferenceOptionsTests
{
    [Fact]
    public void AnEmptyConfigurationIsTheLocalModeAndIsValid()
    {
        // Out of the box means out of the box: nothing configured at all still runs, against a
        // directory under the temporary folder that the producer and the consumer both find.
        HollowReferenceOptions options = new();

        Assert.Equal(HollowInfrastructureMode.Local, options.Mode);
        Assert.False(string.IsNullOrWhiteSpace(options.Local.RootPath));

        options.Validate();
    }

    [Fact]
    public void ChoosingAwsWithoutABucketIsRefusedAtStartUp()
    {
        HollowReferenceOptions options = new() { Mode = HollowInfrastructureMode.Aws };

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("BucketName", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChoosingAwsWithoutATableIsRefusedToo()
    {
        HollowReferenceOptions options = new()
        {
            Mode = HollowInfrastructureMode.Aws,
            Aws = new AwsOptions { BucketName = "blobs" },
        };

        Assert.Contains(
            "TableName",
            Assert.Throws<InvalidOperationException>(options.Validate).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AzureNeedsEitherAConnectionStringOrBothServiceUris()
    {
        HollowReferenceOptions options = new()
        {
            Mode = HollowInfrastructureMode.Azure,
            Azure = new AzureOptions { ContainerName = "blobs", TableName = "announcements" },
        };

        Assert.Throws<InvalidOperationException>(options.Validate);

        options.Azure.ConnectionString = "UseDevelopmentStorage=true";
        options.Validate();

        options.Azure.ConnectionString = null;
        options.Azure.BlobServiceUri = "https://account.blob.core.windows.net";

        // Half of the credential-less arrangement is still not enough.
        Assert.Throws<InvalidOperationException>(options.Validate);

        options.Azure.TableServiceUri = "https://account.table.core.windows.net";
        options.Validate();
    }

    [Fact]
    public void TheShippedSettingsFileSelectsTheLocalMode()
    {
        // The file two directories up from the test project is the one both applications link.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonFile(SettingsFilePath(), optional: false)
            .Build();

        HollowReferenceOptions options = configuration
            .GetSection(HollowReferenceOptions.SectionName)
            .Get<HollowReferenceOptions>()!;

        Assert.Equal(HollowInfrastructureMode.Local, options.Mode);
        Assert.Equal("hollow-reference", options.Namespace);
        Assert.Equal(TimeSpan.FromSeconds(10), options.Producer.CycleInterval);

        // And the branches the local mode does not read are still filled in, so switching mode is a
        // one-word change rather than a scavenger hunt.
        Assert.False(string.IsNullOrWhiteSpace(options.Aws.BucketName));
        Assert.False(string.IsNullOrWhiteSpace(options.Azure.ContainerName));

        options.Validate();
    }

    [Fact]
    public void SwitchingModeIsOneSetting()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonFile(SettingsFilePath(), optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Hollow:Mode"] = "Aws" })
            .Build();

        HollowReferenceOptions options = configuration
            .GetSection(HollowReferenceOptions.SectionName)
            .Get<HollowReferenceOptions>()!;

        Assert.Equal(HollowInfrastructureMode.Aws, options.Mode);

        // Which is only true because the AWS branch was already filled in.
        options.Validate();
    }

    private static string SettingsFilePath() =>
        Path.Combine(AppContext.BaseDirectory, "appsettings.json");
}
