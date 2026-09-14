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
using Hollow.Core.Index.Key;
using Hollow.Core.Read.Engine;
using Hollow.Core.Tools.History;
using Hollow.Core.Write;
using Hollow.Core.Write.ObjectMapper;
using Hollow.Explorer.History;
using Hollow.HistoryUI.Sample;

// An ASP.NET Core application with the Hollow history UI inside it.
//
// A film catalogue is published four times and followed through the three deltas between them; the
// history is mounted at /history so that whoever runs this can ask what happened to a record and
// when. This is the arrangement the history UI is for: it goes in a process that is already
// following the delta chain, and therefore already has whatever authentication guards it.

// ── The producer and the consumer ─────────────────────────────────────────────────────────────────

Producer producer = new();
HollowReadStateEngine consumer = producer.PublishSnapshot(Catalogue.Versions[0]);

// ── The history ───────────────────────────────────────────────────────────────────────────────────

// A history is built on top of a read state as it moves, not from a pile of blobs. It starts at
// whatever the consumer is on, and is told after each transition what version the consumer moved to.
//
// The types to follow are discovered from the data model: every type whose schema declares a primary
// key is followed, because a key is what lets an ordinal in one version be recognised in another.
// Without one, a history has nothing to say about a record beyond that something at that ordinal
// changed. Pass autoDiscoverTypeIndex: false and use history.KeyIndex to name the types by hand.
HollowHistory history = new(consumer, Catalogue.Versions[0].Version, maxHistoricalStatesToKeep: 1024);

foreach ((long version, IReadOnlyList<Film> films) in Catalogue.Versions.Skip(1))
{
    producer.PublishDelta((version, films), consumer);

    // Called after the delta has been applied, with the version just moved to. This is where the
    // history takes a copy of the records that transition dropped: nothing else will have them, and
    // a moment later the space they occupy is the read state's to reuse.
    history.DeltaOccurred(version);
}

// ── The UI ────────────────────────────────────────────────────────────────────────────────────────

HollowHistoryUI historyUI = new(history)
{
    // Header tags to show a column of on the overview. A producer usually stamps the blob with
    // whatever identifies the run that made it, and seeing it beside the change counts is how a
    // reader works out which run caused what.
    OverviewDisplayHeaders = ["catalogue.source"],
};

// Which element of one film's cast is which of the other's. Without a hint the elements are paired by
// minimum difference, which is quadratic and sometimes surprising — a replaced actor can read as two
// changed names rather than one removal and one addition.
historyUI.AddMatchHint(new PrimaryKey("Actor", "Id"));

// ── The application ───────────────────────────────────────────────────────────────────────────────

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

if (builder.Configuration["urls"] is null)
{
    builder.WebHost.UseUrls("http://127.0.0.1:7103");
}

builder.Services.AddHollowHistoryUI(historyUI, basePath: "/history");

WebApplication app = builder.Build();

// The application's own page, which is all the rest of this sample is: somewhere to come back to.
app.MapGet("/", () => Results.Content(HomePage(history), "text/html; charset=utf-8"));

app.MapHollowHistoryUI();

app.Run();

static string HomePage(HollowHistory history)
{
    string versions = string.Join(
        "",
        history.HistoricalStates.Select(state => string.Create(
            CultureInfo.InvariantCulture,
            $"""<li><a href="/history/state?version={state.Version}">{state.Version}</a></li>""")));

    return $"""
        <!DOCTYPE html>
        <html lang="en">
        <head><title>Film catalogue history</title></head>
        <body>
            <h1>Film catalogue</h1>
            <p>Four versions of a catalogue, followed through the three deltas between them.
               The history holds <b>{history.NumberOfHistoricalStates}</b> states, newest first:</p>
            <ul>{versions}</ul>
            <p><a href="/history">Open the history &#8594;</a></p>
            <p>
                Start at the overview and open a version to see which types changed in it, or put a
                film's id in the lookup box to see every version that ever touched it. Searching for
                <b>2</b> finds <i>John Wick</i>, which changed twice; searching for <b>5</b> finds
                <i>Amelie</i>, which never changed at all.
            </p>
        </body>
        </html>
        """;
}

// Publishes the catalogue the way a producer and a consumer do between them. A history is built on a
// delta chain, so the write engine is kept across cycles: a delta is only meaningful relative to the
// state before it.
internal sealed class Producer
{
    private readonly HollowWriteStateEngine _writeEngine = new();
    private readonly HollowObjectMapper _mapper;

    internal Producer()
    {
        _mapper = new HollowObjectMapper(_writeEngine);

        // Declared rather than inferred from the records, so that every version has the same schema
        // even where one of them happens to hold no record of some type.
        _mapper.InitializeTypeState(typeof(Film));
    }

    internal HollowReadStateEngine PublishSnapshot((long Version, IReadOnlyList<Film> Films) cycle)
    {
        WriteCycle(cycle);

        using MemoryStream blob = new();
        new HollowBlobWriter(_writeEngine).WriteSnapshot(blob);
        blob.Position = 0;

        HollowReadStateEngine readEngine = new();
        new HollowBlobReader(readEngine).ReadSnapshot(blob);

        return readEngine;
    }

    internal void PublishDelta((long Version, IReadOnlyList<Film> Films) cycle, HollowReadStateEngine consumer)
    {
        _writeEngine.PrepareForNextCycle();
        WriteCycle(cycle);

        using MemoryStream blob = new();
        new HollowBlobWriter(_writeEngine).WriteDelta(blob);
        blob.Position = 0;

        new HollowBlobReader(consumer).ApplyDelta(blob);
    }

    private void WriteCycle((long Version, IReadOnlyList<Film> Films) cycle)
    {
        // A header tag is how a producer says something about the run that made a blob. The overview
        // shows a column of whichever tags it was asked for.
        _writeEngine.AddHeaderTag(
            "catalogue.source", string.Create(CultureInfo.InvariantCulture, $"nightly-{cycle.Version}"));

        foreach (Film film in cycle.Films)
        {
            _mapper.Add(film);
        }
    }
}
