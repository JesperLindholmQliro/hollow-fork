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
using Hollow.DiffUI.Sample;
using Hollow.Explorer.Diff;

// An ASP.NET Core application with the Hollow diff UI inside it.
//
// Two versions of a film catalogue are published as blobs, read back, and compared; the diff is
// mounted at /diff so that whoever runs this can see what moved between them. This is the arrangement
// the diff UI is for: it goes in a process that already has both states, and therefore already has
// whatever authentication guards them.

// ── The two states ────────────────────────────────────────────────────────────────────────────────

HollowReadStateEngine fromState = Publish(Catalogue.Version1);
HollowReadStateEngine toState = Publish(Catalogue.Version2);

// ── The diff ──────────────────────────────────────────────────────────────────────────────────────

HollowDiff diff = new(fromState, toState);

// An ordinal means nothing across two states, so a type can only be compared if something says which
// record is which. This is the one thing worth getting right: without it a type shows up on the
// overview as uncomparable, coloured yellow, with nothing but its counts.
diff.AddTypeDiff("Film", "Id");
diff.AddTypeDiff("Studio", "Name");

// Calculated up front, and deliberately not while a browser waits: over a real dataset this is the
// slow part, and hiding it behind the first page load would only make the UI look broken.
diff.CalculateDiffs();

HollowDiffUI diffUI = new(diff, fromBlobName: "catalogue-v1", toBlobName: "catalogue-v2");

// The same question one level down: which element of one film's cast is which of the other's. Without
// a hint the elements are paired by minimum difference, which is quadratic and sometimes surprising —
// a replaced actor can read as two changed names rather than one removal and one addition.
diffUI.AddMatchHint(new PrimaryKey("Actor", "Id"));

// ── The application ───────────────────────────────────────────────────────────────────────────────

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

if (builder.Configuration["urls"] is null)
{
    builder.WebHost.UseUrls("http://127.0.0.1:7102");
}

builder.Services.AddHollowDiffUI(diffUI, basePath: "/diff");

WebApplication app = builder.Build();

// The application's own page, which is all the rest of this sample is: somewhere to come back to.
app.MapGet("/", () => Results.Content(HomePage(diff), "text/html; charset=utf-8"));

app.MapHollowDiffUI();

app.Run();

// Writes a snapshot of the catalogue and reads it back, which is what a producer and a consumer do
// between them — the diff wants two read states, not two write states.
static HollowReadStateEngine Publish(IReadOnlyList<Film> films)
{
    HollowWriteStateEngine writeEngine = new();
    HollowObjectMapper mapper = new(writeEngine);

    // Declared rather than inferred from the records, so that both states have the same schema even
    // where one of them happens to hold no record of some type.
    mapper.InitializeTypeState(typeof(Film));

    foreach (Film film in films)
    {
        mapper.Add(film);
    }

    using MemoryStream blob = new();
    new HollowBlobWriter(writeEngine).WriteSnapshot(blob);
    blob.Position = 0;

    HollowReadStateEngine readEngine = new();
    new HollowBlobReader(readEngine).ReadSnapshot(blob);

    return readEngine;
}

static string HomePage(HollowDiff diff)
{
    HollowTypeDiff films = diff.GetTypeDiff("Film")!;

    return $"""
        <!DOCTYPE html>
        <html lang="en">
        <head><title>Film catalogue diff</title></head>
        <body>
            <h1>Film catalogue</h1>
            <p>Two versions of a catalogue, compared. Films:
               <b>{films.TotalItemsInFromState}</b> before, <b>{films.TotalItemsInToState}</b> after,
               <b>{films.UnmatchedOrdinalsInFrom.Count}</b> gone and
               <b>{films.UnmatchedOrdinalsInTo.Count}</b> arrived.</p>
            <p><a href="/diff">Open the diff &#8594;</a></p>
            <p>
                Start at the overview, open <b>Film</b>, and follow a field down to a pair of records.
                The record view opens at what differs and leaves the rest folded away; clicking a row
                asks the server for its children, so a record reaching thousands of others still
                loads.
            </p>
        </body>
        </html>
        """;
}
