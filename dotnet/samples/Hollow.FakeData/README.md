# Hollow.FakeData

The port of `hollow-fakedata`: a generator that publishes a fake book catalogue over a long delta
chain, and serves the explorer and the history UI over it while it does.

This is not a sample of how to use Hollow — the other four samples are that. It is what you point at
the explorer or the history when what they need is a dataset large enough and churny enough to be
worth looking at.

```
cd dotnet
dotnet run --project samples/Hollow.FakeData
```

The explorer is then at <http://127.0.0.1:7001> and the history at <http://127.0.0.1:7002>. Blobs go
under the temporary directory, in `fakehollowdata`, and are left there when the process stops.

## Scale

The defaults are Java's, and they are large: 10 000 books, each published in a random set of
countries — around half the list of 72 — so a cycle publishes roughly 300 000 book records, and there
are 100 cycles of it. Expect it to take a while and to want several gigabytes.

Every constant Java asks you to recompile is a command-line option here:

```
dotnet run --project samples/Hollow.FakeData -- --books 200 --cycles 10 --seed 7
```

| Option | Default | What it is |
| --- | --- | --- |
| `--blob-path` | `<temp>/fakehollowdata` | Where the blobs go |
| `--cycles` | `100` | Producer cycles — states in the delta chain |
| `--books` | `10000` | Books, before the per-country fan-out |
| `--artists` | `1000` | The pool the cover art draws from |
| `--max-adds` | `200` | Most books added in one cycle |
| `--max-removes` | `100` | Most books dropped in one cycle |
| `--max-modifications` | `1000` | Most books changed in one cycle |
| `--seed` | `20160101` | What makes a run reproducible |
| `--explorer-port` | `7001` | 0 picks a free one |
| `--history-port` | `7002` | 0 picks a free one |

## The data model

A book references an id, a country, its images and its metadata; the images are a map of size name to
a list of art; the metadata holds an enum and a list of chapters; a chapter holds a byte array and a
list of scenes; a scene holds a set of character names.

That shape is the point: object, list, set and map records, an inline scalar and referenced ones, an
enum and a byte array all appear, so every kind of page the explorer and the history have is
reachable. Three types declare a primary key — `Book` on `(Id, Country)`, `Artist` on `Name`,
`Chapter` on `ChapterId` — which is what lets the history follow a record from one version to the
next.

## Four things this port does differently

**One seeded random, so a run can be repeated.** Java calls `new Random()` at each use site and takes
its strings from javafaker, so no two runs produce the same catalogue — which makes anything you find
at cycle 60 impossible to go back to. Here every draw comes from one `Random` built from `--seed`,
the words are a handful of lists in `FakeText`, and the timestamps come from a fixed clock rather
than the wall clock. Same seed, same dataset, down to the ordinals.

**The adds-per-cycle entropy actually happens.** Java's `populateCatalog(start, count)` loops
`for (id = start; id < count; id++)`, treating the count as an end. The first call, from 1 to 10 000,
gets away with it. Every later call runs from a start already past 10 000 to a count below 200, so
its body never executes and no book is ever added after the first cycle. Here a count is a count.

**Dropping a cover size drops it.** Java's `modifyBook` calls `Map.remove` with a `Map.Entry` rather
than with a key, which matches nothing, so a book's art only ever grows. Here the key is removed.

**Artists are deduplicated by name.** Java collects them into a `HashSet`, which does nothing, since
`Artist` declares no equality — a thousand draws give a thousand entries however many names repeat.
`Artist` is keyed on its name, so repeats are duplicate keys; deduplicating on the name is what that
set was reaching for.

## Wiring worth copying

Two things in `Program.cs` are the general lesson rather than this generator's business.

The explorer is handed the **consumer**, not a read state, so it follows whatever the cycle loop
publishes. Its per-type heap figures are cached per state, and a delta does not change the state's
randomized tag — so a refresh listener has to call `ClearCache()`. Nothing else would tell it.

The history is built on the consumer's read state and told about each transition by the same
listener: `DeltaOccurred` when the consumer moved by delta, `DoubleSnapshotOccurred` when it fell far
enough behind to reload instead. Java's `HollowHistoryUIServer(consumer, port)` does this inside
itself; this port keeps the history and the UI apart, because a history is useful without a UI over
it, so the wiring is in the open where it can be read.
