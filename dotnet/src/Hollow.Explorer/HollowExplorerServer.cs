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

using System.Globalization;
using Hollow.Api.Consumer;
using Hollow.Core.Read.Engine;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hollow.Explorer;

/// <summary>
/// The explorer on a port of its own, for looking at a dataset without an application to put it in.
/// </summary>
/// <remarks>
/// This is a whole web server, so it is for a developer looking at their own data on their own
/// machine. Inside something already serving requests, <see cref="HollowExplorerExtensions"/> is the
/// way in — and is the one that puts the explorer behind whatever authentication that application has.
/// </remarks>
public sealed class HollowExplorerServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    /// <summary>Serves <paramref name="stateEngine"/> on <paramref name="port"/>.</summary>
    public HollowExplorerServer(HollowReadStateEngine stateEngine, int port)
        : this(new HollowExplorer(stateEngine), port)
    {
    }

    /// <summary>Serves whatever <paramref name="consumer"/> holds, on <paramref name="port"/>.</summary>
    public HollowExplorerServer(HollowConsumer consumer, int port)
        : this(new HollowExplorer(consumer), port)
    {
    }

    /// <summary>Serves <paramref name="explorer"/> on <paramref name="port"/>.</summary>
    public HollowExplorerServer(HollowExplorer explorer, int port)
    {
        ArgumentNullException.ThrowIfNull(explorer);

        Explorer = explorer;
        Port = port;

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        // The loopback address rather than the name "localhost", for two reasons: a dev tool showing a
        // whole dataset has no business being reachable from the network, and Kestrel will not take an
        // ephemeral port under the name.
        builder.WebHost.UseUrls(
            string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}"));

        // A tool a developer runs to read their own data has nothing useful to say on the way up, and
        // the request log would bury whatever it did.
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services.AddHollowExplorer(explorer);

        _app = builder.Build();
        _app.MapHollowExplorer();
    }

    /// <summary>The dataset being served.</summary>
    public HollowExplorer Explorer { get; }

    /// <summary>
    /// The port the pages are served on, or 0 to let the operating system pick one — in which case
    /// <see cref="BaseAddress"/> says which it picked.
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// The address the pages are served at, once <see cref="StartAsync"/> has bound a port.
    /// </summary>
    /// <exception cref="InvalidOperationException">The server has not started.</exception>
    public Uri BaseAddress =>
        _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?.Addresses.Select(address => new Uri(address)).FirstOrDefault()
        ?? throw new InvalidOperationException("the server has not started listening yet");

    /// <summary>Starts serving, returning once the port is accepting connections.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default) =>
        _app.StartAsync(cancellationToken);

    /// <summary>Stops serving, returning once in-flight requests have finished.</summary>
    public Task StopAsync(CancellationToken cancellationToken = default) =>
        _app.StopAsync(cancellationToken);

    /// <summary>Serves until the process is asked to shut down.</summary>
    public Task RunAsync() => _app.RunAsync();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
