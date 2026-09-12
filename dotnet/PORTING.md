# Porting Hollow to .NET 10

This directory holds a .NET 10 port of Netflix Hollow's core engine. All four record kinds — object,
list, set and map — round-trip end to end: a `HollowWriteStateEngine` writes a snapshot blob that a
`HollowReadStateEngine` reads back. An object mapper maps ordinary CLR types onto Hollow records, so
callers need not build records field by field.

Deltas are here for all four record kinds, in both directions: the producer calculates and writes a
delta or a reverse delta, and a consumer applies it to move a cycle forward or back without re-reading
a snapshot.

Indexing is here too: field paths bind a declared key onto the schemas, three indexes look records up
by value rather than by ordinal — `HollowPrimaryKeyIndex` and `HollowUniqueKeyIndex` by a unique key,
`HollowHashIndex` by fields that are not unique and may cross collections — and a set or map schema may
declare a hash key, which the producer honours when laying out each record's hash table.

Both ends of the publish/consume loop that a delta chain exists to serve are here as well.
`HollowProducer` runs the cycle — populate, publish, check, validate, announce — and refuses to
announce a version it cannot vouch for; a producer that restarts calls `Restore` to pick the chain back
up rather than starting a new one. On the other side, `HollowConsumer` keeps a local copy of the
dataset up to date from that blob store, following deltas where it can and loading a snapshot only
where it must.

