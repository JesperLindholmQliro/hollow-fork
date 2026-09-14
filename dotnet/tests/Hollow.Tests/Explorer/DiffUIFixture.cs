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

using Hollow.Core.Index.Key;
using Hollow.Core.Read.Engine;
using Hollow.Core.Tools.Diff;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;
using Hollow.Explorer.Diff;

namespace Hollow.Tests.Explorer;

/// <summary>
/// Two small datasets, a calculated diff between them, and a running diff UI over it.
/// </summary>
/// <remarks>
/// The tests go through HTTP rather than calling the controller, because the part of the diff UI
/// ported from Velocity is the views — and a test that never renders one would not have exercised it.
/// </remarks>
public sealed class DiffUIFixture : IAsyncLifetime
{
    private HollowDiffUIServer _server = null!;

    /// <summary>A studio, referenced by the films it made.</summary>
    public sealed record Studio(string Name, string Country);

    /// <summary>An actor, which several films share.</summary>
    public sealed record Actor(int Id, string Name);

    /// <summary>A film, keyed by its id.</summary>
    [HollowPrimaryKey("Id")]
    public sealed record Film(int Id, string Title, int Year, Studio Studio, List<Actor> Cast);

    /// <summary>The diff being served.</summary>
    public HollowDiff Diff { get; private set; } = null!;

    /// <summary>Where the diff UI is listening.</summary>
    public Uri BaseAddress => _server.BaseAddress;

    /// <summary>
    /// A client of its own, with its own cookies — which is what makes one test's place in a list, and
    /// the record pair it left open, invisible to the next.
    /// </summary>
    public HttpClient NewClient() =>
        new(new HttpClientHandler { CookieContainer = new System.Net.CookieContainer() })
        {
            BaseAddress = BaseAddress,
        };

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        Studio warner = new("Warner Bros.", "US");
        Studio a24 = new("A24", "US");

        // The later state changes a field of one film, drops another, and adds a third — one of each
        // thing the overview counts.
        HollowReadStateEngine from = Publish(
            new Film(1, "The Matrix", 1999, warner, [new Actor(10, "Keanu Reeves"), new Actor(11, "Carrie-Anne Moss")]),
            new Film(2, "John Wick", 2014, warner, [new Actor(10, "Keanu Reeves")]),
            new Film(3, "The Lobster", 2015, a24, [new Actor(12, "Colin Farrell")]));

        HollowReadStateEngine to = Publish(
            new Film(1, "The Matrix", 1999, warner, [new Actor(10, "Keanu Reeves"), new Actor(13, "Laurence Fishburne")]),
            new Film(2, "John Wick: Chapter 2", 2017, warner, [new Actor(10, "Keanu Reeves")]),
            new Film(4, "Everything Everywhere All at Once", 2022, a24, [new Actor(14, "Michelle Yeoh")]));

        Diff = new HollowDiff(from, to);
        Diff.AddTypeDiff("Film", "Id");
        Diff.CalculateDiffs();

        HollowDiffUI diffUI = new(Diff, "from-blob", "to-blob");
        diffUI.AddMatchHint(new PrimaryKey("Actor", "Id"));

        // Port 0 lets the operating system pick one, so that tests running beside each other do not
        // race for a fixed port.
        _server = new HollowDiffUIServer(diffUI, 0);

        await _server.StartAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _server.StopAsync();
        await _server.DisposeAsync();
    }

    private static HollowReadStateEngine Publish(params Film[] films)
    {
        HollowWriteStateEngine writeEngine = new();
        HollowObjectMapper mapper = new(writeEngine);

        mapper.InitializeTypeState(typeof(Film));

        foreach (Film film in films)
        {
            mapper.Add(film);
        }

        return StateEngineRoundTripper.RoundTripSnapshot(writeEngine);
    }
}
