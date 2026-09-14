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

using Hollow.Explorer.Controllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Hollow.Explorer.Diff;

/// <summary>
/// Putting the diff UI into an ASP.NET Core application.
/// </summary>
/// <remarks>
/// Java starts a server of its own, and routes several diffs under one router keyed by path. Here a
/// diff is four controller actions, so it goes into an application that already exists — and a host
/// wanting to show two diffs at once runs two applications, or maps the second one itself.
/// </remarks>
public static class HollowDiffUIExtensions
{
    /// <summary>
    /// Registers <paramref name="diffUI"/>'s pages, along with the MVC services they need.
    /// </summary>
    /// <param name="services">The application's services.</param>
    /// <param name="diffUI">The diff to show.</param>
    /// <param name="basePath">Where the pages should be mounted, or empty for the root.</param>
    public static IServiceCollection AddHollowDiffUI(
        this IServiceCollection services, HollowDiffUI diffUI, string basePath = "")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(diffUI);

        services.AddSingleton(diffUI);
        services.AddSingleton(new HollowDiffUIOptions { BasePath = basePath });
        services.AddSingleton<DiffSessionStore>();

        // The controller and its views live in this assembly rather than the host's, so MVC has to be
        // told to look here for them.
        services.AddControllersWithViews()
            .AddApplicationPart(typeof(DiffController).Assembly);

        return services;
    }

    /// <summary>
    /// Routes the diff's pages, at the path <see cref="AddHollowDiffUI"/> was given.
    /// </summary>
    public static IEndpointRouteBuilder MapHollowDiffUI(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        string basePath = endpoints.ServiceProvider.GetRequiredService<HollowDiffUIOptions>().BasePath;

        // The base path on its own is the overview, which is what a link to the diff points at.
        endpoints.MapControllerRoute(
            name: "hollow-diff-overview",
            pattern: basePath.Length == 0 ? "/" : basePath,
            defaults: new { controller = "Diff", action = "Overview" });

        endpoints.MapControllerRoute(
            name: "hollow-diff-resource",
            pattern: basePath + "/resource/{name}",
            defaults: new { controller = "Diff", action = "Resource" });

        endpoints.MapControllerRoute(
            name: "hollow-diff",
            pattern: basePath + "/{action}",
            defaults: new { controller = "Diff" });

        return endpoints;
    }
}
