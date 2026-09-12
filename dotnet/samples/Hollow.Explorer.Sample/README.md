# The explorer sample

An ASP.NET Core application with the Hollow explorer inside it. The application has a dataset — a
producer publishing a film catalogue, a consumer following it — and mounts the explorer at `/hollow`
so that whoever runs it can look at that dataset without writing a page for it.

```
dotnet run --project samples/Hollow.Explorer.Sample
```

Then open <http://127.0.0.1:7101>. Pass `--urls http://127.0.0.1:8080` to serve somewhere else. The
blobs go to a temporary directory and are deleted when the application stops.

## What it shows

The whole of it is `Program.cs`, and the four lines that matter are these:

```csharp
HollowExplorer explorer = new(consumer);

builder.Services.AddHollowExplorer(explorer, basePath: "/hollow");
// ...
app.MapHollowExplorer();
```

Everything else in the file is the application the explorer is being put into, not the explorer.

**Mounting it.** The explorer goes under `/hollow`, and the application keeps `/` for itself. Every
link the explorer writes is built from that base path, so nothing has to be rewritten by a proxy.
Mounting it into the process that already holds the data is also what puts it behind whatever
authentication that process already has — the explorer has none of its own, and shows everything.

**Following a consumer.** `new HollowExplorer(consumer)` rather than `new HollowExplorer(readEngine)`,
so the pages show whatever version the consumer is currently on rather than the one it happened to
hold at startup. That is also what puts `State Version` in the navigation bar.

**Invalidating the heap cache.** The one thing an embedder has to remember. Each type's heap and hole
figures are worked out once per state rather than once per request, and the cache notices a new state
by its randomized tag — which a *delta* does not change. `ExplorerCacheInvalidator` is a refresh
listener that calls `ClearCache()` and `PrefillHeapStatsCache()` whenever the consumer moves; without
it the figures would be right after a snapshot and stale after every delta.

**Making it look like part of the application.** `HeaderDisplayString` names the dataset in the page
title and the navigation bar, and `AddCommonHeaderEntry` puts a cell of the application's own in that
bar — here a link back to `/`. That value is written into the page as markup rather than as text,
because the point of it is to let an embedder add a link, so only put markup there that you wrote.

**No generated client.** There is no `[HollowGeneratedApi]` anywhere in this sample and the source
generator is not referenced. The explorer reads schemas rather than classes, so it can be pointed at a
dataset whose model the process has never seen — which is what makes it useful against a dataset
someone else produces.

## The Publish button

`/` has a button that publishes the next cycle. It alternates between two versions of the catalogue: a
film is retitled, one is dropped and one is added.

It is there so the explorer can be watched following a delta. The dropped film leaves a **hole**
behind — an ordinal that holds nothing but whose space is still allocated — and the home page shows
that as a type costing more than its records do. Pressing it again swaps back, which reuses the hole.

In a real application an announcement watcher would drive the consumer; here the handler refreshes it
directly so that the page has finished updating by the time it redirects.
