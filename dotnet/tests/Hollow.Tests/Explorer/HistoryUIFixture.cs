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
using Hollow.Core.Tools.History;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;
using Hollow.Explorer.History;

namespace Hollow.Tests.Explorer;

/// <summary>
/// Three versions of a small dataset, the history built across them, and a running history UI over it.
/// </summary>
/// <remarks>
/// The tests go through HTTP rather than calling the controller, because the part of the history UI
/// ported from Velocity is the views — and a test that never renders one would not have exercised it.
/// </remarks>
public sealed class HistoryUIFixture : IAsyncLifetime
{
    private HollowHistoryUIServer _server = null!;

    /// <summary>A studio, referenced by the films it made.</summary>
    public sealed record Studio(string Name, string Country);

    /// <summary>An actor, which several films share.</summary>
    public sealed record Actor(int Id, string Name);

    /// <summary>A film, keyed by its id and the country of the studio that made it.</summary>
    [HollowPrimaryKey("Id", "Studio.Country")]
    public sealed record Film(int Id, string Title, int Year, Studio Studio, List<Actor> Cast);

    /// <summary>The versions the history holds, oldest first.</summary>
    public static IReadOnlyList<long> Versions { get; } =
        [20240115093000123L, 20240116093000123L, 20240117093000123L];

    /// <summary>The history being served.</summary>
    public HollowHistory History { get; private set; } = null!;

    /// <summary>Where the history UI is listening.</summary>
    public Uri BaseAddress => _server.BaseAddress;

    /// <summary>
    /// A client of its own, with its own cookies — which is what makes one test's open group, and the
    /// record it left open, invisible to the next.
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
        Studio pathe = new("Pathe", "FR");

        Producer producer = new();

        // v1: the starting point.
        producer.WriteCycle(
            new Film(1, "The Matrix", 1999, warner, [new Actor(10, "Keanu Reeves"), new Actor(11, "Carrie-Anne Moss")]),
            new Film(2, "John Wick", 2014, warner, [new Actor(10, "Keanu Reeves")]),
            new Film(3, "The Lobster", 2015, a24, [new Actor(12, "Colin Farrell")]),
            new Film(5, "Amelie", 2001, pathe, [new Actor(15, "Audrey Tautou")]));

        HollowReadStateEngine consumer = producer.ReadSnapshot();

        History = new HollowHistory(consumer, Versions[0], 10);

        // v2: one film's cast changes, one is dropped, one arrives.
        producer.NextCycle();
        producer.WriteCycle(
            new Film(1, "The Matrix", 1999, warner, [new Actor(10, "Keanu Reeves"), new Actor(13, "Laurence Fishburne")]),
            new Film(2, "John Wick", 2014, warner, [new Actor(10, "Keanu Reeves")]),
            new Film(4, "Everything Everywhere All at Once", 2022, a24, [new Actor(14, "Michelle Yeoh")]),
            new Film(5, "Amelie", 2001, pathe, [new Actor(15, "Audrey Tautou")]));

        producer.ApplyDelta(consumer);
        History.DeltaOccurred(Versions[1]);

        // v3: a title changes, so that one version has a modification and nothing else.
        producer.NextCycle();
        producer.WriteCycle(
            new Film(1, "The Matrix", 1999, warner, [new Actor(10, "Keanu Reeves"), new Actor(13, "Laurence Fishburne")]),
            new Film(2, "John Wick: Chapter 2", 2017, warner, [new Actor(10, "Keanu Reeves")]),
            new Film(4, "Everything Everywhere All at Once", 2022, a24, [new Actor(14, "Michelle Yeoh")]),
            new Film(5, "Amelie", 2001, pathe, [new Actor(15, "Audrey Tautou")]));

        producer.ApplyDelta(consumer);
        History.DeltaOccurred(Versions[2]);

        HollowHistoryUI historyUI = new(History);
        historyUI.AddMatchHint(new PrimaryKey("Actor", "Id"));

        // Port 0 lets the operating system pick one, so that tests running beside each other do not
        // race for a fixed port.
        _server = new HollowHistoryUIServer(historyUI, 0);

        await _server.StartAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _server.StopAsync();
        await _server.DisposeAsync();
    }

    /// <summary>A producer of films, held across cycles so that a delta can be written.</summary>
    private sealed class Producer
    {
        private readonly HollowWriteStateEngine _writeEngine = new();
        private readonly HollowObjectMapper _mapper;

        internal Producer()
        {
            _mapper = new HollowObjectMapper(_writeEngine);
            _mapper.InitializeTypeState(typeof(Film));
        }

        internal void WriteCycle(params Film[] films)
        {
            foreach (Film film in films)
            {
                _mapper.Add(film);
            }
        }

        internal void NextCycle() => _writeEngine.PrepareForNextCycle();

        internal HollowReadStateEngine ReadSnapshot()
        {
            using MemoryStream blob = new();

            new HollowBlobWriter(_writeEngine).WriteSnapshot(blob);
            blob.Position = 0;

            HollowReadStateEngine engine = new();
            new HollowBlobReader(engine).ReadSnapshot(blob);

            return engine;
        }

        internal void ApplyDelta(HollowReadStateEngine consumer)
        {
            using MemoryStream blob = new();

            new HollowBlobWriter(_writeEngine).WriteDelta(blob);
            blob.Position = 0;

            new HollowBlobReader(consumer).ApplyDelta(blob);
        }
    }
}
