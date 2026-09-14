# The diff sample

An ASP.NET Core application with the Hollow diff UI inside it. Two versions of a film catalogue are
published as blobs, read back, and compared; the diff is mounted at `/diff` so that whoever runs it
can see what moved between them.

```
dotnet run --project samples/Hollow.DiffUI.Sample
```

Then open <http://127.0.0.1:7102>. Pass `--urls http://127.0.0.1:8080` to serve somewhere else.

## What it shows

The whole of it is `Program.cs`, and the lines that matter are these:

```csharp
HollowDiff diff = new(fromState, toState);
diff.AddTypeDiff("Film", "Id");
diff.CalculateDiffs();

HollowDiffUI diffUI = new(diff, fromBlobName: "catalogue-v1", toBlobName: "catalogue-v2");
diffUI.AddMatchHint(new PrimaryKey("Actor", "Id"));

builder.Services.AddHollowDiffUI(diffUI, basePath: "/diff");
// ...
app.MapHollowDiffUI();
```

Everything else in the file is the application the diff is being put into, not the diff.

**Saying which record is which.** An ordinal means nothing across two states — the same film can sit
at ordinal 4 in one blob and 11 in the other, and two unrelated records can share an ordinal. So a
type is only compared if `AddTypeDiff` is told a key for it. A type without one still appears on the
overview, coloured yellow, but with nothing to say beyond its counts.

**Saying which element is which.** `AddMatchHint` answers the same question one level down: which
element of one film's cast is which of the other's. Without a hint, elements are paired by minimum
difference over every pairing — quadratic, and sometimes surprising, because a replaced actor reads
more cheaply as two changed names than as one removal and one addition.

**Calculating before serving.** `CalculateDiffs()` runs at startup rather than on the first request.
Over a real dataset it is the slow part, and hiding it behind a page load would only make the UI look
broken. What it produces is then cached per type, so paging through results does not redo it.

**Mounting it.** The diff goes under `/diff`, and the application keeps `/` for itself. Every link the
diff writes is built from that base path, so nothing has to be rewritten by a proxy. Mounting it into
a process that already holds both states is also what puts it behind whatever authentication that
process has — the diff UI has none of its own, and shows every field of every record.

**No generated client.** There is no `[HollowGeneratedApi]` anywhere in this sample and the source
generator is not referenced. A diff is read from schemas rather than classes, so it can be pointed at
two blobs whose model the process has never seen.

## What the two versions differ by

Each change is a different thing for the diff to find, and between them they fill all four pages:

| Change | Where it shows |
| --- | --- |
| *John Wick* retitled, and its year moved with it | two fields on the type page, one pair on each field page |
| *The Matrix* swaps a cast member | a collection paired element by element, one gone and one arrived |
| Warner Bros.'s country corrected | a change inside a shared record, so it reaches both films that reference it |
| *Seven Samurai* dropped, *Everything Everywhere* added | the unmatched columns on the type page |

## Reading a record

The record page draws two records side by side, as a flat indented list rather than nested markup —
which is what keeps a record a dozen levels deep readable in a table of two cells per row.

It opens at what differs and leaves everything else folded away. Clicking a folded row asks the
server for that row's children and splices them in; clicking an open one drops them again. That is
why the record page loads at all on a record reaching thousands of others: the tree beneath a row is
built when something asks for it, and never for a subtree the two states share exactly.
