# The sample

A whole Hollow deployment in one process: a producer publishing a catalogue of films to a directory of
blobs, and a consumer following it and reading it back through a client nobody wrote by hand.

```
dotnet run --project samples/Hollow.Sample
dotnet run --project samples/Hollow.Sample -- /tmp/my-blobs   # keep the blobs
```

## What it covers

The data model is in `Catalogue.cs` and is the only hand-written description of the data there is. The
producer maps those classes into records; the source generator reads the same declarations at compile
time and emits the client the consumer reads them back through.

**Writing** (`Producing.cs`)

- a producer publishing to the filesystem, with a staging directory and an announcer
- a full cycle, which states the whole dataset and lets the producer work out the difference
- an incremental cycle, which states only what changed, including a deletion by key alone
- five validators, one of which judges the transition rather than the state, and a cycle that is
  refused and therefore never announced
- a listener watching every step of a cycle
- the schemas the model maps to, printed in Hollow's own schema syntax

**Reading** (`Consuming.cs`)

- a consumer pinned to a version, and one following announcements
- the generated client over every kind of field: scalars stored in the record, references to shared
  records, an enum, a list, a set, and a map with a hash key
- following a delta in place rather than reloading, and asking the result what the cycle changed
- four ways to find a record by key, from `api.FindMovie(new MoviePrimaryKey(5))` down to the untyped
  core index the rest are built on
- typed field paths — `CataloguePaths.Movie.Studio.Name.Value` rather than `"Studio.Name.value"` —
  which the indexes take in place of a string and take their own query type from
- a hash index matching many records, and one that matches on one type and returns another
- prefix search over titles, with a tokenizer that makes a query match a word anywhere in one
- reading a string into a stack buffer instead of allocating one
- a consumer that loads only part of the dataset, and a client that reads the rest as absent rather
  than throwing

## The generated code

Nothing under `Generated` is checked in. To read what the generator produced, look in
`obj/generated/Hollow.SourceGenerator/…` after a build — the project turns on
`EmitCompilerGeneratedFiles` so it lands on disk.
