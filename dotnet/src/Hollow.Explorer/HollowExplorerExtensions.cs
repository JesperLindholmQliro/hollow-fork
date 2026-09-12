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

namespace Hollow.Explorer;

/// <summary>
/// Putting the explorer into an ASP.NET Core application.
/// </summary>
/// <remarks>
/// Java starts a server of its own because a servlet container is not something you add to a running
/// process. Here the explorer is four controller actions and four views, so it can just as well go
/// into an application that already exists — which is usually the one already holding the dataset.
/// </remarks>
public static class HollowExplorerExtensions
{
    /// <summary>
    /// Registers the explorer over <paramref name="explorer"/>, along with the MVC services its pages
    /// need.
    /// </summary>
    /// <param name="services">The application's services.</param>
    /// <param name="explorer">The dataset to explore.</param>
    /// <param name="basePath">Where the pages should be mounted, or empty for the root.</param>
    public static IServiceCollection AddHollowExplorer(
        this IServiceCollection services, HollowExplorer explorer, string basePath = "")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(explorer);

        services.AddSingleton(explorer);
        services.AddSingleton(new HollowExplorerOptions { BasePath = basePath });
        services.AddSingleton<ExplorerSessionStore>();

        // The controller and its views live in this assembly rather than the host's, so MVC has to be
        // told to look here for them.
        services.AddControllersWithViews()
            .AddApplicationPart(typeof(ExplorerController).Assembly);

        return services;
    }

    /// <summary>
    /// Routes the explorer's pages, at the path <see cref="AddHollowExplorer"/> was given.
    /// </summary>
    public static IEndpointRouteBuilder MapHollowExplorer(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        string basePath = endpoints.ServiceProvider.GetRequiredService<HollowExplorerOptions>().BasePath;

        // The base path on its own is the home page, which is what a link to the explorer points at.
        endpoints.MapControllerRoute(
            name: "hollow-explorer-home",
            pattern: basePath.Length == 0 ? "/" : basePath,
            defaults: new { controller = "Explorer", action = "Home" });

        endpoints.MapControllerRoute(
            name: "hollow-explorer",
            pattern: basePath + "/{action}",
            defaults: new { controller = "Explorer" });

        return endpoints;
    }
}
