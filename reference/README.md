# Hollow reference implementation (.NET)

A working producer and consumer built on the .NET port in [`../dotnet`](../dotnet), ported from
[Netflix/hollow-reference-implementation](https://github.com/Netflix/hollow-reference-implementation).

The Java original publishes to the local filesystem and carries S3 and DynamoDB infrastructure classes
you switch to by editing `main` and recompiling. This one keeps all three arrangements live at once and
picks between them with a setting:

| | Blobs | Announcement |
|---|---|---|
| **`Local`** (default) | a directory | a **watching folder** — one file per announced version, read by a `FileSystemWatcher` |
| **`Aws`** | Amazon S3 | Amazon DynamoDB |
| **`Azure`** | Azure Blob Storage | Azure Table Storage |

Everything above the storage line is the same in all three: one key layout, one snapshot index, one
publisher, one retriever, one announcer, one watcher.

## Running it

Two terminals, nothing to configure:

```bash
cd reference
dotnet run --project src/Hollow.Reference.Producer     # publishes a catalogue every ten seconds
dotnet run --project src/Hollow.Reference.Consumer     # follows it, and serves the UIs
```

Then open <http://127.0.0.1:7780>. The consumer's home page says which version it is on, and links to:

- **`/explorer`** — every type and every record in the version currently held.
- **`/history`** — what each cycle changed, and everything that ever happened to one film.

Both refresh as the producer cycles. The producer restores the last announced version at start-up, so
stopping and restarting it continues the delta chain rather than beginning a new one.

The first run writes to `<temp>/hollow-reference`, which is how the two processes find each other with
nothing configured — the same trick the Java original plays with `java.io.tmpdir`. Delete that directory
to start over.

Smaller and faster, for trying things out:

```bash
dotnet run --project src/Hollow.Reference.Producer \
  --Hollow:Producer:Source:MovieCount=200 \
  --Hollow:Producer:CycleInterval=00:00:02
```

## Switching infrastructure

[`appsettings.json`](appsettings.json) is linked into both applications, so one file configures both.
Every mode's branch stays in it; only the one named by `Hollow:Mode` is read:

```jsonc
{
  "Hollow": {
    "Mode": "Local",          // "Local", "Aws" or "Azure"
    "Namespace": "hollow-reference",
    "Local":  { /* ... */ },
    "Aws":    { /* ... */ },
    "Azure":  { /* ... */ }
  }
}
```

Anything in there can also be set from the environment (`Hollow__Mode=Aws`) or the command line
(`--Hollow:Mode=Aws`). The branch for the chosen mode is checked at start-up, so a missing bucket name
is a message before the first cycle rather than an exception in the middle of one.

### Local

Blobs go under `<RootPath>/blobs`, keyed exactly as they would be in a bucket, with what S3 would store
as user metadata kept in a sidecar file. Announcements go in `<RootPath>/watching`: announcing a version
creates a file named after it, and the announced version is the highest-numbered file there. A consumer
does not poll for it — a `FileSystemWatcher` on that folder tells it a file appeared, which is as close
as a directory gets to the push notification a real announcement bus would give. The folder is pruned to
the newest `AnnouncementsToKeep` as it grows.

Reads and writes are deliberately slowed down:

```jsonc
"Latency": {
  "PerOperation": "00:00:00.025",   // stands in for the round trip
  "PerMegabyte":  "00:00:00.040"    // stands in for the bandwidth
}
```

Without it the local mode answers in microseconds and never reproduces the races a real deployment has —
a consumer refreshing while the producer is mid-cycle, a watcher firing before the blob it refers to has
landed. Set both to zero to turn it off, which is what the tests do.

Pinning is a file too: put a version number in `<RootPath>/watching/pinned.version` and every consumer
moves to it and stays there until the file is deleted. That mirrors the `pin_version` attribute on the
Java original's DynamoDB row.

### AWS

The services the Java reference implementation uses, and the same layout in them.

To provision: an S3 bucket, and a DynamoDB table whose partition key is a string called `namespace`.
Nothing else — no sort key, no indexes.

```jsonc
"Aws": {
  "Region": "eu-north-1",
  "BucketName": "hollow-reference-blobs",
  "TableName": "hollow-reference-announcements",
  "PollInterval": "00:00:01"
}
```

Credentials come from the default chain — environment, shared config file, container role, instance
role — which is what a deployed service wants. Set `Profile` to use a named profile instead. (The Java
original takes an `AWSCredentials` in every constructor and has no fallback, so it only runs where a key
has been pasted in.)

Setting `ServiceUrl` and `ForcePathStyle` points the same code at LocalStack or MinIO, which is how to
exercise the AWS mode without an AWS account.

DynamoDB has nothing to push with, so the watcher polls it every `PollInterval`, exactly as
`DynamoDBAnnouncementWatcher` does.

### Azure

To provision: a storage account, and nothing else — the container and the table are created on first
use.

```jsonc
"Azure": {
  "BlobServiceUri":  "https://hollowreference.blob.core.windows.net",
  "TableServiceUri": "https://hollowreference.table.core.windows.net",
  "ContainerName": "hollow-reference-blobs",
  "TableName": "hollowreferenceannouncements",
  "PollInterval": "00:00:01"
}
```

With the two URIs set, the credential is whatever `DefaultAzureCredential` finds: a managed identity in
Azure, the `az` CLI login on a developer's machine. A `ConnectionString` may be given instead, which is
simpler to get running and worse to deploy. `"UseDevelopmentStorage=true"` points it at Azurite.

**Table Storage rather than Cosmos DB**, deliberately. Cosmos is the closer match to DynamoDB in shape
and the one an Azure-native design would reach for, but it bills for provisioned throughput whether or
not a row is read, and this table holds a single entity written once a cycle. Table Storage lives in the
same storage account as the blobs, bills per operation, and costs a fraction of a cent a month at that
rate.

## How it is put together

```
src/
  Hollow.Reference.Model/           the data model, and the generated client
  Hollow.Reference.Infrastructure/  the three modes, and the Hollow contracts over them
  Hollow.Reference.Producer/        a console host that cycles forever
  Hollow.Reference.Consumer/        a web host that follows, and serves the two UIs
tests/
  Hollow.Reference.Tests/           xUnit, over the local mode and the shared adapters
```

Every project references the fork by **project reference**, not by package, so a change to the port and
the reference implementation that exercises it build together.

### The data model

`Movie` and `Actor`, ported from `how.hollow.producer.datamodel`. Java's public fields become
properties and its camelCase names become PascalCase, which changes the field names in the schema too:
a dataset written by this producer is read by this consumer, not by the Java one.

`[HollowGeneratedApi]` on `Movie` is what makes the client — `MovieApi`, the record wrappers, the key
indexes, the typed field paths — appear at compile time. Java declares the same thing in
`build.gradle`'s `hollow { }` block and in an `APIGenerator` class, and checks the output in; here there
is nothing to regenerate and nothing to keep in step.

### The storage seam

The Java original writes its publisher and retriever against S3 directly. Here that logic is written
once against two small interfaces, and each mode implements them:

- **`IHollowBlobStore`** — write, open, read metadata, list. Absence is `null`, not an exception: a
  consumer asks for "the delta out of the version I hold" on every refresh, and most of the time there
  is not one yet.
- **`IHollowAnnouncementStore`** — announce, read, and optionally *subscribe*. Only the local mode
  implements subscription; the cloud modes return `null` from it and are polled.

Over those sit the four things Hollow actually asks for, in `Adapters/`, ported from the Java classes
named beside them:

| | from |
|---|---|
| `HollowBlobStorePublisher` | `S3Publisher`, including its snapshot index |
| `HollowBlobStoreBlobRetriever` | `S3BlobRetriever` |
| `HollowAnnouncementStoreAnnouncer` | `DynamoDBAnnouncer` / `S3Announcer` |
| `HollowAnnouncementStoreWatcher` | `DynamoDBAnnouncementWatcher` / `S3AnnouncementWatcher` |

### The snapshot index

A consumer starting cold asks for a snapshot of the announced version, and often there is not one: a
producer keeps only every *n*th snapshot, and the announced version is reachable as an older snapshot
plus a run of deltas. A blob store cannot answer "the greatest version at or below this one", so the
publisher maintains an index of the versions a snapshot exists for and writes it back as one small
object — the first version in full, then the gap to each version after it, as variable-length longs.
That is the Java encoding exactly. Versions are minted from the clock, so the gaps are small and years
of cycles fit in a few kilobytes.

### Where this differs from the Java original

- **One process per role, three infrastructures, chosen by configuration** rather than by editing
  `main`.
- **The consumer is one web application with both UIs mounted in it**, at `/explorer` and `/history`,
  rather than two Jetty servers on two ports. The UIs show the whole dataset, so they belong behind
  whatever already guards the application.
- **The producer runs validators.** Java runs none. The two here catch the failure a producer actually
  has, which is not a corrupt blob but a bad upstream read: an empty catalogue, or one that lost half of
  itself between cycles.
- **A restore that fails does not stop the producer**, and neither does a failed cycle. The next cycle
  publishes a snapshot and every consumer reloads, which is worse than a delta and better than nothing.
- **The consumer waits for the producer's first version** instead of throwing if it started first.
- **Announcement metadata is kept.** Hollow's `Announce` carries a dictionary the Java announcers drop
  on the floor; all three stores here write it and read it back.
- **Optional blob parts are refused rather than silently dropped.** Neither implementation supports
  them; this one says so instead of publishing a blob without its parts.

### Where the asynchronous code meets the synchronous contracts

Hollow's `IPublisher`, `IAnnouncer` and `IBlobRetriever` are synchronous and both cloud SDKs are
asynchronous only. `Adapters/Synchronously.cs` is the one place that bridges the two, with the reason
written down. Neither host has a synchronization context, so blocking there cannot deadlock.

## Tests

```bash
cd reference
dotnet test
```

47 tests covering the snapshot index encoding, the blob key layout, the local blob and announcement
stores, the shipped configuration, and a round trip: the producer publishes, the consumer reads it back
through the same adapters the applications use, follows a second version over the `FileSystemWatcher`,
reaches a version that has no snapshot of its own through the index, and stays put when pinned.

The Java reference implementation has no tests; these are the checks its quick-start guide asks a reader
to perform by hand.

## Licence

Apache 2.0, as the original. See [`../LICENSE`](../LICENSE).
