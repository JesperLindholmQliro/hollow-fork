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

namespace Hollow.Explorer.History;

/// <summary>
/// Putting the history UI into an ASP.NET Core application.
/// </summary>
public static class HollowHistoryUIExtensions
{
    /// <summary>
    /// Registers <paramref name="historyUI"/>'s pages, along with the MVC services they need.
    /// </summary>
    /// <param name="services">The application's services.</param>
    /// <param name="historyUI">The history to show.</param>
    /// <param name="basePath">Where the pages should be mounted, or empty for the root.</param>
    public static IServiceCollection AddHollowHistoryUI(
        this IServiceCollection services, HollowHistoryUI historyUI, string basePath = "")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(historyUI);

        services.AddSingleton(historyUI);
        services.AddSingleton(new HollowHistoryUIOptions { BasePath = basePath });
        services.AddSingleton<HistorySessionStore>();

        // The controller and its views live in this assembly rather than the host's, so MVC has to be
        // told to look here for them.
        services.AddControllersWithViews()
            .AddApplicationPart(typeof(HistoryController).Assembly);

        return services;
    }

    /// <summary>
    /// Routes the history's pages, at the path <see cref="AddHollowHistoryUI"/> was given.
    /// </summary>
    public static IEndpointRouteBuilder MapHollowHistoryUI(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        string basePath = endpoints.ServiceProvider.GetRequiredService<HollowHistoryUIOptions>().BasePath;

        // The base path on its own is the overview, which is what a link to the history points at.
        endpoints.MapControllerRoute(
            name: "hollow-history-overview",
            pattern: basePath.Length == 0 ? "/" : basePath,
            defaults: new { controller = "History", action = "Overview" });

        endpoints.MapControllerRoute(
            name: "hollow-history-resource",
            pattern: basePath + "/resource/{name}",
            defaults: new { controller = "History", action = "Resource" });

        endpoints.MapControllerRoute(
            name: "hollow-history",
            pattern: basePath + "/{action}",
            defaults: new { controller = "History" });

        return endpoints;
    }
}
