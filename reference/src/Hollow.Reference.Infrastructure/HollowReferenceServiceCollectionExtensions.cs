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

using Hollow.Api.Consumer;
using Hollow.Api.Producer;
using Hollow.Reference.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hollow.Reference.Infrastructure;

/// <summary>Puts a <see cref="HollowReferenceInfrastructure"/> and its parts into a service collection.</summary>
public static class HollowReferenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the infrastructure described by <paramref name="configuration"/>, along with each of
    /// the Hollow contracts it provides.
    /// </summary>
    /// <remarks>
    /// The infrastructure is built here rather than lazily, so that a configuration mistake is a
    /// message at start-up. The consumer application does not use this — it needs the built
    /// infrastructure before it can register the explorer, so it calls
    /// <see cref="HollowReferenceInfrastructure.Create"/> itself and registers the result.
    /// </remarks>
    public static IServiceCollection AddHollowReferenceInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        return services.AddHollowReferenceInfrastructure(
            HollowReferenceInfrastructure.Create(configuration));
    }

    /// <summary>Registers an already-built <paramref name="infrastructure"/> and each of its parts.</summary>
    public static IServiceCollection AddHollowReferenceInfrastructure(
        this IServiceCollection services, HollowReferenceInfrastructure infrastructure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(infrastructure);

        services.AddSingleton(infrastructure);
        services.AddSingleton(infrastructure.Options);
        services.AddSingleton(infrastructure.BlobStore);
        services.AddSingleton(infrastructure.AnnouncementStore);
        services.AddSingleton(infrastructure.Publisher);
        services.AddSingleton(infrastructure.Announcer);
        services.AddSingleton(infrastructure.BlobRetriever);
        services.AddSingleton(infrastructure.AnnouncementWatcher);

        return services;
    }
}
