# The history sample

An ASP.NET Core application with the Hollow history UI inside it. A film catalogue is published four
times and followed through the three deltas between them; the history is mounted at `/history` so
that whoever runs it can ask what happened to a record and when.

```
dotnet run --project samples/Hollow.HistoryUI.Sample
```

Then open <http://127.0.0.1:7103>. Pass `--urls http://127.0.0.1:8080` to serve somewhere else.

## What it shows

The whole of it is `Program.cs`, and the lines that matter are these:

```csharp
HollowHistory history = new(consumer, firstVersion, maxHistoricalStatesToKeep: 1024);

foreach ((long version, IReadOnlyList<Film> films) in Catalogue.Versions.Skip(1))
{
    producer.PublishDelta((version, films), consumer);
    history.DeltaOccurred(version);
}

HollowHistoryUI historyUI = new(history) { OverviewDisplayHeaders = ["catalogue.source"] };
historyUI.AddMatchHint(new PrimaryKey("Actor", "Id"));

builder.Services.AddHollowHistoryUI(historyUI, basePath: "/history");
// ...
app.MapHollowHistoryUI();
```

Everything else in the file is the application the history is being put into, not the history.

**A history follows a consumer, it does not read blobs.** It is built on top of a read state as that
state moves, and is told after each transition which version it moved to. `DeltaOccurred` is where the
work happens: it takes a copy of the records that transition dropped, because nothing else will have
them and a moment later the space they occupied is the read state's to reuse. Call it after the delta
has been applied, with the version just moved to.

**Only what is kept is copied.** A historical state holds the records the *next* transition removed,
and nothing else. Everything still current is answered by walking forward along the chain to whichever
later state still has it, ending at the live read state. That is why a long run of versions usually
fits in memory: a record is stored once, in the state that last had it, however many versions it
survived.

**Saying which record is which.** Types are discovered from the data model: every type whose schema
declares a primary key is followed. A key is what lets an ordinal in one version be recognised in
another — without one, a history can say that something at an ordinal changed and nothing more. Pass
`autoDiscoverTypeIndex: false` and use `history.KeyIndex` to name the types by hand instead.

**Saying which element is which.** `AddMatchHint` answers the same question one level down: which
element of one film's cast is which of the other's. Without a hint, elements are paired by minimum
difference over every pairing — quadratic, and sometimes surprising, because a replaced actor reads
more cheaply as two changed names than as one removal and one addition.

**Mounting it.** The history goes under `/history`, and the application keeps `/` for itself. Every
link it writes is built from that base path, so nothing has to be rewritten by a proxy. Mounting it
into a process that is already following the delta chain is also what puts it behind whatever
authentication that process has — the history UI has none of its own, and shows every field of every
record.

**No generated client.** There is no `[HollowGeneratedApi]` anywhere in this sample and the source
generator is not referenced. A history is read from schemas rather than classes, so it can follow a
chain whose model the process has never seen.

**Reading versions as moments.** The versions here are clock-stamped — `20240117093000000` is
2024-01-17 09:30 UTC — which is how a producer usually numbers them, and is what lets the pages show a
moment instead of a seventeen-digit number. A version that does not read as a timestamp is shown
unchanged. The zone is the caller's: `new HollowHistoryUI(history, timeZone)`, defaulting to UTC.

## What the four versions differ by

The history is initialised at the first version, so that one has no line of its own on the overview:
there is no transition into it to describe.

| Transition | What moved | Where it shows |
| --- | --- | --- |
| v1 → v2 | *Seven Samurai* dropped, *Everything Everywhere* added, *The Matrix* swaps a cast member | one of each thing the overview counts |
| v2 → v3 | *John Wick* retitled, its year moved with it | a single record changing, and nothing else |
| v3 → v4 | Warner Bros.'s country corrected, inside a record two films share | one change reaching two films at once |

*Amelie* is in every version and never changes, which is what makes a search for it worth trying.

## Two things the counts will surprise you with

Both are the history being right rather than the sample being odd, and both are easier to understand
once seen than described.

**The overview counts every followed type, not just `Film`.** `Actor` declares a primary key too, so
it is followed too, and the cast change at v2 moves two actors as well as one film. The overview's
line for v2 therefore reads three added and three removed rather than one of each — open the version
to see it split by type.

**A key is part of a record's identity.** `Film` is keyed by `("Id", "Studio.Country")`, which is what
gives the state-type page something to group by; with a single-field key there is nothing to group.
It also makes the last transition read the way it does. Correcting Warner Bros.'s country from `US` to
`USA` changes the *key* of both films that reference it, so v4 counts two removals and two additions
and no modifications at all: under the model as declared, `(2, US)` and `(2, USA)` are two different
records. Searching for `2` finds both of them for the same reason.

That is what a primary key means. Putting a field in the key that can be corrected later is worth
thinking about twice, and this sample is arranged so that the consequence is visible rather than
described.

## Following a record

Put an id in the lookup box at the top of any page. `2` finds *John Wick*, which changed twice; `5`
finds *Amelie*, which never changed and so is reported as such rather than as an empty table.

A result links to the record page for the version it changed in, which draws the record as it stood on
either side of that transition — the same page the diff UI draws, over two states of one chain rather
than two unrelated ones. It opens at what differs and leaves the rest folded away; clicking a folded
row asks the server for its children. The record page also lists every other version that touched the
same record, so a record can be followed through the history a version at a time.
