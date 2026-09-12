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

using Hollow.Core.Read.Engine;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;
using Hollow.Explorer;

namespace Hollow.Tests.Explorer;

/// <summary>
/// A small dataset served by a running explorer, shared by the tests that read it.
/// </summary>
/// <remarks>
/// The tests go through HTTP rather than calling the controller, because the part of the explorer
/// ported from Velocity is the views — and a test that never renders one would not have exercised it.
/// </remarks>
public sealed class ExplorerFixture : IAsyncLifetime
{
    private HollowExplorerServer _server = null!;

    /// <summary>An actor, which several films share.</summary>
    public sealed record Actor(string Name, int? Age);

    /// <summary>A studio, whose single field makes it searchable through the reference.</summary>
    public sealed record Studio(string Name);

    /// <summary>A film, keyed by its id.</summary>
    [HollowPrimaryKey("Id")]
    public sealed record Film(
        int Id,
        string Title,
        int Year,
        Studio Studio,
        List<Actor> Cast);

    /// <summary>The state the explorer is serving.</summary>
    public HollowReadStateEngine ReadEngine { get; private set; } = null!;

    /// <summary>The explorer itself, for a test wanting to change what the header shows.</summary>
    public HollowExplorer Explorer => _server.Explorer;

    /// <summary>Where the explorer is listening.</summary>
    public Uri BaseAddress => _server.BaseAddress;

    /// <summary>
    /// A client of its own, with its own cookies — which is what makes one test's search invisible to
    /// the next, since the explorer keeps a reader's search against their session.
    /// </summary>
    public HttpClient NewClient() =>
        new(new HttpClientHandler { CookieContainer = new System.Net.CookieContainer() })
        {
            BaseAddress = BaseAddress,
        };

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        HollowWriteStateEngine writeEngine = new();
        HollowObjectMapper mapper = new(writeEngine);

        Studio warner = new("Warner Bros.");
        Studio a24 = new("A24");

        mapper.Add(new Film(
            1, "The Matrix", 1999, warner, [new Actor("Keanu Reeves", 34), new Actor("Carrie-Anne Moss", 32)]));
        mapper.Add(new Film(
            2, "Everything Everywhere All at Once", 2022, a24, [new Actor("Michelle Yeoh", 60)]));
        mapper.Add(new Film(
            3, "John Wick", 2014, null!, [new Actor("Keanu Reeves", 34)]));

        ReadEngine = StateEngineRoundTripper.RoundTripSnapshot(writeEngine);

        // Port 0 lets the operating system pick one, so that tests running beside each other do not
        // race for a fixed port.
        _server = new HollowExplorerServer(ReadEngine, 0);

        await _server.StartAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _server.StopAsync();
        await _server.DisposeAsync();
    }
}