There is **one deliberate departure from the Hollow format**: a `Decimal` field type that stores a .NET
`decimal` exactly. It is opt-in — a dataset that declares no decimal field is byte-identical to what
Netflix Hollow produces. Read [Format extension: the `Decimal` field
type](#format-extension-the-decimal-field-type) before changing anything in the write or read path.

Type resharding is here on both sides: a producer may change how many shards a type's records are
written in as the data grows or shrinks, and a consumer rearranges the records it already holds to
match before applying the delta that says so. The incremental producer is here too, for a caller whose
source of truth is a change feed rather than a table.

Code generation is here, in both forms: mark a model root `[HollowGeneratedApi]` and a Roslyn source
generator emits a typed C# client at compile time — `api.GetMovie(17).Title` rather than field 3 of
ordinal 17 — or call `HollowCodeGenerator` to write the same client out as text. Either way a type that
declares a primary key also gets a unique-key index. The typed runtime it sits on is useful without it, since the generic records
traverse any dataset by name. Variable-length fields can be read into a caller's buffer, or viewed in
place where the storage layout allows it, rather than always allocating.

A dataset can also be read by a person rather than a program: `Hollow.Explorer` is the port of
`hollow-explorer-ui` as ASP.NET Core MVC, carrying over the original pages' HTML. Mount it into the
application that already holds the dataset, or run it on a loopback port of its own. See
[The explorer](#the-explorer).

What is **not** here is object longevity and the diff/history tools. The status section says exactly
what is and is not ported.

## Building and testing

```
dotnet build
dotnet test
```

Requires the .NET 10 SDK. On a machine without ICU installed, set
`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` — nothing in the port depends on culture-sensitive
behaviour.

## Producing and consuming a dataset

A producer, publishing to a directory:

```csharp
HollowProducer producer = new HollowProducerBuilder()
    .WithBlobStagingDirectory("/var/hollow/staging")
    .WithPublisher(new HollowFilesystemPublisher("/var/hollow/blobs"))
    .WithAnnouncer(new HollowFilesystemAnnouncer("/var/hollow/blobs"))
    .WithValidators(new DuplicateDataDetectionValidator("Movie"))
    .Build();

producer.InitializeDataModel(typeof(Movie));
producer.Restore(new HollowFilesystemAnnouncementWatcher("/var/hollow/blobs").GetLatestVersion(),
    new HollowFilesystemBlobRetriever("/var/hollow/blobs"));

producer.RunCycle(state =>
{
    foreach (Movie movie in QueryEverything())
    {
        state.Add(movie);
    }
});
```

A consumer, reading the same directory:

```csharp
using HollowConsumer consumer = new HollowConsumerBuilder()
    .WithLocalBlobStore("/var/hollow/blobs")
    .WithAnnouncementWatcher(new HollowFilesystemAnnouncementWatcher("/var/hollow/blobs"))
    .Build();

consumer.TriggerRefresh();

using HollowPrimaryKeyIndex index = new(consumer.StateEngine!, new PrimaryKey("Movie", "Id"));
index.ListenForDeltaUpdates();

int ordinal = index.GetMatchingOrdinal(42);
```

A populator describes the whole dataset each cycle, not the change since last time; the producer works
out what moved. Nothing is announced unless the cycle publishes, reads its own blobs back, finds that
the snapshot and both deltas agree, and passes every validator. `Restore` on startup is what keeps a
restarted producer on the chain consumers are already following — without it the first cycle after a
restart forces every one of them to take a snapshot.

On the consumer side, with an announcement watcher it follows whatever the producer announces; without
one, drive it with `TriggerRefreshTo(version)`. Either way it follows deltas where it can, so
`consumer.StateEngine` keeps returning the same instance and an index told to
`ListenForDeltaUpdates()` stays valid — register an `IRefreshListener` to hear when that stops being
true.

## Prefix and sparse-integer indexes

Two more ways to find records, alongside the primary-key and hash indexes shown above.

A prefix index answers "which records start with this?", which is what an autocomplete box needs:

```csharp
using HollowPrefixIndex index = new(consumer.StateEngine!, "Movie", "Title");
index.ListenForDeltaUpdates();

foreach (int ordinal in index.FindKeysWithPrefix("the mat").AsEnumerable())
{
    // every movie whose title starts with "the mat"
}
```

Pass a `tokenizer` to index each word of a title separately, so a query matches a word anywhere in it.
Note that Netflix marks this index deprecated — experimental, and discontinued over its memory
efficiency — and suggests repeated lookups into a `HollowUniqueKeyIndex` where that will do. It is
ported with that caveat.

A sparse integer set answers "is this integer in the data?" for values scattered across a wide range,
without a bit set over the whole range:

```csharp
HollowSparseIntegerSet released2009 = new(
    consumer.StateEngine!, "Movie", "Id.Value",
    ordinal => movies.ReadInt(ordinal, yearPosition) == 2009);

bool isThere = released2009.Get(1_000_000);
```

The predicate is what makes it more than a membership test over a column: the set holds the values of
the records that satisfy it, so the question it answers is "is there a 2009 release with this id?".

## Incremental cycles

`RunCycle` describes the whole dataset. `RunIncrementalCycle` describes only what moved since the last
version, and the producer carries everything else across unchanged:

```csharp
producer.RunIncrementalCycle(state =>
{
    foreach (Change change in QueryChangesSinceLastVersion())
    {
        if (change.IsDeletion)
        {
            state.Delete(change.Movie);
        }
        else
        {
            state.AddOrModify(change.Movie);
        }
    }
});
```

Everything after population is identical: the same blobs are written, the same integrity check and
validators run, and the same version is announced. A producer can run either kind of cycle; it need
not be built for one.

Records are named by primary key, so every type an incremental cycle touches needs one — through
`[HollowPrimaryKey]` on the CLR type, or on the schema. `AddIfAbsent` leaves an existing record alone;
`Delete` takes either an object or a `RecordPrimaryKey` and ignores a record that is not there.
Reporting the same record twice is the last word winning, not both changes applying.

Deleting a record also deletes what it referenced, unless something else still references it. That is
`TransitiveSetTraverser` (in `Core/Tools/Traverse`), and it is what stops the strings of every deleted
record accumulating in the state forever. It is public in its own right: given a selection of ordinals
per type it can add everything the selection points at, add everything that points at the selection,
or drop whatever something outside still needs.

## Type resharding

A type's records are split across a power-of-two number of shards, chosen from the data size so that
no one allocation has to hold the whole type. By default that count is fixed the first time the type
is written. Turning resharding on lets it follow the data:

```csharp
HollowProducer producer = new HollowProducerBuilder()
    // ...
    .WithTypeResharding()
    .Build();
```

A consumer needs no configuration: when a delta declares a different count from the one it holds, it
rearranges its records to match and then applies the delta.

Three things are worth knowing before turning it on.

**Every consumer of the chain has to be able to follow it.** A consumer that predates resharding
applies the delta at the count it already has and misreads every ordinal in it. There is no
negotiation — the producer's header tag `hollow.type.resharding.invoked` records what moved
(`Movie:(1,2) Actor:(8,4)`, always in the forward direction) but nothing checks that consumers coped.

**A count moves by at most a factor of two per cycle**, so a type that has badly outgrown its shards
takes several cycles to get where it is going. That is what keeps each consumer's rearrangement to one
split or one join.

**A shard-count change alone is enough to put a type in a delta**, even when none of its records
changed. The arrangement is what the delta is carrying.

A reverse delta is written at the *previous* cycle's count, because that is the arrangement it takes a
consumer back to. `HollowTypeWriteState` therefore tracks both: `NumShards` for this cycle and
`RevNumShards` for the last.

On the consumer side the rearrangement runs one shard at a time, so only one shard's worth of records
is duplicated at any moment rather than the whole type. Each step publishes a complete new shard array
before releasing the storage it replaced — that is what `ShardsHolder` is for, holding the shard array
and the mask that selects one so a reader cannot pair a new array with an old mask. If a rearrangement
fails part way through it throws `InvalidOperationException`, and the read state is then unusable:
only a fresh snapshot recovers it.

## The typed API layer

Everything above reads a record by asking a type state for field 3 of ordinal 17. The typed layer is
the one generated code is written against, and it is ported: `HollowApi` and the `HollowTypeApi`
family that read fields by name, the delegates that decide whether a read goes to the blob or to a
cache, the `HollowObject`/`HollowList`/`HollowSet`/`HollowMap` record wrappers, the providers that
turn an ordinal into a wrapper, and the missing-data handler a read falls through to when the field is
not in the dataset.

It is useful before a generator exists, because the generic records traverse anything:

```csharp
GenericHollowObject movie = new(consumer.StateEngine!, "Movie", ordinal);

int year = movie.GetInt("Year");
string? title = movie.GetObject("Title")!.GetString("value");

foreach (GenericHollowObject actor in movie.GetList("Cast")!.Cast<GenericHollowObject>())
{
    // ...
}
```

The wrappers implement the BCL collection interfaces, so `HollowList<T>` is an `IReadOnlyList<T>` and
LINQ works over a record without anything being materialised. A record is a handle — a type name, an
ordinal and a delegate — and two handles to the same record compare equal, so a record can be a
dictionary key.

A field the dataset does not have does not fail. It goes to `IMissingDataHandler`, which by default
answers absent (`null`, `int.MinValue`, `double.NaN`), so a client compiled against a newer model can
read an older dataset. Replace `HollowReadStateEngine.MissingDataHandler` to make it loud instead.

Caching is a configuration choice rather than a code change: a generated API routes every read through
a `HollowObjectProvider<T>`, and swapping `HollowObjectFactoryProvider<T>` for
`HollowObjectCacheProvider<T>` caches the whole type. The cache follows deltas — a record the next
version still holds keeps its wrapper, repointed at the new type API — and pins what it holds until
`Detach()`.

## Reading strings and bytes without allocating

A variable-length field can be read into a caller's buffer rather than into a fresh `string` or
`byte[]`:

```csharp
Span<char> buffer = stackalloc char[64];
ReadOnlySpan<char> title = titleRecord.GetString("value", buffer);
```

`GetVarLengthByteLength` says how big a buffer a field needs. It is the stored byte count, which
bounds the character count rather than giving it: a character is stored VarInt-encoded, so text is
never longer in characters than in bytes. A too-small buffer is refused rather than truncated. The
same shape reads a `Bytes` field into a `Span<byte>`, and `ReadStringInto`/`ReadBytesInto` are the
forms that tell a null field from an empty one, by returning -1.

Where a byte field can be viewed in place rather than copied, it is:

```csharp
if (movie.TryGetBytes("Poster", out ReadOnlySpan<byte> poster))
{
    // a view straight into storage, no copy
}
```

That succeeds only when the value sits inside a single storage segment. Variable-length data lives in
a list of fixed-size pooled arrays, so a value long enough, or unlucky enough in where it starts, lies
across a boundary and cannot be one span. `ReadOnlySequence<byte>` covers the general case with no
copy either way — one sequence segment per piece, or a single-segment sequence when the value happens
to be contiguous:

```csharp
ReadOnlySequence<byte> poster = movie.GetBytesSequence("Poster");
SequenceReader<byte> reader = new(poster);
```

There is deliberately no `ReadOnlySequence<char>`. A string's characters are VarInt-encoded in
storage, so there are no `char`s there to point at — a sequence over them would have to decode into a
buffer first, which is what `GetString(name, Span<char>)` already does, honestly.
`GetStringBytesSequence` exposes a string's encoded bytes for hashing or copying, where the bytes
rather than the text are what is wanted.

## The typed value indexes

`HollowPrimaryKeyIndex` and `HollowHashIndex` take an `object[]` and hand back ordinals.
`UniqueKeyIndex` and `HashIndex` are the typed façades over them: the query is an object of the
caller's own type, the match comes back as a record wrapper, and both ends are bound to the schemas
when the index is built rather than at the first query that gets them wrong.

```csharp
UniqueKeyIndex<Movie, int> byId = UniqueKeyIndex.From<Movie>(consumer)
    .BindToPrimaryKey()
    .UsingPath<int>("Id");

consumer.AddRefreshListener(byId);

Movie? found = byId.FindMatch(42);
```

A composite key is an object with a `[FieldPath]` member per field, in any order — the index puts
them in the order the declared primary key lists them, and refuses a key that is not the declared one:

```csharp
private sealed record ReleaseKey(
    [property: FieldPath("Id")] int Id,
    [property: FieldPath("Studio.Name", Order = 1)] string Studio,
    [property: FieldPath("Year", Order = 2)] int Year);
```

`HashIndex` answers the question that is neither unique nor one-to-one, and its select form returns
the records a path names rather than the roots that matched:

```csharp
HashIndexSelect<Movie, Actor, string> castByStudio = HashIndex.From<Movie>(consumer)
    .SelectField<Actor>("Cast.element")
    .UsingPath<string>("Studio.Name.value");

foreach (Actor actor in castByStudio.FindMatches("Lionsgate"))
{
    // every cast member of every film that studio made
}
```

Register an index with the consumer to have it follow the data, and remove it when it is no longer
wanted: a registered index is still being rebuilt on every snapshot, and still holds the state it was
built over.

Turning an ordinal into a record needs the typed API, so the consumer now builds one. Give it an
`IHollowApiFactory` and it hands the result out as `HollowConsumer.Api`:

```csharp
new HollowConsumerBuilder()
    .WithBlobRetriever(blobRetriever)
    .WithApiFactory(new MoviesApiFactory())
    .Build();
```

The API is rebuilt on each snapshot and kept across deltas, since the type APIs read through the state
engine itself and anything caching underneath is its own delta listener.

## The code generator

`Hollow.Api.Codegen` turns a data model into the typed client the layer above was built for. Point it
at the CLR types a producer maps, or at any dataset that carries schemas, and it emits C#:

```csharp
HollowCodeGenerator generator = new(new HollowCodeGeneratorOptions { Namespace = "Acme.Movies" });

HollowCodeGenerator.WriteTo("obj/generated", generator.Generate(typeof(Movie)));
```

What comes out, per type: a type API that resolves each field's position once, a delegate interface
with a lookup and a cached implementation, a record wrapper with a property per field, and a factory.
Plus one API class holding them all, a factory for a consumer to take, and a unique-key index for each
type that declares a primary key. The schemas are derived by `HollowObjectMapper`, the same code the
write path uses, so the generated client reads exactly what a producer mapping those types writes.

### What C# changes

- **Getters become properties**, and Java's paired `getYear()`/`getYearBoxed()` — a primitive and its
  boxed form, because only the latter can be null — collapses into one `int? Year`. That removes a
  whole category of generated method.
- **A reference to a single-value type reads as that value.** `movie.Title` is a `string`, not an
  `HString`; the wrapper is still there as `movie.TitleRecord`. Java has the same shortcut and it
  matters more here, where a wrapper type in a property signature reads as noise. Turn it off with
  `UseErgonomicShortcuts = false` and the property is the wrapper.
- **A string reference also generates a comparison.** `movie.IsTitleEqual("Rush")` goes through the
  shared `String` record's type API, so the stored text is never built — the one read the typed layer
  can do that a naive wrapper cannot.
- **The six built-in scalar wrappers are not emitted.** Java generates `HString`, `HInteger` and the
  rest into every generated package; they are in `Hollow.Core.Types` here, so the generated API just
  names them.
- **Nullability is part of the contract.** The output is emitted `#nullable enable` and has to compile
  without a warning — a warning in generated source is one the caller cannot fix.
- **The collection wrappers are the BCL interfaces.** A generated `ListOfActor` is an
  `IReadOnlyList<Actor>`, so LINQ and `foreach` work over it with nothing materialised.
- **The `Decimal` extension generates an accessor** like any other field, which Java has no equivalent
  of — see [Format extension: the `Decimal` field
  type](#format-extension-the-decimal-field-type).

### A type the dataset does not have

A client generated from one model may be pointed at a dataset written from another, and a whole type
can be missing. The generated API holds a stand-in for it
(`core.read.dataaccess.missing`, ported for this) rather than failing to construct: every read goes to
the missing-data handler, `TypeApi.IsTypePresent` says so, and the type enumerates as empty. The
marker is an interface, `IHollowMissingTypeDataAccess`, where Java names all four concrete classes at
each check.

### Two front ends, one set of emitters

There are two ways to run the generator, and both end in the same emitters.

**`HollowCodeGenerator` is the text emitter.** Point it at CLR types or at any dataset carrying
schemas, get `.cs` files, write them where you like. Reach for it when the model is a dataset rather
than declared types, or when reading the generated source matters.

**`Hollow.SourceGenerator` is a Roslyn incremental source generator.** Mark a model root and the
client appears in the compilation — nothing to check in, nothing to regenerate when the model changes:

```csharp
[HollowGeneratedApi]
[HollowPrimaryKey("Id")]
public sealed record Movie(int Id, string Title, List<Actor> Cast);
```

For a model in `Acme.Catalogue` that emits `Acme.Catalogue.Generated.CatalogueApi`. The generated
namespace defaults to the model's own with `.Generated` appended, because the wrapper generated for a
type is named after that type and would otherwise collide with it; the API is named for where the
*model* lives, so it is `CatalogueApi` rather than `GeneratedApi`. Both, and the emitter options, are
settable on the attribute. Several roots may be marked; those naming the same namespace and API class
become one client, so a model with several entry points does not produce two APIs each knowing half of
it. A model that cannot be mapped is `HOLLOW001` — a build error pointing at the declaration, rather
than a silently missing client.

Consume it as an analyser:

```xml
<ProjectReference Include="…/Hollow.SourceGenerator.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

### What sharing the emitters costs

A Roslyn analyser targets `netstandard2.0` and loads into whatever host runs the compiler, so it
**cannot reference the Hollow runtime at all**. That single constraint shapes the whole design.

The emitters therefore depend on nothing. They work against `ModelSchema` — a description thin enough
to carry no Hollow types — rather than `Hollow.Core.Schema`, and the generator project compiles the
same five files by `<Compile Include="../Hollow/…" Link="Shared/…" />` rather than holding a copy of
them. Everything in those files is `internal`, so the two assemblies' copies cannot collide. Sharing
source is uglier than sharing a reference; the alternative was two sets of emitters that would drift.

It also means those files keep to the `netstandard2.0` API surface: no `Index`/`Range` (`pascal[1..]`
is `pascal.Substring(1)`), no `ArgumentException.ThrowIfNullOrEmpty`, no
`string.Replace(string, string, StringComparison)`. Each is commented where it would otherwise read as
a needless long way round.

### The one seam that cannot be shared

Deriving the model. The text emitter asks `HollowObjectMapper`, because it runs with the model's types
loaded. The generator sees symbols rather than types, so `SymbolModel` applies the same rules again
against Roslyn: the type-naming rules, `MapOf…To…`/`SetOf…`/`ListOf…`, scalar wrappers, enums as a
`_name` field, which members map and in what order, when a scalar is inlined, and the derived hash
keys.

The two have to agree exactly — a client generated one way reads a blob written by a producer mapping
the same types the other way. So `SourceGeneratorTests` declares one model twice, once as source and
once as CLR types, and asserts the two derivations produce byte-identical schema text. `ModelSchema`
renders itself in Hollow's schema syntax precisely so that comparison can be made directly. That
includes hash keys, which nothing the emitters produce depends on: describing the model in full rather
than only in the part that matters today is what makes the check worth having.

### A primary key is a record, not an argument list

Java's generated index takes the key's fields positionally, as `Object...`. Two `String` key fields
passed the wrong way round compile and then silently match nothing. The generator here emits a record
for the key instead — `MoviePrimaryKey(int Id)`, `ScreeningPrimaryKey(string Title, int Year)` — and
the index takes that, so the compiler checks the names, the types and the order.

Each component is resolved by walking the model rather than by reading one segment of the path, so a
key that crosses a reference still gets a real type: `@PrimaryKey(Name)` on a type whose `Name` is a
`String` reference gives `string`, because that is what the index auto-expands the path to and matches
on. Only a path the model cannot follow falls back to `object`. Two paths ending in the same segment
would give the record two properties of one name, so where that happens the whole path names them.

The API itself gets the lookup, which is the shape most callers want:

```csharp
Movie? film = api.FindMovie(new MoviePrimaryKey(5));
```

It builds the index on first use, keeps it, and has it follow deltas, so repeated lookups against one
version cost one build. The index then lives exactly as long as the API does — a delta leaves both in
place, and a snapshot replaces both. The standalone `{Type}UniqueKeyIndex` is still generated for an
application that would rather the build happened on refresh than on the first lookup, and it is still
what a `HollowConsumer` registers as a refresh listener. Java offers only the standalone index.

Making the API own an index is what turned up a latent bug: nothing in this port ever called
`HollowApi.DetachCaches`, so a replaced API's cached delegates stayed registered against type states
that had since been overwritten. `HollowDataHolder` now detaches the outgoing API before building the
new one.

### A field path is a value, not a string

Java's indexes take their paths as text: `usingPath("Studio.Name.value", String.class)`. A typo is a
runtime failure, the type the path arrives at is something the caller has to know and repeat, and
nothing stops a path for one type being handed to an index over another.

The generator emits the paths instead, modelled on Swift's `KeyPath`:

```csharp
HashIndex<Movie, string> byStudio =
    HashIndex.From<Movie>(consumer).UsingPath(CataloguePaths.Movie.Studio.Name.Value);

HashIndexSelect<Movie, Actor, string> castByStudio = HashIndex.From<Movie>(consumer)
    .SelectField(CataloguePaths.Movie.Cast.Element)
    .UsingPath(CataloguePaths.Movie.Studio.Name.Value);
```

`CataloguePaths.Movie.Studio.Name.Value` is a `FieldPath<Movie, string>`, so `UsingPath` infers its
query type rather than being told it. Both ends are type arguments, as in `KeyPath<Root, Value>`: the
value so an index can type itself, and the root so a path cannot be handed to an index over some other
type. The root is also carried as a type *name*, because the index underneath binds against the schema
rather than against CLR types — `RequireRoot` checks it where the compiler cannot.

Each step is itself a path, which is what makes a route that stops early as usable as one that runs to
a value: `CataloguePaths.Movie.Studio` is a `FieldPath<Movie, Studio>` and also the thing `.Name` hangs
off. That works because a generated step class *derives from* the path type rather than converting to
one. The root is a type parameter on the step class rather than baked into it, so the generator emits a
class per type rather than per route — which is what stops a model that references itself generating
forever.

Two things follow from the schema rather than from the model, and surprise people:

- A route crosses a reference where the model looks like it holds a value. A `string Title` is a
  reference to the shared `String` type, so the route is `Title.Value`, not `Title`. So is a nullable
  `decimal` or `byte[]`.
- A collection step is named for the schema's own field: `.Element` on a list or set, `.Key` and
  `.Value` on a map, spelling `element`, `key` and `value`.

The methods that still take text are suffixed `Raw` — `UsingPathRaw`, `SelectFieldRaw` — for a path the
generated routes cannot express. Constructors could not be renamed, so `HollowPrefixIndex`,
`HollowPrimaryKeyIndex`, `HollowUniqueKeyIndex` and `PrimaryKey` gained overloads taking paths
alongside the string ones they already had. `[FieldPath]`, which `UsingBean` reads, is still a string:
an attribute argument cannot be anything else.

### Testing it

Java's generator tests write the output to a temporary directory and shell out to the JDK compiler,
which tells them only that it compiles. `CodeGeneratorTests` compiles the output in process with
Roslyn, asserts there is not even a warning, loads the assembly and reads real records through it —
including a run that checks the generated client agrees with the untyped generic layer on every record
of every type, which is what catches a field position resolved wrongly.

The emitted text itself is deliberately not pinned. It is an implementation detail, and a test over it
turns every improvement into a test change.

`FieldPathTests` compiles a client and walks its generated routes by reflection, checking that each
spells the text the string form spelled and arrives at the type it claims — and then builds a real
index from one, because the schema has the last word on whether a path resolves. The two ends matter
separately: the spelling is the generator's, the resolution is the dataset's.

`SourceGeneratorTests` drives the source generator the way the compiler does, then compiles and runs
what it produced. Its load-bearing test is the one that declares a model twice — once as source, once
as CLR types — and asserts that the generator's symbol-based derivation and the object mapper's
reflection-based one produce identical schema text. That is the only seam the two front ends do not
share, so it is the only one that can drift.

### What is not ported

Java's three independent extras — a POJO generator, a "performance API" generator and a test-data
builder generator, around 1,900 lines between them — are not ported, and nothing else depends on them.


## The sample

`samples/Hollow.Sample` is a runnable console application that is the whole loop in one process: a
producer publishing a catalogue of films to a directory of blobs, and a consumer following it and
reading it back through a client the source generator wrote at compile time.

```
dotnet run --project samples/Hollow.Sample
```

It exists partly as documentation and partly as a test nothing else is: every other test either
exercises one layer or builds its own scaffolding, whereas the sample uses the library the way a
caller would, from the filesystem publisher through to the generated indexes. Two defects in this port
were found by writing it — the code generator naming every API `GeneratedApi`, and an ergonomic
shortcut emitting the wrong property name for an enum — neither of which the unit tests had noticed.

Its `README.md` lists what it covers. Two things in it are worth naming here because they are not
obvious:

- **A refused cycle's blobs are still published.** Publication happens before validation; only the
  announcement is withheld. So a cycle the validators refuse leaves a second delta hanging off the
  version before it, and a consumer can only follow one of them — the next announced version is then
  reached by a snapshot rather than by a delta. Java behaves the same way. The sample runs its refused
  cycle last so that the chain it demonstrates stays clean.
- **A consumer with an announcement watcher cannot be pointed at a version.** It follows what was
  announced, and `TriggerRefreshTo` throws. The sample uses a version-pinned consumer to walk the
  chain step by step and a watcher-driven one to show what production looks like.


## Culture-invariant formatting and parsing

> **Every conversion between a number and text in this codebase names the invariant culture. No
> exceptions — not in data, not in schema text, not in exception messages.**

A conversion that uses the ambient culture works on the machine that wrote it and fails on someone
else's: a decimal point becomes a comma, a group separator appears, and a negative sign becomes U+2212
MINUS SIGN rather than an ASCII hyphen. Hollow's own output is worse than merely cosmetic — schema
text and displayed field values are formats a caller may read back.

### How to comply

| Situation | Write this |
| --- | --- |
| Formatting a number | `value.ToString(CultureInfo.InvariantCulture)` |
| A number inside an interpolated string | `$"... {value.Invariant()} ..."` |
| Joining numbers or boxed field values | `InvariantFormatting.JoinInvariant(", ", values)` |
| Parsing a number | `int.Parse(text, CultureInfo.InvariantCulture)`, and likewise for the rest |
| Converting a boxed value | `Convert.ToInt32(value, CultureInfo.InvariantCulture)` |

`InvariantFormatting` (in `Core/Util`) exists only so the interpolated-string case can say what it
means without swamping the call site. It is `internal`; nothing about it reaches a caller.

### Two traps

**`ToString(null, null)` looks correct and is not.** The second `null` is the format provider, and a
null provider means the *current* culture. It also satisfies the CA1305 analyzer, because an argument
was passed. This is exactly how the bug that prompted this section got in. Never write it.

**Interpolated strings are not analyzed at all.** `$"{value}"` is a current-culture conversion and no
analyzer will say so. That is why the `Invariant()` extensions exist and why the rule has to be
followed by hand there.

### What enforces it

Three things, each of which was checked to fail when the rule is broken:

1. **The analyzers.** CA1304, CA1305, CA1307, CA1310 and CA1311 are errors, set in `.editorconfig`.
   A bare `value.ToString()` will not build.
2. **The whole test suite runs under a hostile culture.** `HostileCulture` is a module initializer in
   the test assembly that switches the process to a culture using a comma decimal separator, a space
   group separator and U+2212 for negatives. Any test comparing formatted output against a literal
   fails if the code under test used the ambient culture. The culture is built by hand rather than by
   name, because a named culture collapses to the invariant one where
   `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT` is set — which would quietly turn the guard off.
3. **`CultureInvarianceTests`** pins the formatting surface directly, and its first test asserts that
   the hostile culture really is installed — so removing the guard fails loudly rather than silently
   making the other tests vacuous.

## Format extension: the `Decimal` field type

Everything else in this port aims to produce and consume exactly the bytes Netflix Hollow does. This
does not: it adds a ninth field type, `FieldType.Decimal`, which Netflix Hollow has no equivalent of.

It exists because Hollow's numeric field types cannot carry a .NET `decimal`. A `double` loses both
precision and scale, and a `long` of minor units loses the scale and forces every consumer to agree on
an exponent out of band. Money is the obvious case, but anything where `1.50` and `1.5` are meant to
stay distinguishable has the same problem.

### The compatibility rule

> **A dataset that declares no `Decimal` field must serialise byte-for-byte identically to what
> Netflix Hollow would produce. Every change to this port must preserve that.**

This is what keeps the extension honest: an existing model pays nothing for it, a blob written from
one stays readable by a Java Hollow consumer, and the extension is a decision each schema makes rather
than something imposed on the format.

`FormatCompatibilityTests` enforces the rule. It pins the SHA-256 of a snapshot and of a delta over a
dataset that exercises every original field type, every schema kind, null values and a sharded type.
A digest is a blunt instrument, and that is the point: it fails on *any* change to the byte stream,
whether or not anybody thought to write a test for that part of it.

**If one of those digests changes, stop.** The change under test altered the blob format. That may be
right — but it has to be a decision, and this section has to be updated to say what changed and why.
Do not re-baseline a digest to make a build pass.

The digests currently in the test were verified against the commit immediately before the extension
was added, so they are the pre-extension bytes rather than merely the bytes of the day they were
written.

### What a consumer without the extension sees

A Java Hollow consumer reading a blob that *does* declare a decimal field will fail while parsing the
schema: `DECIMAL` is not a field type name it knows. It fails cleanly rather than misreading the data,
because the field type is named in the schema rather than implied by a width.

### The encoding

A decimal field is a fixed-length field of **16 bytes (128 bits)**, holding the four integers
`decimal.GetBits` returns — the low, middle and high words of the 96-bit mantissa, then the flags word
carrying the scale and the sign — each big-endian, in that order.

It is the only field type wider than 64 bits, which is the one structural thing the rest of the code
has to account for: an element of the fixed-length bit string is at most 64 bits, so a decimal occupies
*two* elements, the low half at the field's bit offset and the high half 64 bits later.
`FixedLengthDataExtensions.GetWideElementValue` and `SetWideElementValue` are how the code that handles
fields generically — the field filter and the delta applicator — reads and writes a field without
caring how wide it is. **Anything new that walks fields generically must use them, not
`GetLargeElementValue`, which silently truncates at 64 bits.**

Null is all sixteen bytes set. That is not a representable decimal — the flags word may only carry a
scale of 0 to 28 in bits 16 to 23 and a sign in bit 31 — and it is the same all-ones convention Hollow
already uses for its other fixed-length fields.

### Scale is stored but ignored when comparing

The stored form keeps the scale, so `1.50m` round-trips as `1.50m`. That is the point of the field
type, and it means two records differing only in scale are two records, not one.

.NET's own `==` on `decimal` ignores scale, though, so anything that hashes a decimal has to ignore it
too, or a hash table keyed on one breaks: two values that compare equal would land in different
buckets. `DecimalBits.CanonicalHashCode` strips trailing zeros before hashing, and the primary key
index, the set and map hash keys, and `HollowReadFieldUtils` all go through it. `decimal.GetHashCode`
would also be scale-invariant but is an implementation detail of the runtime, and a hash that decides
where a record lands in a blob has to keep producing the same answer across runtime versions.

### Where it is wired in

| Concern | Where |
| --- | --- |
| The encoding itself | `Core/Memory/Encoding/DecimalBits.cs` |
| Fields wider than one element | `FixedLengthDataExtensions` in `Core/Memory/IFixedLengthData.cs` |
| Writing | `HollowObjectWriteRecord.SetDecimal`, `HollowObjectTypeWriteState` |
| Reading | `IHollowObjectTypeDataAccess.ReadDecimal`, `HollowObjectTypeReadState` |
| Deltas | `HollowObjectTypeDataElements.ApplyDelta` |
| Field filtering | `HollowObjectTypeDataElements.RemoveExcludedFieldsFromFixedLengthData` |
| Hashing and comparison | `HollowReadFieldUtils`, `SetMapKeyHasher`, `HollowWriteStateEnginePrimaryKeyHasher` |
| Indexing | `HollowPrimaryKeyIndex`, `HollowPrimaryKeyValueDeriver` |
| Mapping a CLR `decimal` | `HollowObjectTypeMapper.ScalarFieldType`, `HollowObjectMapper.DefaultTypeName` |
| Copying a record back out of a read state | `HollowObjectCopier.Copy`, which restore depends on |

## Layout

| Java | .NET |
| --- | --- |
| `hollow/src/main/java/com/netflix/hollow/...` | `dotnet/src/Hollow/...` |
| `hollow/src/test/java/com/netflix/hollow/...` | `dotnet/tests/Hollow.Tests/...` |
| (no equivalent) | `dotnet/samples/Hollow.Sample/` |
| package `com.netflix.hollow.core.memory.encoding` | namespace `Hollow.Core.Memory.Encoding` |
| package `com.netflix.hollow.api.error` | namespace `Hollow.Api.Error` |
| package `com.netflix.hollow.api.consumer` | namespace `Hollow.Api.Consumer` |
| package `com.netflix.hollow.api.client` | namespace `Hollow.Api.Client` |
| package `com.netflix.hollow.api.producer` | namespace `Hollow.Api.Producer` |
| package `com.netflix.hollow.tools.checksum` | namespace `Hollow.Core.Tools.Checksum` |

Java packages map to .NET namespaces one for one with the `com.netflix` prefix dropped and each
segment PascalCased. Java's one-public-type-per-file rule is not followed where a type is trivially
small and only meaningful alongside its neighbour (for example the iterator interfaces and their empty
implementations share a file).

## Naming changes

Where a Java name could not be carried over, the reason is recorded in a `<remarks>` block on the .NET
type. The systematic changes are:

- **Interfaces take an `I` prefix.** `ByteData` → `IByteData`, `FixedLengthData` → `IFixedLengthData`,
  `VariableLengthData` → `IVariableLengthData`, `ArraySegmentRecycler` → `IArraySegmentRecycler`,
  `HollowDataset` → `IHollowDataset`, `TypeFilter` → `ITypeFilter`, `HollowOrdinalIterator` →
  `IHollowOrdinalIterator`, `HollowTypeStateListener` → `IHollowTypeStateListener`, and the
  `Hollow*TypeDataAccess` family.
- **Getters and setters become properties.** `getName()` → `Name`, `numFields()` → `FieldCount`,
  `setElementTypeState(x)`/`getElementTypeState()` → `ElementTypeState { get; set; }`.
- **`HashCodes.hashCode(...)` → `HashCodes.Compute(...)`**, because a static method named after
  `object.GetHashCode` reads as an override, and `HashCode` is an unrelated BCL type. The hash values
  are unchanged — they are part of the blob layout contract.
- **Enums with per-constant data become plain enums plus an extensions class.** C# enums cannot carry
  fields, so `HollowObjectSchema.FieldType` is the top-level `FieldType` enum with
  `FieldTypeExtensions`, and `HollowSchema.SchemaType` is `SchemaType` with `SchemaTypeExtensions`.
  `FieldType` members are PascalCase (`FieldType.Int`), so `ToWireName()` maps them back to the
  upper-case names the blob format uses.
- **Default interface methods that are pure helpers become extension methods.** A C# default interface
  member is only callable through the interface, which would force a cast at most call sites, so
  `ByteData.readLongBits` → `ByteDataExtensions.ReadInt64Bits` and `HollowDataset.hasIdenticalSchemas`
  → `HollowDatasetExtensions.HasIdenticalSchemas`. Members that implementations override
  (`ITypeFilter.Resolve`) stay on the interface.
- **`getFilePointer()` → `Position`**, since the .NET abstraction is a stream rather than a file.
- **`SegmentedByteArray.copy(long, byte[], int, int)` → `CopyTo`**, to make the direction explicit
  where Java relies on parameter order.
- **`Blob.getInputStream()` → `OpenStream()`**, because the .NET name describes what the call does —
  it opens a new stream each time — rather than reading as a property access.
- **`HollowConsumer.Builder.withX(...)` → `HollowConsumerBuilder.WithX(...)`**, and likewise
  `HollowProducer.Builder` → `HollowProducerBuilder`. Java makes each builder generic in itself so that
  a subclass keeps the fluent return type; C# extension methods cover that case, so the port's builders
  are plain sealed classes.
- **`HollowProducer.Populator` → the `Populator` delegate.** Java declares it as a functional
  interface; a delegate is what a C# lambda binds to without ceremony. `HollowProducer.Validator` and
  the rest of the listener family stay interfaces, since an implementation carries state.
- **`HollowProducer.Incremental.IncrementalPopulator` → the `IncrementalPopulator` delegate**, for the
  same reason. `IncrementalWriteState` becomes `IIncrementalWriteState` under the interface rule
  above.
- **`HollowTypeReshardingStrategy.getInstance(typeState)` → `ForType(typeState)`**, since `GetInstance`
  reads as a singleton accessor where this picks a strategy by record kind. `shardingFactor` →
  `ShardingFactor`, `reshard` → `Reshard`.
- **Java's per-kind `Hollow*TypeShardsHolder` classes become one generic `ShardsHolder<TShard>`.** Java
  needs a class per record kind so that `getShards()` can return a covariant array; a type parameter
  does the same job.
- **`core.index.FieldPath` → `ValueFieldPath`.** Java has two unrelated things called `FieldPath`: the
  package-private one that reads values out of records, and `FieldPaths.FieldPath`, the bound path the
  indexes are built on. Java gets away with it because one is nested; this port cannot nest it, so the
  value-reading one says what it does.
- **`TST` → `TernarySearchTree`**, and `HollowSparseIntegerSet.IndexPredicate` → the
  `IndexPredicate` delegate, for the same reason as `Populator`.
- **`HollowSparseIntegerSet.size()` → `EstimateBitsUsed()`**, because `Size` reads as a count of
  members where the method returns a count of bits — `Cardinality()` is the one that counts members.
- **An acronym of three letters or more is PascalCased**, per .NET's own guidance, so `HollowAPI` →
  `HollowApi`, `HollowObjectTypeAPI` → `HollowObjectTypeApi`, and the namespace `api` → `Api`. Two-
  letter acronyms keep both letters (`IO`), which is why `HollowBlobInput` and friends are unchanged.
- **package `core.type` → namespace `Hollow.Core.Types`**, because `Hollow.Core.Type` would shadow
  `System.Type` inside the `Hollow.Core` namespace: every unqualified `Type` in a file under it would
  resolve to the namespace and fail to compile. The plural also reads correctly, since the namespace
  holds several types rather than describing one.
- **Java's six generated scalar APIs become two generic types.** `HString`, `HInteger`, `HLong`,
  `HDouble`, `HFloat` and `HBoolean` each come with their own type API, two delegates, a record class
  and a factory in Java. Here they are `HollowScalarTypeApi<TValue>` and `HollowScalar<TValue>`, with
  the six names kept as thin subclasses so generated code and callers read the same as Java's.
  `HDecimal` is added for this port's field type.
- **`HollowRecordDelegate` and friends take the `I` prefix** under the interface rule, and Java's
  `HollowObjectDelegate` pair — a lookup and a cached implementation per type — keeps that shape:
  `IHollowObjectDelegate`, `HollowObjectAbstractDelegate`, `HollowObjectGenericDelegate`.
- **`@FieldPath` → `FieldPathAttribute`**, since .NET spells an attribute with the suffix, and Java's
  `order()` element becomes the `Order` property: C# reflection defines no order over a type's
  members, where Java's `getDeclaredFields` happens to be stable enough for Hollow to rely on.
- **`UniqueKeyIndex.from(consumer, type)` → `UniqueKeyIndex.From<T>(consumer)`**, with the builders as
  separate non-generic entry points. Java can hang a static factory with its own type parameters off
  the generic class; C# cannot do it in a way that reads well, so `UniqueKeyIndex` and `HashIndex` are
  static classes alongside `UniqueKeyIndex<T, TKey>` and `HashIndex<T, TQuery>`. The type arguments
  that Java passes as `Class` objects — `usingPath(path, Integer.class)` — are type arguments here:
  `UsingPath<int>(path)`, so the two cannot disagree.
- **`HollowAPIFactory` → `IHollowApiFactory`**, with `DelegateHollowApiFactory` added for the case
  Java has no name for: building a known API type without reflecting over its constructors.
- **Java's four `Hollow*MissingDataAccess` classes are identified by a marker interface**,
  `IHollowMissingTypeDataAccess`, rather than by naming all four concrete classes at each check.

## Behavioural differences

### The fixed-length bit string no longer uses unaligned reads

Java's `FixedLengthElementArray` reads elements with `sun.misc.Unsafe`, loading eight bytes at an
unaligned byte offset inside a `long[]`. That is fast, but it is little-endian-only, it reads past the
end of a segment (which is why Java allocates one extra `long` per segment and duplicates the next
segment's first word into it — the "fencepost"), and it silently corrupts element values wider than 58
bits at unlucky bit offsets. Java's own tests for that corruption are commented out in the source.

This port composes each element from the one or two 64-bit words that actually contain it. The
consequences:

- No fencepost slot is allocated or maintained, so `IArraySegmentRecycler.GetLongArray()` returns
  exactly `1 << Log2OfLongSegmentSize` elements rather than one more.
- `GetElementValue` and `GetLargeElementValue` are now equivalent. Both are kept so ported call sites
  read like the original.
- Element widths up to 64 bits are correct at every offset. `FixedLengthElementArrayTests` pins this.
- Reads that straddle a word boundary cost an extra shift and or.

The serialised form is unchanged: it is, and always was, a count followed by that many big-endian
64-bit words.

### `setNull` on a variable-length field

Java's `HollowObjectWriteRecord.setNull` marks the field *present* and writes a null marker into its
buffer. For fixed-length fields that is equivalent to leaving the field unset, but for `STRING` and
`BYTES` the field is then serialised with a length prefix in front of the marker, so it reads back as
a one-byte value rather than as null. This port marks the field absent instead: byte-identical to
Java for every fixed-length type, and correct for the variable-length ones.
`RoundTripTests.ExplicitlyNulledStringReadsBackAsNull` pins it.

### The object mapper reads public members, not private fields

Java's `HollowObjectMapper` maps every declared field, reaching private state through
`sun.misc.Unsafe`. This port maps public instance properties with a getter, and public instance
fields, in declaration order. That is the idiomatic .NET surface and avoids reflecting over private
state, but it means a type's schema follows its public shape; use `[HollowTransient]` to exclude a
member. Collection type names follow Java's convention (`ListOfString`, `SetOfInteger`,
`MapOfStringToInteger`), and the scalar wrapper types keep Java's names (`String`, `Integer`, `Long`
and friends) so a .NET-produced blob describes the same types a Java-produced one would.

A `decimal` member maps to the extension field type and, when non-nullable, is inlined like the other
numeric value types — the CLR does not classify `decimal` as primitive, but it behaves like one here.
A `decimal?` becomes a reference to a wrapper type named `Decimal`, which no Java-produced blob will
ever contain.

### NaN bit patterns

`HollowObjectWriteRecord` derives its float and double null sentinels from Java's canonical NaN
(`0x7FC00000` / `0x7FF8000000000000`) plus one. .NET's `float.NaN` and `double.NaN` have the sign bit
set, so deriving the sentinel the way Java does would produce a different, incompatible value. The
sentinels are written as literals, and a NaN *value* is canonicalised to Java's pattern before being
stored, matching `Float.floatToIntBits` rather than `floatToRawIntBits`.

### The two unique-key indexes

Java has `HollowPrimaryKeyIndex` and `HollowUniqueKeyIndex`, which answer the same questions. The
difference is where they get their type accesses: the primary key index walks the schema's referenced
type states on every lookup, while the unique key index resolves them through the data access it was
given and keeps them.

In Java that is what lets the unique key index survive more than two deltas without being rebuilt when
object longevity is on. Object longevity is not ported, so here the difference is narrower: the unique
key index works against any `IHollowDataAccess` rather than requiring a `HollowReadStateEngine`, and
does not depend on the schema's mutable type-state wiring. Both are ported because the distinction is
real and a caller may want either; they share their hash table through `UniqueKeyHashTable`, where Java
duplicates it.

### The prefix index takes a tokenizer rather than being subclassed

Java's `HollowPrefixIndex` exposes a `protected getKeys` to override, which is how its own tests split
a title on whitespace so that a query matches a word anywhere in it rather than only at the start.
This port takes a delegate instead and keeps the class sealed:

```csharp
using HollowPrefixIndex index = new(
    stateEngine, "Movie", "Title.value",
    tokenizer: keys => keys.SelectMany(key => key.Split(' ')));
```

Two smaller differences in the same class. Java lowercases a case-insensitive key with the default
locale, which is wrong in Turkish among others; this uses the invariant culture, per the rule above. And
Java throws on a record whose path reaches a null string, where this simply does not index that record
— its own documentation says nulls are not indexable.

### The prefix index sizes its tree from the keys, not from the type behind the path

The node capacity of the ternary search tree is allocated up front and cannot grow, so the estimate has
to cover the worst case: a tree so unbalanced that every character of every key gets a node. Java
derives the average key length by reading field 0 of the type at the end of the path, which reads the
wrong field when the path ends at an inline string and misses keys entirely when the path crosses a
collection. This measures the keys the index is actually about to insert.

### An index that matches on nothing is refused

`HollowHashIndex` requires at least one match field. With none, every record has a zero-bit key, and
the tables use a bit of the key to tell an occupied bucket from an empty one — so Java builds such an
index without complaint and then finds only some of the records. This port rejects it at construction.
`HashIndexTests.AnIndexWithNoMatchFieldsIsRejected` pins that.

### A declared hash key replaces ordinal-based lookup

This is inherent to the format rather than specific to the port, but it is easy to trip over. When a
set or map schema declares a hash key, the producer places each element in the bucket a consumer
probing by that key will look in — not the bucket its ordinal hashes to. `Contains`, `Get` and the
potential-match iterators probe by ordinal, so on a keyed collection they no longer find anything
reliably; use `FindElement`, `FindKey`, `FindValue` and `FindEntry` instead. Iteration is unaffected,
because it walks the buckets rather than probing them.
`HashKeyTests.ADeclaredKeyReplacesOrdinalBasedLookup` pins this.

The object mapper derives a hash key for a set or map that declares none, as Java does: the element or
key type's `[HollowPrimaryKey]` if it has one, otherwise its single field if it maps to exactly one
non-reference field. So a mapped `HashSet<int>` or `Dictionary<string, T>` is keyed by default and
loses ordinal-based lookup. Declare `[HollowHashKey]` with no field paths to opt one collection out,
or set `HollowObjectMapper.UseDefaultHashKeys` to `false` to opt a whole model out.

Where Java silently keeps whichever schema was registered first when two members declare different
hash keys over the same CLR set type — both are `SetOfX`, but their records would be hashed
differently — this port compares the schemas and reports the collision instead.

### The consumer's nested types are flattened into a namespace

Java packs the consumer's contracts into `HollowConsumer` as nested types: `HollowConsumer.Blob`,
`HollowConsumer.BlobRetriever`, `HollowConsumer.RefreshListener` and a dozen more. They are top-level
types in `Hollow.Api.Consumer` here, where the names read the same once the namespace is accounted for
and `HollowConsumer` itself stays a manageable size. The interfaces take the usual `I` prefix.

Three things around the edges of Java's consumer are absent, because what they exist to serve is not
ported: the generated `HollowAPI` layer (so `IRefreshListener` takes the read state engine alone,
without the `HollowAPI` parameter Java passes alongside it), object longevity, and metrics collection.

### The consumer refreshes through a task rather than an executor

Java's consumer takes an `Executor` and offers `triggerAsyncRefresh()` and
`triggerAsyncRefreshWithDelay(int)`. The port has one `TriggerRefreshAsync(TimeSpan, CancellationToken)`
returning a `Task`, which is how a .NET caller expects to control scheduling and cancellation.

Java exposes the refresh lock as a `Lock` for the caller to take and release. `AcquireRefreshLock()`
returns an `IDisposable` instead, so a `using` block cannot leak it. It is thread-affine, like the
`ReaderWriterLockSlim` behind it, so it must not be held across an `await`.

### A delta-only consumer can still load its first snapshot

A consumer with no announced version to go to initialises to an empty state, and Java sets its
"snapshot next time" flag while deliberately ignoring the double-snapshot config — the empty state is
not real data and there is nothing to protect. But the code that applies the plan then checks whether
a data holder exists at all, and the empty one does, so a delta-only consumer in that position could
never load any data.

This port checks whether the holder has actually loaded a version. A holder that has not is treated as
absent, which is what the flag it was given plainly intended.

### A delta may name a type the consumer does not hold

A producer that adds a type publishes a delta mentioning it, and the type is new to every consumer on
the chain. A consumer that filtered a type out is in the same position. Either way the delta's bytes
for that type are skipped and the rest of it is applied; the type arrives with the next snapshot,
since a delta carries records but not a data model. This matches Java, and is why
`RestoreTests.RecordsMatchOnTheirCommonFieldsWhenTheSchemaChanges` has to take a snapshot before the
consumer sees the new type.

### Rolling the cycle forward is idempotent

`HollowWriteStateEngine.PrepareForNextCycle` does nothing when the engine is already accepting
records, and `PrepareForWrite` does nothing when it is already prepared. Java does the same, and it
matters more than it looks: `RestoreFrom` leaves the engine accepting records, so a caller that
routinely calls `PrepareForNextCycle` at the top of every cycle would otherwise discard the restored
ordinal assignment and produce a delta claiming that every record had changed.

### The filesystem blob store matches a transition on its whole from-version

Java finds a delta by testing whether a file name starts with `delta-` followed by the current
version, which also matches version 12's delta when looking for version 1's. It gets away with it
because a real version is a fixed-width timestamp. This port matches `delta-{from}-`, so a test — or a
deployment using small sequential versions — behaves the same way as production.

### The producer's cycle refuses more than Java's does in two places

A listener that throws is normally swallowed — a producer's job is to publish data, not to run other
people's code. Java's `VetoableListener` and `ListenerVetoException` exist so that a listener which
genuinely needs to stop a cycle can, but the dispatch in this fork catches everything regardless,
which makes both of them dead. This port honours them. Since the port takes no logging dependency
where Java logs a warning, a swallowed exception is surfaced through the
`HollowProducer.ListenerFailed` event instead.

A validator that throws is a separate case, and here Java and this port agree: a broken validator
cannot vouch for the data, so it becomes an `Error` result and fails the cycle. The other validators
still run first, so one report says everything that is wrong at once.

### The change validators tell a replacement from an add and a remove themselves

`ObjectModificationValidator` and `RecordCountPercentChangeValidator` judge what a cycle *did* rather
than what it holds, which the blob format does not record: an ordinal holds one value forever, so a
record is only ever added or removed. Telling a replacement apart needs the type's primary key, and
Java does that inside `AbstractHollowDataAccessor` — the base class of the `api.consumer.data` record
collections, which are not ported. Here it is `RecordChangeSet.Compute`, standing on its own. That
also makes it usable without materialising a record per change, which the Java base class forces.

Three departures in the validators themselves:

- `ChangeThreshold` leaves an unset bound **unchecked**, where Java encodes "unset" as a negative
  number. A caller cannot pass Java's sentinel by accident because there is nothing to pass.
- A constant bound is range-checked **where it is written**, not cycles later. A threshold read afresh
  each cycle cannot be, so that one is checked when it is read and a nonsensical value becomes an
  `Error` result rather than an exception escaping `OnValidate`.
- `ObjectModificationValidator<T>` takes one function from data access and ordinal to record, where
  Java takes two — one building the API, one reading a record out of it — and is generic in the API
  type as well. A generated API's accessor fits the single function directly:
  `(dataAccess, ordinal) => new CatalogueApi(dataAccess).GetMovie(ordinal)!`.

### The blob compressor compresses staging, not the blob store

`IBlobCompressor` wraps the bytes a blob is *staged* as. A publisher reads a staged blob back through
the same compressor, so what reaches the blob store — and what a consumer downloads — is plain. The
gain is that a large cycle's four staging files stay small while the producer holds them all at once.
This is Java's behaviour, and it is easy to misread the name; a blob store that wants compression at
rest does it in its own publisher, where the consumer side can be made to match.

### The write state engine mints its own randomized tag

A delta names the state it applies to by a randomized tag, which is what stops a consumer applying it
to the wrong state. Java mints a fresh one in the write state engine's constructor and on every
`prepareForNextCycle`; earlier revisions of this port left the tag at zero unless a caller set one,
which made the check vacuous by default. It now mints as Java does. A test that compares blob bytes
still pins the tag by assigning `RandomizedTag` after construction, which is when the tests that do
this were already doing it.

### The checksum walks shards in Java's order

`HollowChecksum` folds each value into a running hash, so the order the records are visited in is part
of the result. The walk is shard-major, as Java's is, rather than in ordinal order — which is the
simpler thing to write and would produce a different number for a multi-sharded type. Two checksums
are only ever compared against each other, so either would work, but matching Java keeps the values
comparable if anyone ever does transport one.

One consequence is worth stating, since resharding makes it reachable: a checksum is only meaningful
between two states at the same shard count. Rearranging a state changes the walk order and so changes
the number, even though the data is identical. The producer's integrity check is unaffected — it
compares states of the same cycle, which agree on the count — but a test that reshards and expects the
checksum to hold still is asserting the wrong thing.

### The incremental producer is a method, not a separate producer

Java splits the incremental API into a `HollowProducer.Incremental` subclass, built through
`HollowProducer.Builder.buildIncremental()`, and a caller has to decide up front which kind of
producer it wants. Here `RunIncrementalCycle` sits alongside `RunCycle` on `HollowProducer`, so the
same producer can run either kind of cycle. Nothing in the cycle distinguishes them after population —
the incremental populator is turned into an ordinary one — so there was nothing for the split to
protect.

Java's deprecated `HollowIncrementalProducer`, the standalone class that predates
`HollowProducer.Incremental`, is not ported.

### An incremental cycle's changes are collected before any of them are applied

Java's incremental write state writes into a `ConcurrentHashMap` and so does this one, which is what
makes the populator safe to run across several threads and makes reporting the same record twice the
last word winning. Nothing is applied to the write state until the populator returns. A populator that
throws therefore leaves the previous version exactly as it was, and the version consumers are on is
still announced.

### Delta application has two paths, and a test that proves it

Each applicator carries a run of records the delta leaves alone across wholesale — one `CopyBits` for
the records, one byte copy per variable-length field, and one strided `IncrementMany` to correct the
pointers that moved — and falls back to merging record by record when it cannot. It cannot when any
width moved, since then a record no longer occupies the same bits it did; the collections also stop a
run at a pending removal, whose elements are dropped and which therefore shifts what follows it by a
different amount.

The two paths produce identical output by design, which is exactly what makes the choice invisible:
`DeltaTests` and `CollectionDeltaTests` compare an applied delta against a snapshot of the same cycle
and would pass whichever ran. So `DeltaDiagnostics` counts the records carried across in bulk, and the
tests named `…CarriedAcrossInBulk` assert the count. Without them the bulk path would be dead code the
suite never reaches: it takes a large dataset changed in one place to get there, and every other test
is small enough that a width moves on every cycle.

One case needs saying because it is invisible in the counts: a type whose records did not change at
all is left out of the delta entirely by `HollowBlobWriter`, so nothing is applied for it and nothing
is carried across.

The resharding splitters and joiners have the same two paths, for the same reason and guarded the same
way — `AReshardMovesRecordsItDoesNotHaveToRebuild` is the test that says which one ran. A split
interleaves ordinals across the new shards, so there is never a run of them going to the same place;
what is bulk-copied there is one record at a time, and one collection's elements at a time.

### A type's ordinal map can be partitioned four ways

`HollowWriteStateEngine.PartitionedOrdinalMap`, or `WithPartitionedOrdinalMap()` on the producer
builder, gives each type four `ByteArrayOrdinalMap`s instead of one. A record's hash picks the map, and
the map's index becomes the low two bits of the ordinal it hands back, so a global ordinal is
`(local << 2) | mapIndex`. What it buys is throughput: populating a cycle from several threads then
contends on four write locks rather than one.

The costs are real and worth stating, because the default is off:

- **Ordinals are interleaved rather than consecutive.** A type spends two bits of the 29-bit ordinal
  space, and its records lose some of the locality a delta relies on.
- **Holes are reclaimed more slowly.** The free-ordinal pool is per map, so a hole waits for a record
  that hashes to *its* partition rather than for the next record at all.

Two things have to hold whatever the routing does, and both are tested. Deduplication is across the
whole type, so `Add` searches every map before assigning — the hash-routed one first, since that is
almost always where a record is. And a restored producer has to hand a re-added record the ordinal the
published state gave it, so restore places each record in the map its published ordinal belongs to
rather than the one its hash would choose.

### `SetElementValue` assumes the bits it is writing are zero

It ORs, because the storage it was designed for is freshly allocated. That is fine everywhere the
original port used it and a trap for anything that writes twice: bulk-copying a record and then
correcting one of its fields leaves the two values ORed together. `HollowObjectTypeDataElements.CopyRecord`
calls `ClearElementValue` first for exactly this reason. `IncrementMany` has no such problem — it is
arithmetic on what is there, which is why the delta applicators correct pointers with it instead.

### Concurrency primitives

Java's `AtomicLongArray` has no .NET equivalent. `ThreadSafeBitSet` and `ByteArrayOrdinalMap` use
`long[]` with `Volatile` and `Interlocked` operations instead, which give the same publication
guarantees.

`SegmentedByteArray.orderedCopy` issues one release fence for the whole block rather than Java's
`Unsafe.putByteVolatile` per byte. This is sufficient: a record is only ever published by writing its
ordinal after its bytes, so one fence before that publication orders all of it.

### Empty arrays

`SegmentedLongArray` computes its segment count as `((numLongs - 1) >>> log2) + 1`, which underflows
into a nonsensical count when `numLongs` is zero. The port allocates one segment in that case, so a
type present in the schema with no records works.

### Logging

The port takes no logging dependency. Where Java logs, the port either throws or raises an event —
`ByteArrayOrdinalMap.SoftLimitBreached` is the one instance so far.

### Java-compatible binary IO

`HollowBlobInput` and `HollowBlobOutput` reproduce `java.io.DataInput`/`DataOutput`: big-endian
integers and modified UTF-8 strings (`ModifiedUtf8`). This has no BCL equivalent —
`BinaryReader.ReadString` uses a 7-bit-encoded length and standard UTF-8 — and is required for a .NET
consumer to read a blob a Java producer wrote. `BlobIoTests` pins the byte sequences.

Java's `HollowBlobInput` abstracts over `DataInputStream` and `RandomAccessFile` to serve its two
memory modes; .NET's `Stream` covers both, so the port wraps a stream and reports seekability through
`CanSeek`.

### A variable-length field with nothing in it has no storage behind it

If a var-length field is null or empty in every record of a type, no byte storage is allocated for it
at all. Java's readers dereference the storage before checking the length and so throw on such a
field; the port checks first and reads it as empty. This only became visible with the span reads,
which is where a type with one empty field is an ordinary case rather than a degenerate one.

### The object mapper registers referenced types before the type that references them

A type appears in the blob after everything it points at, so that a delta — applied type by type in
that order — lets a listener on the referencing type follow a reference and find the new record rather
than the one it is replacing. Java gets that order as a side effect of building its sub-mappers inside
the mapper's constructor; the port's mapper builds them lazily, so it registers them explicitly.
Nothing noticed until `HollowSparseIntegerSet` arrived, since it is the first delta listener that
reads record data while the delta is being applied.

### A generated client reads a dataset that is missing a whole type

Java's generated API constructs a `Hollow*MissingDataAccess` for a type the dataset does not have, and
its `HollowObjectTypeAPI` special-cases that class so it never reads the absent schema. The port does
the same through `IHollowMissingTypeDataAccess`, and adds `HollowTypeApi.IsTypePresent` so generated
code can enumerate such a type as empty rather than walking populated ordinals that do not exist —
Java's equivalent throws there.

### A delta publishes each shard before announcing it

For the same reason, a type read state publishes its new shard array before notifying listeners and
before returning the storage it replaced to the recycler. A listener may read the records it is being
told about, and a concurrent reader must not be left pointing at storage that has gone back to the
pool. Java's ordering is safe only because nothing there reads during the notification.

## The explorer

`Hollow.Explorer` is the port of `hollow-explorer-ui`: four pages over a dataset — every type and what
it costs, a page of one type's records with one of them written out, a schema as a tree opened a branch
at a time, and a search built a clause at a time.

### It is ASP.NET Core MVC, not Blazor

These pages are documents with links, and every bit of state a link needs is already in the URL. A
component model would add a connection to keep open and buy nothing back. The HTML is carried over
from the Velocity templates close to verbatim, inline styles and all — what changed is only what was
framework rather than page:

| Velocity | Razor |
| --- | --- |
| `$esc.html($x)` | `@x` — the framework's own encoding, not a tool placed in the context |
| `$esc.url($x)` | `Uri.EscapeDataString(x)` |
| `#showSchema` recursing on itself | `_SchemaDisplay.cshtml` rendering itself as a partial |
| header template + page + footer template | a layout with `@RenderBody()` |
| a page class building a `VelocityContext` | a controller action returning a view model |

Java's four page classes each existed to build a context and merge three templates, so each page is
now one method rather than one class.

### The record is written through the response, and escaped by hand

A record holding a large collection is large, so the browse page writes it straight into the response
rather than building it up as a string first — which is what Java's `HtmlEscapingWriter` is for, and
why `HtmlEscapingTextWriter` is here rather than being replaced by Razor.

It escapes by hand rather than through `System.Text.Encodings.Web`. Every encoder that class offers
escapes newlines: they are control characters, and no set of allowed Unicode ranges can let one
through, because `ForbidUndefinedCharacters` removes the whole `Cc` category after the ranges are
applied. A record laid out over twenty lines would arrive inside its `<pre>` as one line of `&#xA;`.
What is left — replacing `&`, `<`, `>`, `"` and `'` — is what Java's `escapeHtml4` does to the same
text, and is enough because the destination is element content and nothing else.

The rest of the page still uses Razor's default encoder, which does escape newlines. That shows up in
the schema column as `&#xA;` in the markup; it renders correctly, because a browser decodes character
references inside `<pre>`. Registering a laxer encoder would have fixed the markup at the cost of
changing the encoder for every view in whatever application the explorer was mounted in, which is not
a library's call to make.

### Session state is the explorer's own, not ASP.NET Core's

Two things outlive a request: the search being built, and which schema branches the reader has opened.
Neither is data — both are a place in the data that took several requests to reach and that a URL is
the wrong size to carry. ASP.NET Core's session state stores `byte[]`, so using it would mean
serialising a result set and a schema tree on every request to deserialise them on the next. Java
keeps the objects themselves in the servlet session, and so does `ExplorerSessionStore`, behind a
cookie of its own so that a host embedding the explorer does not have to wire up session middleware to
get a working page.

This keeps a reader's state in the process serving them. Behind a load balancer without sticky
sessions they would lose their place on whichever request landed elsewhere — which is the same
constraint Java has, and acceptable for what the explorer is.

### Two places where the port does not reproduce a bug

- `browse-selected-type-top.vm` guards the record block with `#if($ordinal != $null)`. The ordinal is
  an `int` boxed into the context, so it is never null, and a page showing no record still renders
  the FORMAT links and `ordinal: -1`. The port asks what the template meant to ask.
- `HollowRecordJsonStringifier` breaks the line between a list's elements but not a set's, so a
  pretty-printed set arrives with every element after the first on one line. It is the same JSON
  either way, and nothing reads the output but a person.

The template's `</td>...</th>` mismatch on the home page's type cell is also corrected.

### An empty field submits nothing

Java's query page adds a clause for whatever the form contained, so submitting it empty leaves the
reader with a search matching nothing and no way back but clearing it. The port requires a field name
before it will add a clause.

### Where `formatBytes` went

`hollow-ui-tools`' `HollowDiffUtil.formatBytes` is `Hollow.Explorer.ByteSize.Format`, named for what
it does rather than the class it happened to live in. Its arithmetic runs in `double` throughout,
which is what Java's does as soon as it takes a logarithm — so the rounding matches, and
`long.MinValue` comes out as `-8 EiB` rather than overflowing the way negating it would. Java's test
is ported alongside it.

Nothing else in `hollow-ui-tools` needed porting: `HollowUIRouter`, `HollowUIWebServer`,
`HttpHandlerWithServletSupport`, `HollowUISession` and `EscapingTool` are all plumbing that ASP.NET
Core replaces outright.

### Mounting it

`AddHollowExplorer(explorer, basePath)` and `MapHollowExplorer()` put the pages into an application
that already exists — usually the one already holding the dataset, which is also the one that already
has authentication. `HollowExplorerServer` is the other way in, mirroring Java's Jetty server: it
binds the loopback address rather than `localhost`, because a dev tool showing a whole dataset has no
business being reachable from the network, and because Kestrel will not take an ephemeral port under
the name.

## Status

### Ported and tested

| Java package | Notes |
| --- | --- |
| `core.memory` | `IByteData`, `ArrayByteData`, `ByteDataArray`, `SegmentedByteArray`, `SegmentedLongArray`, `ByteArrayOrdinalMap`, `FreeOrdinalTracker`, `ThreadSafeBitSet`, `IFixedLengthData`, `IVariableLengthData`, `MemoryMode` |
| `core.memory.encoding` | `ZigZag`, `VarInt`, `HashCodes`, `FixedLengthElementArray`, `FixedLengthMultipleOccurrenceElementArray`, and `DecimalBits` (port-specific; see the format extension above) |
| `core.memory.pool` | `IArraySegmentRecycler`, `WastefulRecycler`, `RecyclingRecycler` |
| `core.schema` | `HollowSchema` and the object/list/set/map schemas, `FieldType`, `SchemaType`, `SimpleHollowDataset`, `HollowSchemaSorter`, `HollowSchemaHash` |
| `core.index` | `FieldPaths` and the bound `FieldPath`/`FieldSegment`/`ObjectFieldSegment`/`FieldPathException` types, `HollowPrimaryKeyIndex`, `HollowUniqueKeyIndex`, `HollowHashIndex` and its builder, preindexer, field and result types, `HollowPrefixIndex` and the `TernarySearchTree` behind it, `HollowSparseIntegerSet`, `ValueFieldPath` (Java's package-private `core.index.FieldPath`), `GrowingSegmentedLongArray`, `MultiLinkedElementArray` |
| `core.index.traversal` | The traversal tree and `HollowIndexerValueTraverser`, which enumerate every combination of values a record's indexed paths reach |
| `core.index.key` | `PrimaryKey`, including its dataset-resolution helpers, and `HollowPrimaryKeyValueDeriver` |
| `core` | `HollowConstants`, `IHollowDataset`, `HollowHeaderTags` (the header tags `HollowStateEngine` declares) |
| `core.util` | `BitSet` and `IntList` (port-specific stand-ins for `java.util.BitSet` and Hollow's `IntList`), `InvariantFormatting`, `HollowWriteStateCreator` |
| `tools.checksum` | `HollowChecksum` and `ApplyToChecksum` on the four read states, which the producer's integrity check compares |
| `core.write` | The write records (object, list, set, map), `FieldStatistics`, `HollowTypeWriteState` and its four subclasses including the four-way partitioned ordinal map, `HollowWriteStateEngine`, `HollowBlobHeaderWriter`, `HollowBlobWriter`, `HollowBlobOutput` |
| `core.write.copy` | `HollowRecordCopier` and the object/list/set/map copiers, plus `IOrdinalRemapper`/`IdentityOrdinalRemapper` (Java puts the remapper in `tools.combine`) |
| `core.write.objectmapper` | `HollowObjectMapper`, the four type mappers, and the `HollowTypeName`/`HollowInline`/`HollowTransient`/`HollowPrimaryKey`/`HollowHashKey`/`HollowShardLargeType` attributes, including Java's default hash-key derivation |
| `core.read` | `HollowBlobInput`, `HollowBlobHeaderReader`, `HollowBlobReader`, `HollowReadStateEngine`, `HollowTypeReadState`, `PopulatedOrdinalListener`, `SnapshotPopulatedOrdinalsReader`, the data-access interfaces, `ITypeFilter` |
| `core.read.engine.*` | Data elements and read states for object, list, set and map |
| `core.read.iterator` | The ordinal iterators, including the potential-match iterators used for key lookups |
| `core.memory.encoding` (delta) | `GapEncodedVariableLengthIntegerReader` |
| delta write path | `CalculateDelta`/`WriteCalculatedDelta` on all four type write states, `HollowBlobWriter.WriteDelta` |
| delta read path | `ApplyDelta` on the data elements and read states of all four record kinds, `HollowBlobReader.ApplyDelta` |
| reverse deltas | `HollowTypeWriteState.CalculateReverseDelta`, `HollowBlobWriter.WriteReverseDelta`; a consumer applies one through the same `ApplyDelta` |
| hash keys | `HollowWriteStateEnginePrimaryKeyHasher` on the write side, `SetMapKeyHasher` and `FindElement`/`FindKey`/`FindValue`/`FindEntry` on the read side |
| restore | `HollowWriteStateEngine.RestoreFrom` and `HollowTypeWriteState.RestoreFrom`, `HollowWriteStateCreator` |
| resharding (read) | `ShardsHolder`, `HollowTypeDataElements`/`HollowTypeReadStateShard`, the data element splitters and joiners for all four record kinds, `HollowTypeReshardingStrategy`, `GapEncodedVariableLengthIntegerReader.Split`/`Join` |
| resharding (write) | `HollowWriteStateEngine.AllowTypeResharding`, `GatherShardingStats` with its one-factor-of-two-per-cycle rule, `RevNumShards`/`RevMaxShardOrdinal` and the direction-aware delta writers, the `hollow.type.resharding.invoked` header tag |
| `tools.traverse` | `TransitiveSetTraverser` — `AddTransitiveMatches`, `RemoveReferencedOutsideClosure`, `AddReferencingOutsideClosure` |
| `api.consumer` | `HollowConsumer` and `HollowConsumerBuilder`, `Blob`/`HeaderBlob`/`BlobType`, `IBlobRetriever`, `IAnnouncementWatcher`/`VersionInfo`/`AnnouncementStatus`, `IRefreshListener`/`ITransitionAwareRefreshListener`/`IRefreshRegistrationListener`/`HollowRefreshListener`, `IDoubleSnapshotConfig`, `IUpdatePlanBlobVerifier` |
| `api.consumer.fs` | `HollowFilesystemBlobRetriever`, `HollowFilesystemAnnouncementWatcher` |
| `api.client` | `HollowUpdatePlan`, `HollowUpdatePlanner`, `FailedTransitionTracker`, `HollowDataHolder`, `HollowClientUpdater` |
| `api.producer` | `HollowProducer` and `HollowProducerBuilder`, the cycle with its rollback, `Restore`, `Blob`/`HeaderBlob`/`IPublishArtifact`, `IBlobStager`, `IPublisher`, `IAnnouncer`, `IVersionMinter`/`VersionMinterWithCounter`, `IBlobCompressor`, `IWriteState`/`IReadState`/`Populator`, `Status`, `ReadStateHelper` |
| `api.producer.listener` | The per-stage listener interfaces, `HollowProducerListener` as a no-op base, `IVetoableListener`/`ListenerVetoException` |
| `api.producer.validation` | `IValidatorListener`, `ValidationResult` and its builder, `ValidationStatus`, `ValidationStatusException`, `DuplicateDataDetectionValidator`, `RecordCountVarianceValidator`, `MinimumRecordCountValidator`, `NullPrimaryKeyFieldValidator`, `ObjectModificationValidator`, `RecordCountPercentChangeValidator` with `ChangeThreshold` |
| `core.read.engine` (change set) | `RecordChangeSet`, the added/removed/replaced computation Java keeps inside `AbstractHollowDataAccessor` |
| `api.producer` (incremental) | `HollowProducer.RunIncrementalCycle`, `IIncrementalWriteState`/`IncrementalPopulator`, `IIncrementalPopulateListener`, `RecordPrimaryKey`, `HollowObjectMapper.ExtractPrimaryKey`, and the `AddAllObjectsFromPreviousCycle`/`RemoveOrdinalFromThisCycle` family on the write state |
| `api.producer.enforcer` | `ISingleProducerEnforcer`, `BasicSingleProducerEnforcer` |
| `api.producer.fs` | `HollowFilesystemBlobStager`, `HollowInMemoryBlobStager`, `HollowFilesystemPublisher`, `HollowFilesystemAnnouncer` |
| `core.read` (field access) | `HollowReadFieldUtils`, and the allocation-free reads — `ReadStringInto`, `ReadBytesInto`, `TryGetBytesSpan`, `GetVarLengthSequence` on the object data access, with `TryGetSpan`/`CopyTo`/`GetSequence` on `IByteData` behind them |
| `core.read.missing` | `IMissingDataHandler`, `DefaultMissingDataHandler` |
| `api.custom` | `HollowApi`, `HollowTypeApi`, `HollowObjectTypeApi`, `HollowListTypeApi`, `HollowSetTypeApi`, `HollowMapTypeApi` |
| `api.objects` | `IHollowRecord`, `HollowObject`, `HollowList`, `HollowSet`, `HollowMap` |
| `api.objects.delegate` | `IHollowRecordDelegate`, `IHollowCachedDelegate`, the object delegates, and the list/set/map delegates in both lookup and cached form |
| `api.objects.generic` | `GenericHollowObject`, `GenericHollowList`, `GenericHollowSet`, `GenericHollowMap` |
| `api.objects.provider` | `HollowObjectProvider`, `HollowFactory`, `HollowObjectFactoryProvider`, `HollowObjectCacheProvider` |
| `core.type` | `HollowScalarTypeApi<TValue>` and `HollowScalar<TValue>` with `HString`, `HInteger`, `HLong`, `HDouble`, `HFloat`, `HBoolean` and `HDecimal` over them, and a concrete type API for each |
| `core.read.dataaccess.missing` | `HollowObjectMissingDataAccess` and its list, set and map counterparts, behind the `IHollowMissingTypeDataAccess` marker |
| `api.consumer.index` | `FieldPathAttribute`, the match and select extractors, `UniqueKeyIndex` and `HashIndex`/`HashIndexSelect` with their builders |
| `api.client` (API factory) | `IHollowApiFactory`, `DefaultHollowApiFactory`, `DelegateHollowApiFactory`, `GeneratedHollowApiFactory<TApi>`, and `HollowConsumer.Api` |
| `api.codegen` (client API) | `HollowCodeGenerator` and its options, the model resolver, the naming rules and the emitters for all four record kinds, the API class, the API factory and the unique-key index |
| `api.codegen` (source generator) | `Hollow.SourceGenerator`: `HollowApiSourceGenerator`, `SymbolModel` and the `HollowGeneratedApi` attribute — no Java counterpart, since Java has nothing that runs inside the compiler |

Test coverage is carried over from the Java tests where they exist — `VarIntTest`, `HashCodesTest`,
`FixedLengthElementArrayTest`, `FreeOrdinalTrackerTest`, `ThreadSafeBitSetTest`,
`ByteArrayOrdinalTest`, and the schema tests — with additional cases for the port-specific IO layer.
`HashCodesTests` pins MurmurHash3 output against values produced by an independent transcription of
the Java algorithm, so the port cannot drift from the blob layout contract unnoticed.

`RoundTripTests`, `CollectionRoundTripTests` and `ObjectMapperTests` cover the write and read engines
together, which is the only way to check the blob format itself: the bit packing, the variable-length
ranges, the null sentinels, the hash-table layout, the shard layout and the trailing
populated-ordinals bit set all have to agree for a record to survive the trip.

`DeltaTests` and `CollectionDeltaTests` assert whole-state equivalence rather than spot-checking
fields: after a delta the consumer must hold exactly what a consumer that read that cycle's snapshot
outright would hold, ordinals included. A merge that gets a record slightly wrong still looks
plausible field by field.

`HashKeyTests` looks elements up through the read state rather than comparing hash values, because
producer and consumer compute the key hash from entirely different representations of the record —
the producer from its serialised bytes, the consumer from boxed key values — and only agreement
between them makes a lookup work. `ObjectMapperHashKeyTests` covers the same ground from the mapper's
side, including the keys it derives when a model declares none.

`DecimalFieldTests` and `DecimalBitsTests` cover the format extension, and
`FormatCompatibilityTests` pins the bytes of a dataset that does not use it — see
[Format extension: the `Decimal` field type](#format-extension-the-decimal-field-type).

`ReverseDeltaTests` unwinds five chained generations one at a time and checks that going back and
forward again along the same transition lands on the same state. `UniqueKeyIndexTests` asserts that the
two unique-key indexes agree on every query, which is the useful check given they exist to answer the
same questions differently.

`RestoreTests` carries over the scenarios and the expected ordinals from Java's `core.write.restore`
tests, since the whole point of a restore is which ordinal each record ends up with. It also makes the
economic argument directly: after a restart, one changed record out of 201 has to produce a delta a
fraction the size of a snapshot, and a restore that quietly failed to reuse ordinals would produce one
larger than a snapshot while still being correct field by field.

`ConsumerTests` turns on the distinction between the two ways a consumer can move: following deltas
keeps the same `HollowReadStateEngine` instance, so anything built over it is still valid, while a
snapshot replaces it. `Assert.Same` on the state engine is what most of those tests actually check.
`UpdatePlanTests` covers the planning decision on its own, against a stub blob store described by
which versions exist rather than by real blobs. `FilesystemBlobStoreTests` pins the on-disk layout,
which is shared with Java.

`ProducerTests` is mostly about the refusals, since that is what distinguishes a producer from writing
blobs by hand: a failed validation, a validator that throws, a duplicate key, a record count that
moved too far, a vetoing listener, and — through a stager that re-emits an earlier cycle's snapshot —
a cycle whose blobs do not agree with each other. In each case the assertion is that the version was
never announced and the producer can still publish the next one against the version consumers are
actually on. `ChecksumTests` pins what the checksum distinguishes, which is the question that decides
whether the integrity check is worth anything: the same records at different ordinals, a set's bucket
layout, and both halves of a decimal all have to change it.

`ReshardingTests` states resharding as an equivalence, because that is the only claim worth making:
rearranging a state read at one shard count has to land on exactly what the producer would have
written at the other — same ordinals, same values, same hash bucket positions — since a consumer that
reshards then applies a delta is about to be compared, record for record, against a producer that
never had the old count at all. That is asserted for splits and joins across all four record kinds,
and then end to end: a consumer follows a producer that splits, one that joins, and a reverse delta
back across a reshard. `ProducerTests` adds the case that matters most, a full producer cycle that
reshards with its own integrity check still passing.

`IncrementalProducerTests` makes the same kind of claim: an incremental cycle and a full one
describing the same data publish the same thing. What is not obvious there is deletion, so several
tests are about which sub-records go with a deleted record and which stay — removing the last
reference to a string drops it, removing one of two does not. `TransitiveSetTraverserTests` covers the
traversal underneath on its own, over object fields, list elements, set elements and map entries.

`PrefixIndexTests` and `SparseIntegerSetTests` cover the two later indexes, including what each does
when a delta moves the data underneath it — which is where both went wrong first, and where the two
delta-ordering differences noted above came from.

`GenericRecordTests` exercises the typed runtime without any generated code, which is the only way to
test it before a generator exists: every reference is followed by name and comes back as another
generic record, so the wrappers, the delegates and the missing-data fallback are all on the path.
`TypedApiTests` covers the other direction with a hand-written API of exactly the shape a generator
would emit — a `MovieApi`, a `MovieTypeApi`, a delegate, a record wrapper and a factory — since the
question that matters for the generator is whether that shape works, not whether a string was
emitted correctly. It includes a client whose model has a field the dataset does not, which is the
case the missing-data handler exists for.

`SpanReadTests` asserts that an allocation-free read returns exactly what the allocating one does, for
both strings and bytes, and pins the cases where the cheap path is not available: a string is never a
view, a byte field straddling a segment boundary is copied rather than viewed, and a value inside one
segment is a view into storage. The multi-segment sequence is read back through a `SequenceReader` on
the theory that if it composes with the BCL's own reader it is a real `ReadOnlySequence`.

`TypedIndexTests` covers the typed façades over the two value indexes, against a consumer with a
generated-shape API. Most of what is worth testing there is what the binding refuses — a key type
whose member cannot match the field it names, a select path that resolves to a value rather than a
record, a key that is not the primary key it claims to be — since the point of the layer is to catch
those when the index is built rather than at a query that silently never matches.

`CodeGeneratorTests` compiles the generator's output in process with Roslyn, refuses even a warning,
and then reads real records through the loaded assembly. The assertion that matters most is that the
generated client agrees with the untyped generic layer on every record of every type, which is what
would catch a field position resolved wrongly. The emitted text itself is not pinned: it is an
implementation detail, and a test over it turns every improvement into a test change.

`FilesystemProducerTests` is the only test where a real producer and a real consumer share a real
directory. Everything else uses in-memory stand-ins on one side or the other, so this is what would
catch a producer and a consumer that each work but do not agree on a file name, a staging step or the
announcement format.

### Ported, not yet covered by a round-trip

`HollowObjectSchema.FilterSchema` and the read path honour a filter, but only the explicit include
sets of `TypeFilter` exist; the recursive rule DSL is still absent.

### Not ported

- **Object longevity**, which serves reads of an older version from a live state. This is why Java has
  both `HollowPrimaryKeyIndex` and `HollowUniqueKeyIndex`; see the note below on what separates them
  here. It is also why `HollowConsumer` has no `ObjectLongevityConfig` or stale-reference detector:
  both exist to serve the proxy data access that is not ported.
- **Historical state creation**, which a consumer uses to serve queries against prior states.
- **Shared-memory mode.** `MemoryMode.SharedMemoryLazy` and the `BlobByteBuffer`, `EncodedByteBuffer`
  and `EncodedLongBuffer` types behind it. Constructing a read state engine with it throws.
- **Optional blob parts**, which split a snapshot across several streams.
- **Producer metrics** (`api.producer.metrics`), asynchronous snapshot publishing, and the blob
  storage cleaner.
- **The consumer's optional layers.** Object longevity, metrics collection
  (`api.consumer.metrics`) and `api.consumer.data`.
- **The deprecated `api.client.HollowClient`**, superseded by `HollowConsumer`; only the parts of
  `api.client` that `HollowConsumer` uses are ported.
- **Tools** (`tools`: diff, history, combine, split, patch. `tools.checksum` is ported because the
  producer's integrity check needs it, `tools.traverse` because the incremental producer does, and
  `tools.query`, `tools.stringifier` and `tools.util` because the explorer does),
  **`api.codegen`'s three extras** (the POJO, "performance API" and test-data builder generators; the
  client API generator itself is ported — see [The code generator](#the-code-generator)),
  **sampling** (`api.sampling`, deliberately — see below), and every module outside
  `hollow` except the explorer — `hollow-diff-ui`, `hollow-jsonadapter`, `hollow-protoadapter`,
  `hollow-zenoadapter`, `hollow-test`, `hollow-fakedata`. `hollow-explorer-ui` is ported as
  `Hollow.Explorer`; see [The explorer](#the-explorer). Of `hollow-ui-tools`, only `HollowDiffUtil`'s
  `formatBytes` and `HtmlEscapingWriter` had anything to port — the rest is Jetty and servlet plumbing
  that ASP.NET Core replaces outright.
- **`GarbageCollectorAwareRecycler`**, which picks a pooling strategy by inspecting the JVM's
  collector through `ManagementFactory`. The decision it encodes does not transfer to .NET; pick
  `RecyclingRecycler` or `WastefulRecycler` explicitly.

### Notes for whoever picks this up next

Before writing any code that turns a number into text or back, read
[Culture-invariant formatting and parsing](#culture-invariant-formatting-and-parsing). The rule is
absolute and the two traps in it are not obvious.

The one thing in this port that is not a faithful reproduction of Netflix Hollow is the `Decimal`
field type. Its compatibility rule — a dataset that uses no decimal field serialises exactly as
Netflix Hollow would serialise it — is enforced by `FormatCompatibilityTests` and has to survive every
subsequent change. If you are touching the write path, the read path, the delta applicators or the
field filter, read [Format extension: the `Decimal` field
type](#format-extension-the-decimal-field-type) first; the trap is that a decimal field is 128 bits
wide and every other fixed-length field is 64 or fewer.

### Suggested order for the remaining work

The loop is closed end to end: a producer publishes, a consumer follows, a restarted producer stays on
the chain, and a client generated at compile time reads it with types. What is left is either an
optimisation or a feature on top.

Nothing is outstanding from the original list. What remains unported is listed above, and each item
there is a feature on top rather than a gap in the loop: object longevity and the history it serves,
shared-memory mode, optional blob parts, producer metrics, and the diff tools and the UI over them.

The explorer is the first thing in this port that is read rather than called, and the next module
along — `hollow-diff-ui` — shares its shape: the same navigation bar, the same Velocity templates,
the same session-scoped place-in-the-data. Whoever takes it on should read
[The explorer](#the-explorer) first; the decisions there about Razor, escaping and session state were
made once and should not be made differently twice. The diff engine itself (`tools.diff`) is the
larger half of that job and is not started.
