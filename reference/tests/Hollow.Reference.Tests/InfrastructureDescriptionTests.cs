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
/// What both applications print at start-up, which is the only way anyone finds the directory the local
/// mode rendezvous in.
/// </summary>
public sealed class InfrastructureDescriptionTests
{
    [Fact]
    public void TheDescriptionNamesTheDirectoriesTheLocalModeUses()
    {
        using TemporaryDirectory root = new();
        using HollowReferenceInfrastructure infrastructure = Create(root.Path);

        string described = string.Join("\n", infrastructure.Describe());

        Assert.Contains(Path.Combine(root.Path, "blobs"), described, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(root.Path, "watching"), described, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDescriptionNamesTheModeAndTheNamespace()
    {
        using TemporaryDirectory root = new();
        using HollowReferenceInfrastructure infrastructure = Create(root.Path);

        string described = string.Join("\n", infrastructure.Describe());

        Assert.Contains("Local", described, StringComparison.Ordinal);
        Assert.Contains("a-catalogue", described, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDescriptionSaysTheLocalAnnouncementFolderIsWatchedRatherThanPolled()
    {
        // Worth stating: it is the difference between this mode and the two cloud ones, and the reason
        // a consumer picks a version up the moment it is announced.
        using TemporaryDirectory root = new();
        using HollowReferenceInfrastructure infrastructure = Create(root.Path);

        Assert.Contains(
            "rather than polling",
            string.Join("\n", infrastructure.Describe()),
            StringComparison.Ordinal);
    }

    private static HollowReferenceInfrastructure Create(string rootPath) =>
        HollowReferenceInfrastructure.Create(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hollow:Mode"] = "Local",
                ["Hollow:Namespace"] = "a-catalogue",
                ["Hollow:Local:RootPath"] = rootPath,
                ["Hollow:Local:Latency:PerOperation"] = "00:00:00",
                ["Hollow:Local:Latency:PerMegabyte"] = "00:00:00",
            })
            .Build());
}
