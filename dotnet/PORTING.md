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

A dataset does not have to be on the heap at all: shared-memory mode maps the blob file and reads
records out of the mapping, so a large dataset costs the garbage collector nothing and two processes on
one machine share the pages. See [Shared-memory mode](#shared-memory-mode).

The status section says exactly what is and is not ported.

## Building and testing

```
dotnet build
dotnet test
```

The benchmarks are a project of their own and are not part of either — see
[The benchmarks](#the-benchmarks) for how to run them, and measure a Release build when you do.

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

## Schemas as text

A dataset's schemas normally arrive inside the blob, and `HollowSchema.ToString()` writes them out in
the form a data model is checked into a repository as:

```
Movie @PrimaryKey(id, country.code) {
    int id;
    string title;
    Country country;
}

ListOfActor List<Actor>;
SetOfActor Set<Actor> @HashKey(name);
MapOfStringToActor Map<String,Actor>;
```

`HollowSchemaParser` reads that form back — `Parse` for one schema, `ParseCollection` for a file of
them, over a string or a `TextReader`. Java's names are `parseSchema` and `parseCollectionOfSchemas`.

Three things are worth knowing about the grammar.

**An unrecognised field type is a reference.** `Country country;` declares a reference to the type
`Country`, and so does `strnig title;` — a misspelling of `string` becomes a reference to a type
called `strnig` rather than an error. That is Java's behaviour, and the format gives no way to tell
the two apart.

**`decimal` is a field type here.** Java has eight built-in names; this port has nine, because of
[the `Decimal` field type](#format-extension-the-decimal-field-type). It has to be in the parser's
table, or a schema this port wrote would read back with its decimal fields turned into references to
a type called `decimal`.

**A failure is a `FormatException`**, where Java throws `IOException`. Nothing here does any I/O
beyond reading the text it was handed, and the message says what it expected and what it found.

Java tokenises with `java.io.StreamTokenizer`, which .NET has no equivalent of. The tokenizer here is
written for this grammar rather than being a general one: a word is a letter or an underscore
followed by letters, digits, underscores and dots — the dots are what make `country.code` a single
token rather than three — plus the punctuation the grammar uses, with both comment forms skipped.

The tests keep Java's schema text verbatim and add the two schema files the Java repository holds,
`schema1.txt` and `hollow_code_gen_test.schema`, copied into the test project as embedded resources.
Each of them also has to survive being written back out and read again.

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

## What a transition changed

A consumer that follows a delta chain usually wants more than the new data: it wants to know what
moved. `HollowDataAccessor<T>` answers that as records —

```csharp
MovieDataAccessor movies = new(consumer);

foreach (Movie arrived in movies.AddedRecords) { ... }
foreach (Movie gone in movies.RemovedRecords) { ... }

foreach (UpdatedRecord<Movie> change in movies.UpdatedRecords)
{
    Console.WriteLine($"{change.Before.Title} is now {change.After.Title}");
}
```

— and the code generator emits one of these per type that declares a primary key, so the usual case is
to construct it rather than to write it. Turn it off with `GenerateDataAccessors = false`.

Java calls the base class `AbstractHollowDataAccessor`; .NET does not spell the abstractness into a
name. Four things differ beyond that.

**The matching is not in the base class.** Telling a replacement from an addition and a removal is
`RecordChangeSet`'s work, which this port already had and which stands on its own. Java does it inside
`AbstractHollowDataAccessor`, so a caller who only wants the counts — a validator, say — has to
materialise a record per change to get them. Here the accessor is a thin typed view over the change
set, and the change set is usable without it.

**A record is read when it is asked for.** Nothing is materialised until something enumerates, so a
transition of a million records costs a bit set. Java's `HollowRecordCollection`, which exists only to
do this, has no counterpart here: the collection is private to the accessor.

**An accessor is emitted only for a keyed type.** Java emits one for every object type, including the
ones with no primary key — where it would throw the moment it was asked anything, because there is no
way to pair a removal with an addition.

**There are seven scalar accessors, not six.** `StringDataAccessor`, `IntegerDataAccessor` and the
rest read the shared types every dataset has; `DecimalDataAccessor` reads this port's own. They
collapse the way the scalar type APIs already did — one generic base, and the named classes are a type
name and one read.

One thing to know about `RemovedRecords`: the ordinals in it are no longer populated, and reading one
works because the storage behind it has not been reused yet. Read them before the next transition, or
hold the records through it with [object longevity](#object-longevity).

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

Java ships three independent extras beside the client API generator. Two are ported —
`HollowPerfApiGenerator`, which emits against [the performance API](#the-performance-api), and
`HollowTestDataGenerator`, which emits against `api.testdata`. Both return their sources rather than
writing them, as the client generator does, so `HollowCodeGenerator.WriteTo` can leave unchanged files
alone.

The third, the POJO generator, is not ported. It emits plain classes that copy a record's fields out of
the dataset, which is the one thing a Hollow client exists to avoid; nothing else depends on it.


## The samples

### The console sample

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


### The explorer sample

`samples/Hollow.Explorer.Sample` is the other one: a minimal ASP.NET Core application with the explorer
mounted inside it.

```
dotnet run --project samples/Hollow.Explorer.Sample   # then http://127.0.0.1:7101
```

Four lines of it are the explorer; the rest is the application it is being put into — a producer, a
consumer, a page of its own at `/`, and a button that publishes the next cycle so the explorer can be
watched following a delta. It deliberately has no `[HollowGeneratedApi]` and does not reference the
source generator, because the explorer reads schemas rather than classes and it is worth showing that
it needs no generated client.

The one thing it exists to demonstrate that nothing else does is cache invalidation. The explorer works
each type's heap and hole figures out once per state, keyed on the randomized tag — which a delta does
not change — so a consumer that moves by delta has to say so. The sample does it with a refresh
listener; an embedder who forgets gets figures that are right after a snapshot and stale ever after.

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
| `hollow-fakedata/src/main/java/hollow/...` | `dotnet/samples/Hollow.FakeData/` |
| `hollow-perf/src/jmh/java/com/netflix/hollow/...` | `dotnet/benchmarks/Hollow.Benchmarks/` |
| package `com.netflix.hollow.core.memory.encoding` | namespace `Hollow.Core.Memory.Encoding` |
| package `com.netflix.hollow.api.error` | namespace `Hollow.Api.Error` |
| package `com.netflix.hollow.api.consumer` | namespace `Hollow.Api.Consumer` |
| package `com.netflix.hollow.api.client` | namespace `Hollow.Api.Client` |
| package `com.netflix.hollow.api.producer` | namespace `Hollow.Api.Producer` |
| package `com.netflix.hollow.tools.checksum` | namespace `Hollow.Core.Tools.Checksum` |

Java packages map to .NET namespaces one for one with the `com.netflix` prefix dropped and each
segment PascalCased. Java's one-public-type-per-file rule is not followed where a type is trivially
small and only meaningful alongside its neighbour (for example `OrdinalEnumerables.cs` holds the two
entry structs alongside the class that yields them).

## Naming changes

Where a Java name could not be carried over, the reason is recorded in a `<remarks>` block on the .NET
type. The systematic changes are:

- **Interfaces take an `I` prefix.** `ByteData` → `IByteData`, `FixedLengthData` → `IFixedLengthData`,
  `VariableLengthData` → `IVariableLengthData`, `ArraySegmentRecycler` → `IArraySegmentRecycler`,
  `HollowDataset` → `IHollowDataset`, `TypeFilter` → `ITypeFilter`,
  `HollowTypeStateListener` → `IHollowTypeStateListener`, and the `Hollow*TypeDataAccess` family.
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

### Java's cursors are sequences here

Java walks the elements of a collection record with a cursor. `HollowOrdinalIterator` hands back the
next ordinal and returns `NO_MORE_ORDINALS` once there are none left; `HollowMapEntryOrdinalIterator`
pairs a `next()` returning `false` with `getKey()` and `getValue()`. Neither is a
`java.util.Iterator`, so neither works with `for-each` or the stream library.

This port drops the interfaces and hands back `IEnumerable<int>` — or `IEnumerable<HollowMapEntry>`
for a map — so a walk is a `foreach` and LINQ applies to it:

| Java | Here |
| --- | --- |
| `HollowCollectionTypeDataAccess.ordinalIterator(ordinal)` | `ElementOrdinals(ordinal)` |
| `HollowSetTypeDataAccess.potentialMatchOrdinalIterator(ordinal, hash)` | `PotentialMatchElementOrdinals(ordinal, hash)` |
| `HollowMapTypeDataAccess.ordinalIterator(ordinal)` | `Entries(ordinal)` |
| `HollowMapTypeDataAccess.potentialMatchOrdinalIterator(ordinal, hash)` | `PotentialMatchEntries(ordinal, hash)` |
| `HollowHashIndexResult.iterator()` | the result *is* an `IEnumerable<int>` |
| `HollowPrefixIndex.findKeysWithPrefix(prefix)` returning an iterator | returns `IEnumerable<int>` |
| `MultiLinkedElementArray.iterator(index)` | `Elements(index)` |
| `RemovedOrdinalIterator`, with `next()`, `reset()` and `countTotal()` | `RemovedOrdinals`, an `IEnumerable<int>` |
| `IntMap`'s entry iterator | `IntMap.Entries()`, a sequence of `(Key, Value)` tuples |

Three consequences are worth naming.

**They are lazy.** Java reads the record's size when the cursor is built; here it is read when
enumeration begins. Nothing in the library depends on the difference, but a sequence held across a
delta transition is a description of a walk, not the result of one.

**Enumerating again walks again.** That is what let `RemovedOrdinalIterator.reset()` go: the four
delta historical state creators each make two passes over the removals — one to size the storage, one
to copy into it — and with a cursor the second pass needed an explicit rewind. `countTotal()` is
`Count()`.

**A set or map walk still carries its bucket.** The record copiers need the bucket an element came
from when `PreserveHashPositions` is on, which a bare sequence of ordinals would lose.
`HollowMapEntry` carries `Bucket` alongside `KeyOrdinal` and `ValueOrdinal`, and
`OrdinalEnumerables.SetElementsWithBuckets` yields `HollowSetElement`, which pairs an ordinal with its
bucket. `ElementOrdinals` and `Entries` do not expose it, because nothing else wants it.

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

### Naming what a collection holds

`[HollowCollectionTypeName("MovieId")]` on a list or set member, and
`[HollowMapTypeName(KeyTypeName = "SubTypeKey", ValueTypeName = "SubTypeValue")]` on a map member,
name the Hollow type the elements, keys or values are stored as. Without them a `List<int>` puts its
elements in the dataset's shared `Integer` type alongside every other loose integer; a type of its own
means a smaller ordinal pool and fewer bits per reference. Adding either to an existing member changes
the schema, so a producer and its consumers have to move together.

Java's two annotations take their names as optional elements, both defaulting to the empty string.
Here the collection one takes its single name as a constructor argument, and the map one has init-only
properties so that either half may be given alone.

**The collection's own name is unaffected**, in this port as in Java: a `List<int>` whose elements are
named `MovieId` is still a `ListOfInteger`, holding `MovieId` records. `[HollowTypeName]` is what
renames the collection, and composing the two is what Java's documentation shows.

**One refusal Java does not make.** That naming rule makes it easy to end up with two `ListOfInteger`
schemas over different element types — one member annotated, one not. Java keys its mappers by type
name alone, so the second member silently gets the first's mapper and its annotation does nothing at
all. Here two declarations of one type name have to agree on the schema, and a disagreement is a
`HollowMappingException` naming both.

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
object longevity is on, and the same holds here now that longevity is ported — see
[Object longevity](#object-longevity). The unique key index works against any `IHollowDataAccess`, so
it can be built over a proxy; the primary key index walks the schema's type states and cannot. Both are
ported because the distinction is real and a caller may want either; they share their hash table
through `UniqueKeyHashTable`, where Java duplicates it.

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
potential-match walks probe by ordinal, so on a keyed collection they no longer find anything
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

Two things around the edges of Java's consumer are absent, because what they exist to serve is not
ported: the `HollowAPI` parameter Java passes to a refresh listener alongside the read state engine,
and metrics collection.

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

## The diff UI

`Hollow.Explorer.Diff` is the port of `hollow-diff-ui`: the machinery for laying two records out side
by side, and four pages over a calculated `HollowDiff` — every type and how far apart the two states
are in it, one type's fields and record pairs, one field's pairs, and two records drawn against each
other.

It shares an assembly with the explorer rather than having one of its own. The two are the same kind
of thing — a few controller actions and some Razor views over a dataset — and they share their MVC
plumbing, their session-store base and `ByteSize`. Java splits them because each needs its own Jetty
server and Velocity engine, neither of which survives the port.

### Three layers, ported in order

The engine (`Hollow.Core.Tools.Diff`) answers *what moved*; the effigy and pairer layer answers *how
to draw one pair*; the pages are what is left.

- **The equality mapping** is what makes a diff over a large dataset finish. Built leaf-first, it
  gives every group of byte-identical records one identity, so a subtree the two states share is
  never walked at all. The port adds a guard Java lacks: a type's map is recorded as empty *before*
  it is built, so a self-referencing type asks for the empty map rather than recursing until the
  stack runs out.
- **The matcher** pairs records by primary key, because an ordinal means nothing across two states.
- **The counting tree** mirrors the data model, pairs off equal ordinals at each branch and passes
  only the remainder down; leaves score by hashing values with multiplicity.

Java runs the first and third of those on a `SimultaneousExecutor`. The port runs them on one thread,
as it does everywhere else, which also makes the scores deterministic.

### The row tree, and why it is lazy

A record is turned into a `HollowEffigy` — an ordinary object tree that compares by value rather than
by ordinal, and reads its fields when asked. Two effigies are aligned into rows by a pairer: objects
pair by field name, collections by a match hint (a `PrimaryKey` per element type) or, failing that,
by minimum difference over an every-against-every matrix of packed longs.

Only the root's immediate children are built when the page loads. Everything below is built when a
row is opened, which is what makes the page load at all on a record reaching thousands of others —
and a subtree the equality mapping calls identical is never built.

The view then decides what to show: what differs, and the branches leading down to it, capped at 300
rows before it stops opening branches on the reader's behalf.

### The rows are values, not a string that is parsed back

Java's `DiffViewOutputGenerator` writes each row as eight pipe-delimited fields, and
`HollowDiffHtmlKickstarter` tokenises that string back apart to build the initial HTML. The port
works a row out once as a `DiffViewRowDisplay` and produces the delimited form only for the browser,
which is the one place it is needed — the page's script splices new rows in itself, so sending it
markup would mean sending the same thing twice.

`HollowDiffHtmlKickstarter` therefore has no counterpart: the initial rows are a `@foreach` in
`ObjectDiff.cshtml`, which is what Razor is for.

### It closes an XSS hole

Java's `getFieldValue` replaces the pipe delimiter — there is no escape in that wire format — but
never HTML-escapes. A record holding markup puts that markup straight into the page, in both the
diff UI and the history UI that shares the code. The port escapes first and then replaces, over text
that can no longer be markup.

The cells still reach the page as markup, because they are drawn with box-drawing entities; what
changed is that the *data* in them is escaped before it gets there.

### The stylesheet and the margin images ride along in the assembly

`diffview.css` and the three expand/collapse images are `EmbeddedResource`s served by a controller
action, rather than sitting in a `wwwroot`. An application embedding the diff gets a working page
without having to serve static files or copy anything into its own content root. The action indexes a
fixed set of four names, so nothing a caller writes reaches the manifest as text.

### Two places where the port does not reproduce a bug

- `HollowEffigyCollectionPairer`'s match-hint probe does not stop once it has paired an element, so
  one element of the earlier collection can appear on several rows against several of the later one.
  The port stops. There is a test for it.
- The same pairer is missing an early return for an empty collection, which reports its elements
  twice. There is a test for that too.

### The diff sample

`samples/Hollow.DiffUI.Sample` publishes two versions of a film catalogue, compares them, and mounts
the diff at `/diff`. Its `README.md` covers the two things worth getting right — a key per type so
records can be paired at all, and a match hint per element type so collections can be — and the one
thing that is easy to get wrong, which is calculating the diff at startup rather than behind the first
page load.

## The history UI

`Hollow.Explorer.History` is the port of `com.netflix.hollow.history.ui`, over
`Hollow.Core.Tools.History` — the port of `com.netflix.hollow.tools.history`. Six pages over a
`HollowHistory`: every version it holds, one version, one type in that version, one group of that
type's changed records, one record laid out across the transition that changed it, and a search that
finds every version a key ever moved in.

It shares an assembly with the explorer and the diff for the same reason they share one with each
other, and its record page *is* the diff's — the same effigy, pairer, row tree and renderer, over two
states of one delta chain rather than two unrelated ones.

### What a history actually holds

A historical state holds only the records the *next* transition removed. Everything else is answered
by walking forward along a chain of states to whichever later state still has it, ending at the live
read state. A record is stored once, in the state that last had it, however many versions it survived
— which is why a long run of versions fits in memory at all.

`HollowHistory` is built on a read state as that state moves, and is told after each transition which
version it moved to. `DeltaOccurred` is where the copying happens, because a moment later the space
the removed records occupied is the read state's to reuse.

### Key ordinals are what make it a history

An ordinal means nothing across two states. The key index assigns every *distinct key ever seen* a
permanent ordinal of its own, and each state's changes are recorded against those. That is what lets a
page ask "what happened to this record" rather than only "what changed at this ordinal", and what lets
a search for a key return the versions it moved in.

### Four differences from Java worth naming

- **The key index gave one key two ordinals.** The ordinal mapper refuses to call two records equal
  when the second was interned in the same cycle as the first, because a value written this cycle is
  not readable yet. Java's first update runs both the before and the now ordinals through in one
  cycle, so a record that changed in that very transition is seen twice and given two key ordinals for
  the one key — and the pages then show it twice. This port closes the cycle between the two passes,
  so the second pass recognises the key. `HollowHistoryTests` covers it.
- **The intern pool truncated non-ASCII keys.** `ObjectInternPool` writes a string's *character* count
  and then its UTF-8 *bytes*, then reads those bytes from one past the length — which assumes the
  length varint is a single byte. So any non-ASCII key read back short, and any key of 128 bytes or
  more read back corrupt. The port writes the byte count and reads from wherever the varint actually
  ended.
- **Grouping by a field the key does not have.** Java resolves it to index -1 and then reads field -1.
  The port drops it: it is a caller's mistake, not a crash.
- **The expand links on the state-type page.** Java writes each group's link as
  `javascript:expandGroup('…')`, interpolating a key field's *value* into executable text. The port
  carries the name as a data attribute and reads it in a listener, so a value can be whatever it
  likes.

### The timestamp converter loses its statics

Java's `VersionTimestampConverter` hardcodes a Pacific time zone constant and carries a process-wide
mutable millisecond offset that anything can set. Neither survives: the zone is a constructor argument
on `HollowHistoryUI`, defaulting to UTC, and there is no offset. A version that does not read as a
timestamp is shown unchanged, as in Java.

.NET has no table of time zone abbreviations, so where Java prints `PST` the port prints the offset —
`UTC-07:00` — which says the same thing without a table.

### The templates lose their repetition

`history-state-type.vm` repeats the same forty lines three times over, once each for modified, added
and removed; `history-query.vm` does the same per type. Both become a loop over the three, and the
record grid and the subgroup list become partials shared by the type page, the expanded group and the
search results — nine copies in Java, one here.

Java's `history-state-enhanced-ui.vm` is a second, unfinished take on the state page, reachable only
by editing `HistoryStatePage` and swapping which template it names. It is not ported.

`HistoryStatePage.sendJson` and `HistoryStateTypePage.sendJson` are not ported either. They exist for a
Netflix-internal front end, hand-build `Map<String, List<List<String>>>` shapes with positional
meaning, and have no counterpart here.

### The history sample

`samples/Hollow.HistoryUI.Sample` publishes a film catalogue four times, follows it through the three
deltas between them, and mounts the history at `/history`. Its `README.md` covers what a history holds
and why `DeltaOccurred` has to be called after the delta rather than before.

It is also arranged to make one modelling consequence visible rather than described: `Film` is keyed
by `("Id", "Studio.Country")`, so correcting a studio's country changes the *key* of every film that
references it, and the overview counts two removals and two additions rather than two modifications.
That is what a primary key means, and it is worth seeing once.

### The fake data generator

`samples/Hollow.FakeData` is the port of `hollow-fakedata`. It is not a sample of how to use Hollow —
the four above are that. It is a generator: a fake book catalogue published over a long delta chain,
with the explorer and the history UI served over it while it runs, for when what those need is a
dataset big enough and churny enough to be worth looking at.

The model is the point of it. A book references an id, a country, its images and its metadata; the
images are a map of size name to a list of art; the metadata holds an enum and a list of chapters; a
chapter holds a byte array and a list of scenes; a scene holds a set of character names. Object, list,
set and map records, an inline scalar and referenced ones, an enum and a byte array — so every page
the explorer and the history have is reachable from it. Three types declare a primary key, which is
what lets the history follow a record from one version to the next.

Four differences from Java, all covered in the sample's `README.md`:

- **One seeded random.** Java calls `new Random()` at each use site and draws its words from
  javafaker, so no two runs produce the same catalogue — which makes whatever you find at cycle 60
  impossible to go back to. Here every draw comes from one `Random` built from `--seed`, the words are
  a few lists, and the timestamps come from a fixed clock. Same seed, same dataset, down to the
  ordinals.
- **Java's constants are command-line options**, rather than something to recompile.
- **Three Java bugs are not reproduced.** `populateCatalog(start, count)` loops
  `for (id = start; id < count; id++)`, treating the count as an end, so no book is ever added after
  the first cycle. `modifyBook` calls `Map.remove` with a `Map.Entry` rather than a key, so a book's
  art only ever grows. And the artists go into a `HashSet` of a class that declares no equality, so
  the deduplication it is reaching for never happens — which matters, because `Artist` is keyed on its
  name.

### The benchmarks

`benchmarks/Hollow.Benchmarks` is the port of `hollow-perf`: eleven suites over the parts of Hollow
that sit on a hot path — the hash functions, the ordinal map, the bit string, both indexes, reading a
long and reading a string, checksumming a collection type, the snapshot round trip, the duplicate-key
validator, and reading while delta transitions replace the storage underneath.

Java runs these under JMH. BenchmarkDotNet is the .NET equivalent and is deliberately not used: it
cannot be restored on every machine this port gets built on, and a benchmark project that will not
build is worth less than a rough one that will. `Harness.cs` does the three things that matter — a
calibrated operation count so the stopwatch does not become the measurement, discarded warmup
iterations, and JMH's 99.9% error bar — and the `README.md` says plainly what it leaves out, which is
everything else. The numbers are ratios between cases in one run, not absolutes to quote.

Two things did not port, for the same kind of reason each time: the thing being measured is not in
this port.

- **`FixedLengthElementArrayPlainPut` and `SegmentedLongArrayPlainPut`**, the two classes in
  `hollow-perf/src/main`, and the `writePlain` case that uses them. They are copies of core classes
  with `Unsafe.putOrderedLong` swapped for `Unsafe.putLong`, to price the release store in
  `setElementValue`. There is no release store here to price: `SegmentedLongArray.Set` is an ordinary
  array store, and publication is ordered once by the fence before the ordinal is written rather than
  per word — see [Concurrency primitives](#concurrency-primitives). Both classes and the comparison
  fall away together.
- **`deltaLaggedIndex`**, the third case of the duplicate-detection benchmark, which prices
  `DuplicateDataDetectionValidator.findDuplicateKeysInDelta`. That method does not exist here; the
  validator has only the full-scan path, and the two snapshot cases it would be compared against are
  ported.

One thing the benchmarks needed that the library does not have: `core.util.StateEngineRoundTripper`,
which Java ships in the main artifact. This port keeps it out of the library, since it is only ever
used by tests, so the test project and the benchmark project each have their own copy of the same ten
lines.

Two suites measure something slightly different from Java's, and say so where they are defined.
`Read` and `ReadLarge` run the same code, because this port dropped Java's unaligned single-word read
path; keeping both cases is how a run that disagrees would show up. And the delta-transition suite
fences its read against the transition with a `ReaderWriterLockSlim`, which Java's does not: applying
a delta releases the storage it replaced the moment the new storage is published — here and in Java
alike — so a read part way through the old elements faults. That is what object longevity is for, and
it is not what the benchmark is pricing.

## Flat records

A flat record is one record plus everything it references, serialised standalone — a movie, its title,
its cast list and every actor in it, as one array of bytes that means something without the dataset it
came from. It is what a producer hands to a service that wants a single record now rather than the
whole feed.

```csharp
// Out of a read state engine, as bytes.
FlatRecordExtractor extractor = new(readEngine, new HollowDatasetSchemaIdentifierMapper(readEngine));
byte[] wire = extractor.Extract("Movie", ordinal).ToArray();

// Into a write state engine at the far end.
FlatRecord received = new(new ArrayByteData(wire), new HollowDatasetSchemaIdentifierMapper(writeEngine));
int arrived = new FlatRecordDumper(writeEngine).Dump(received);
```

A CLR object can skip the engines entirely:

```csharp
FlatRecord record = mapper.WriteFlat(movie, schemaIdMapper);
Movie again = mapper.ReadFlat<Movie>(record)!;
```

### The layout

All varints: `[top record location] [records length] [record]* [primary key field locations]*`. Each
record is a schema identifier followed by that schema's ordinary blob encoding, so the encoders are
the ones the blob writer already uses.

Two things about it are worth knowing before reading the code. **A reference field holds an index into
the flat record**, not a dataset ordinal — which is the whole point, and is why the extractor needs an
`IOrdinalRemapper` rather than copying records straight. **The top record is last**, because a record
may only reference one already written, so the writer appends and the reader starts at the end. The
trailing key field locations let a receiver key the record without a model class.

Set elements and map keys are gap-encoded — what is stored is the step from the previous one — while
map values are not. A collection's hashes are left out of what is written: a set's bucket layout is a
property of the dataset it came from, not of the record, so two sets of the same elements have to
flatten identically.

Records deduplicate as they are written, by content hash, exactly as they would share an ordinal in a
dataset.

### A schema identifier names the schema the record was written against

This is the one thing to get right, and it is easy to get backwards. The identifier says how the
*writer* laid the record out. It has to: the reader walks the bytes field by field, so if it resolved
the identifier to its own idea of the schema it would lose its place at the first field the two ends
disagree about and then read a length, a field type or a pointer out of the middle of a value. Every
symptom after that is a lie.

What the *destination* declares decides only what is kept. A field it does not have is dropped, which
is what lets a record written against a newer model land in an older consumer. A *reference* with
nothing to point at is refused instead: a silently null reference is a worse answer than a failure.

Because a disagreement here is otherwise undiagnosable, `FlatRecordDumper` wraps any failure in a dump
of the record as this end decoded it — the schema layout it read, where it stopped, and the bytes in
hex. Java has the same diagnostic and for the same reason.

### `IHollowSchemaIdentifierMapper` has an implementation here

Java ships none outside its own tests: numbering the schemas is left to the caller, and the caller is
expected to have a registry. This port adds `HollowDatasetSchemaIdentifierMapper`, which numbers a
dataset's schemas in declaration order.

The cost is worth stating plainly, because it decides what the class is good for: **the numbers move
when the model changes.** Add a type and everything after it renumbers. That makes it right for
process-to-process transfer where both ends run the same model, and wrong for storage — a record
written today and read after a model change would decode as something else. For storage, write a
mapper that assigns stable identifiers and keeps them.

### Reading one back is a conversion, not an assignment

`ParseFlatRecord` on the four type mappers is the inverse of `WriteFlat`. Java's version reflects the
value it read straight back onto the field and lets the JVM's widening rules make it fit. .NET will
not assign an `int` to a `short`, so `MappedValues` puts back the type information the write threw
away: a `short`, a `uint` and an `int` are all an int field, a `char[]` and a `string` are both a
string field, and an enum is its member name.

Construction prefers a constructor whose parameters all name mapped members, which is what makes a
`record` type or a primary constructor work; failing that the type is built empty and its members
assigned. An init-only property is settable through reflection — `init` is a rule the compiler
enforces, and reflection is not the compiler — but a get-only auto-property is not, and falls to the
constructor path. A member reachable by neither is an error rather than a silent null.

### Two defects in the write path that round-tripping found

Neither is reachable from Java, which has no unsigned types and no `char`-to-string mapping.

**A `uint` or `ulong` overflowed the conversion to the signed field it is stored in.** Converting *by
value* refused half of each range instead of storing it. They travel by their bits now, both ways. The
one value that cannot survive is the bit pattern the format spends on null — `2147483648` for a
`uint`, whose bits are `int.MinValue` — and that is refused with a message saying so rather than read
back as null.

**A lone `char` maps to a string field** per `ScalarFieldType`, but the write path only handled
`char[]` and `string` and cast anything else, so a `char` member threw.

### The stringifier

`FlatRecordStringifier` prints a record as indented text. A one-field object prints as its value
rather than as a record with a field — the wrapper types the object mapper generates around a string
or an int are an artefact of the model, and printing them as records buries the data three lines deep
in nothing. `ExcludeObjectTypes` leaves named types out wherever they appear.

Numbers are formatted invariantly, where Java's version uses the default locale. A dump whose meaning
changes with the machine that produced it is no use for comparing two of them.

## Combining, splitting and patching

`Hollow.Core.Tools.Combine`, `.Split` and `.Patch` are the ports of `tools.combine`, `tools.split` and
`tools.patch` — the dataset-reshaping tools. They share one problem, and it is worth stating once
because all of them are shaped by it.

A record's ordinal is its identity *within one state and no other*. Two states produced independently
number their records differently, so the moment records from one state are written into another, every
reference in every copied record is wrong. Each of these tools therefore carries a table per type — an
`int[]` from the input's ordinals to the output's, `-1` for "not copied yet" — behind an
`IOrdinalRemapper`. The record copiers consult it for each reference they write, and asking about an
ordinal nothing has copied yet *copies it*. That one line is what pulls a record's whole reference
closure across without anything having to walk it: copying a film asks for its studio's ordinal, which
copies the studio, which asks for its country's, and so on down.

Java runs all three on a `SimultaneousExecutor`. This port copies on one thread throughout, as it does
elsewhere — which removes a `ThreadLocal` of per-type copiers from the combiner and a lock from the
copy path, and costs nothing correctness-wise because none of the shared state was ever partitioned by
thread in the first place.

`HollowObjectHashCodeFinder` is not ported (see [Not ported](#not-ported)), so
`typesWithDefinedHashCodes` is always empty. Everything hanging off it is therefore dead here:
`preserveHashPositions` is `false` at every call site in all three tools, and the combiner's
hash-order-independent ordinal map — which existed so that two sets differing only in bucket order
would be written once — is left out entirely. Where Java asks the question, the port has a `static`
returning `false` with a comment saying why, rather than silently dropping the branch.

### The combiner

`HollowCombiner` copies one or more read states into a single write state. Without keys it is a plain
copy-everything-and-rewrite-the-references pass, and the write state's own byte-level deduplication
takes care of identical records arriving from two inputs.

Primary keys are what make it interesting. Given a key, the same record arriving from two inputs is
written **once** — from whichever input was passed first — and the second input's references are
pointed at the copy that was kept. That last part is `HollowCombinerPrimaryKeyOrdinalRemapper`: having
placed a record, it looks the same key up in every *other* input and pre-maps that input's ordinal to
the same output ordinal, so a later reference resolves to the copy already written instead of copying
a duplicate.

Keys are applied in dependency order, a round of copying per group of keys that do not depend on each
other, because deduplicating a type changes what the types referencing it look like. `C.Key` in a
compound key on `B` means *the surviving* `C` only if `C` was deduplicated in an earlier round — which
is exactly the difference between `ACompoundKeyReachingThroughAReferenceDeduplicatesOnTheWholeKey` and
`ACompoundKeyAndACascadingOneAreAppliedInDependencyOrder` in the tests.

`IHollowCombinerCopyDirector` decides which records are copied at all, with five implementations
carried over: include/exclude by ordinal, include/exclude by primary key, and the default that copies
everything. Note what "exclude" means: it stops a record being copied *directly*, but a record that
something else copied still references is pulled across anyway. Excluding a record therefore
**replaces** it with a later input's record of the same key rather than deleting it —
`AnExcludedRecordIsReplacedByTheNextInputsRecordWithTheSameKey` and
`AnExcludedRecordStillArrivesWhenAnotherInputHoldsIt` are the two halves of that.
`ExcludeReferencedObjects` grows the exclusion to the whole closure when deletion is what was actually
wanted.

One bug fixed in that method: Java iterates the excluded-ordinals map while `addTransitiveMatches`
adds to it. Here the state engines are materialised into a set first, with a comment saying why.

### The splitter

`HollowSplitter` is the combiner in reverse: one read state into several write states. A record copied
into a shard pulls everything it references in behind it, renumbered into that shard's ordinal space,
and a record several shards' roots reach is copied into **each** of them — a shard has to stand on its
own. Each shard gets its own copier and its own remapper for that reason; the tables cannot be shared,
because the same record has a different ordinal in each shard.

`IHollowSplitterCopyDirector` names the top-level types and says which shard each of their records
goes in. Everything else follows. `HollowSplitterOrdinalCopyDirector` divides by ordinal, which is even
but not reproducible — an ordinal means nothing across two states, so the same record can land
elsewhere next cycle. `HollowSplitterPrimaryKeyCopyDirector` divides by key, which is, and can also
name types to replicate into every shard.

Two departures from Java, both in the splitter:

- **The key hash is taken unsigned.** Java computes `hashKey(...) % numShards` on a signed `int`, so
  about half of all keys yield a negative number — which collides with the `-1` that means "put this
  in every shard". Half the dataset was replicated instead of placed. The test keys on a string
  rather than an int to show it: `HollowReadFieldUtils.IntHashCode(i) == i`, so a small id can never
  go negative, whereas a string hashes through MurmurHash3 and plenty do.
- **A top-level type the input does not have is refused.** Java logs a warning and carries on, which
  produces shards quietly missing a type the caller asked to split by.

### The patchers

Two different things share the `patch` name.

`HollowStateEngineRecordPatcher` (`Patch.Record`) replaces named records of one state with the same
records from another. There is no in-place edit of a Hollow state, so this is a combine with a
director that says which side each record comes from: everything *except* the matched closure from the
base, and *only* the matches from the patch source. The two traversals on the base side are what make
it a replacement rather than an addition — `AddTransitiveMatches` grows the matched set to everything
those records reference, and `RemoveReferencedOutsideClosure` takes back out whatever something
outside the set still references, because that is shared data rather than part of what is being
replaced.

Records are named by `TypeMatchSpec`, which takes *traversal* paths rather than a primary key, so a
spec can say "every film whose cast includes this actor" as readily as "the film with this id". The
step across a collection is the literal `element`, and a `string` property needs a trailing `value`
because the mapper maps it as a reference to the `String` type — `"Cast.element.Name.value"` is the
shape. A spec naming a type the state does not have matches nothing; Java reads its maximum ordinal
before checking whether it is there at all, and throws a null reference.

`HollowStateDeltaPatcher` (`Patch.Delta`) is unrelated, and the subtler of the two. A delta can only be
written between two states whose ordinals line up, and two states produced independently do not: the
same record may sit at different ordinals, and the same ordinal may hold different records. The patcher
builds an **intermediate** state that lines up with both, so a consumer can be walked from one to the
other in two transitions instead of a double snapshot.

The trick is the ordinals nothing can share. A record that differs between the two states at the same
ordinal is written at an ordinal past `max(from.MaxOrdinal, to.MaxOrdinal)` — somewhere neither state
uses — so the first transition removes it from its old ordinal and adds it at the new one, which frees
the old ordinal to take the later state's record on the second transition. Everything the two states
agree on never moves.

As with the record patcher, a type only one of the two states has is dropped rather than dereferenced;
Java reads the later state's schema before checking whether it has the type.

### A delta bug the patcher found

Building a delta patcher test against two states with *different* schemas for the same type turned up
a genuine bug in `HollowObjectTypeDataElements.ApplyDelta`, unrelated to the tools and present since
the read path was ported.

When a transition frees an ordinal — a record that became a ghost on the *previous* transition and is
now going away entirely — the port was still copying that record's data forward into the new elements.
That looks harmless, since the ordinal is free and nothing should read it. It is not. The producer
stops accounting for such a record when it sizes the new state's fields, so its value can need more
bits than the field now has, and `FixedLengthElementArray.SetElementValue` ORs into memory rather than
masking. The excess bits ran straight into the *next* record's field — which read back as null,
because the bits it spilled happened to fill that field to all-ones.

The symptom was a live record losing a field, several ordinals away from anything the patch touched.
Java skips these records (its `removalsReader`, threaded through `mergeOrdinal`), and the list, set and
map applicators in this port already did; only the object one did not. It now reads
`from.EncodedRemovals` the same way, treats a freed ordinal as an empty slot, and splits the bulk-copy
run at one so both paths agree.

## Object longevity

Hollow reuses its memory. A record read out of a consumer is a *handle* — a type and an ordinal — not
a copy. Once a delta lands, that ordinal may hold a different record, and the handle silently starts
reading it. That is the contract, and for most code it is fine: read what you need, let go, read again
next cycle.

It is not fine when a reference outlives a refresh. A cached object, a request that took longer than
the cycle, a background task holding a list — each ends up serving data that is not merely stale but
*wrong*, belonging to whatever record took the ordinal. Nothing fails; the numbers are just someone
else's. Object longevity is the feature that stops that, and `Hollow.Core.Read.DataAccess.Proxy`,
`.Disabled`, `IObjectLongevityConfig` and `StaleReferenceDetector` are its parts.

Turn it on through the consumer builder:

```csharp
using HollowConsumer consumer = new HollowConsumerBuilder()
    .WithBlobRetriever(blobRetriever)
    .WithObjectLongevityConfig(ObjectLongevityConfig.Enabled)
    .Build();
```

### How a reference is kept readable

The API a consumer hands out holds a data access, and every record read through that API reads through
it. With longevity off that is the read state engine itself, which the delta moves on underneath.

With longevity on, the consumer builds the API over a `HollowProxyDataAccess` instead — an
`IHollowDataAccess` that forwards every read to another one and can be pointed somewhere else
afterwards. Then on each delta:

1. A `HollowHistoricalStateDataAccess` is built holding exactly the records that transition removed.
   This is the same machinery the history UI uses — see [The history UI](#the-history-ui).
2. The **outgoing** proxy — the one every existing reference reads through — is pointed at it.
3. A **new** API is built over a fresh proxy onto the live state, for everything obtained from here on.

So both are true at once: a reference taken before the refresh keeps reading what it always read, and
one taken after reads the new data. The per-type proxies are reused rather than rebuilt, which is the
whole trick — a caller's records point at *those objects*, so repointing them moves the data out from
under a record without moving the record.

A historical state holds only its own transition's removals, so a reference may ask it for a record it
has not got: one that survived that transition and was removed later, or one that is still live. The
states are chained through `NextState`, and the read walks forward until it reaches a state that has
the record — ending, for a record nothing ever removed, at the live read state. The chain is held
weakly, because it exists only for the references still out there.

### What bounds the retention

Left alone this is unbounded: one leaked reference pins every historical state taken since. That is
what `StaleReferenceDetector` is for. Each superseded state is watched, and moves through two periods:

- The **grace period**, during which nothing is flagged or dropped. Long enough to cover whatever
  legitimately outlives a cycle.
- The **usage detection period**, during which the consumer watches whether the stale data is actually
  read. Reads still succeed throughout; what the window decides is whether the data may be dropped at
  the end of it.

Once both have passed with no read seen, and `DropDataAutomatically` is set, the proxy is pointed at
the *disabled* data accesses and the historical state is released. A read after that throws
`HollowDataAccessDisabledException`, whose message names the feature — so a stale reference becomes a
stack trace rather than a wrong answer. `ForceDropData` drops even when reads *were* seen, which is the
setting for finding the code that holds a reference too long rather than tolerating it.

### Three departures from Java

**Usage detection does not go through `api.sampling`.** Java asks the sampling framework whether a
stale reference has been read, via `hasSampleResults()`. Sampling is ported here — see
[Sampling](#sampling) — and longevity still does not use it, because the proxy is already on every
single read and a sampler nobody turned on would answer no.
`HollowProxyDataAccess.WasRead` is a flag set there and cleared when the detection window opens.
It is a plain field rather than an interlocked one on purpose: the write is on the read path of every
record and the only reader is a housekeeping timer, so the cost of the write matters and a lost write
does not — it delays a drop by one housekeeping interval.

**The housekeeping runs on a `TimeProvider` timer, not a daemon thread.** Which is also what makes the
hour-long periods testable: the tests inject a clock and make two hours pass without waiting.

**The detector watches the proxy as well as the API — and this fixes a real hole.** Java's
`StaleHollowReferenceDetector` holds a weak reference to the `HollowAPI` alone. But an application
holds *records*, and a record holds the **proxy**, not the API. The API is therefore routinely
collected while the data behind it is still perfectly reachable — at which point Java's `detach()`
dereferences a dead weak reference, does nothing, and the historical state is pinned for good. Exactly
the case the detector exists to prevent. The port's handle watches both and drops through the proxy;
the API is used only for `DetachCaches` and for identity. This was caught by a test that passed on its
own and failed in the full suite, where a collection had actually run.

Java's expired-usage stack trace recorder is not ported either. It exists to attribute a read of
dropped data to the code that made it, which in .NET is what the exception's own stack trace is.

## Shared-memory mode

By default a consumer reads a snapshot *onto the heap*: every record is copied out of the blob into
pooled arrays, and the blob is then closed and forgotten. Shared-memory mode does not copy. The blob
file is mapped into the address space and left there, and a record is read out of the mapping when it
is asked for.

That changes three things. The dataset no longer counts against the managed heap, so it no longer
counts against the garbage collector either — there is nothing to trace and nothing to compact. A
process restarts without re-reading anything, because the pages are already in the operating system's
cache. And two processes mapping the same file share those pages, which is where the mode gets its
name: a second consumer on the same machine costs almost nothing.

What it costs is that a read may fault. On-heap, the data is there; mapped, the first touch of a page
may go to disk. The mode suits a large dataset read unevenly, and suits a small one read hot rather
less.

Turn it on by giving the state engine the mode and the reader a mapped input:

```csharp
HollowReadStateEngine readEngine = new(MemoryMode.SharedMemoryLazy);

using HollowBlobInput input = HollowBlobInput.Mapped(snapshotPath);
new HollowBlobReader(readEngine).ReadSnapshot(input);
```

The two have to agree — a state engine in this mode builds data elements that read through a mapping,
and there is no mapping behind a serial input, so a mismatch is rejected rather than half-working.

Everything above the read is unchanged. The indexes, the generic records, the generated API, the
explorer and the diff all read through the same interfaces and cannot tell which storage they were
handed. `SharedMemoryModeTests` is built around exactly that: it reads one blob both ways and compares
the two states record by record with `HollowChecksum`.

### Two things the mode refuses

**A delta.** Applying one edits records in place, and a mapped file is read-only. A consumer in this
mode follows the chain by mapping each new snapshot instead — which is cheap, since mapping does not
read anything.

**A filter.** Filtering rewrites each record's layout as it is read, dropping the excluded fields, and
a mapped record is never rewritten. `MemoryMode.SupportsFiltering` is the test, and the blob reader
makes it before touching the input.

Both are refused with a `NotSupportedException` that says which mode and why, as Java does.

### The pieces

| Type | Java | What it is |
| --- | --- | --- |
| `MemoryMappedBlob` | `BlobByteBuffer` | The mapped file. Hands out bytes and 64-bit words by absolute offset. |
| `EncodedLongBuffer` | same | `IFixedLengthData` over the mapping: the bit-packed fixed-length fields. |
| `EncodedByteBuffer` | same | `IVariableLengthData` over the mapping: the string and bytes payloads. |
| `FixedLengthDataFactory` | same | Picks between `FixedLengthElementArray` and `EncodedLongBuffer`. |
| `VariableLengthDataFactory` | same | Picks between `SegmentedByteArray` and `EncodedByteBuffer`. |

The two factories are the whole of the wiring. Each of the four data elements carries the mode it was
built for and asks a factory for its storage; nothing else in the read path changed.

### Byte order, which is the part that bites

A bit string is written to a blob as a run of 64-bit words, each **big-endian**, because that is what
`java.io.DataOutput` writes. The bit string's own numbering runs the other way: bit `i` is bit `i % 64`
of word `i / 64`, counting from the least significant. So a word read out of the file has to be
byte-swapped before its bits mean anything.

Java does this a byte at a time, mapping logical byte `k` to file byte `(k & ~7) + 7 - (k & 7)`. In
.NET the whole aligned word can be read and reversed in one instruction:

```csharp
long stored = _view.ReadInt64(at);

return BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(stored) : stored;
```

Variable-length data gets **no** such treatment. It is a plain byte stream, and byte `n` of the stream
is byte `n` of the file — `EncodedByteBuffer.Get` is a straight read. Getting this backwards is the
easiest mistake in the mode and the hardest to spot, because short values still look plausible; the
tests cover it with a 5,000-character string, which does not.

A last wrinkle: the final element of a bit string is reached through a 64-bit window that may run off
the end of the file. The bits past the end are shifted away by the caller, so `GetByte` answers zero
for up to eight bytes past the end rather than failing.

### One simplification over Java

Java's `BlobByteBuffer` carries a **spine** of `MappedByteBuffer`s, one per gigabyte, because a Java
buffer is indexed by `int`. Every read picks a buffer, shifts and masks an offset into it, and the
whole arrangement tops out at two exabytes.

A .NET `MemoryMappedViewAccessor` takes a `long` offset. One view covers the whole file, and the spine,
its arithmetic and its ceiling all go away — `MemoryMappedBlob` is a view and a length.

The mapping outlives the `HollowBlobInput` that opened it: the data elements read through it for as
long as the state engine is alive. Disposing the input closes the stream and leaves the mapping be,
which is released when nothing refers to it any more.

## The performance API

A generated client API hands out record objects. That is the right trade almost always, and the wrong
one when a process walks millions of records in a tight loop: every hop allocates a wrapper whose only
content is an ordinal. The performance API is the same dataset without the wrappers — a type API per
type, and a reference that is nothing but a number.

### A reference is a struct, so the type system can hold the type name

`HollowRef` is a `readonly struct` over a single `long`, packing the type identifier above the ordinal.
Java cannot do that: a long is a long, so it ships a `Ref` class of static helpers that pack and unpack
those longs and relies on the caller not to hand a `Movie` reference to an `Actor` API. A struct carries
the same one word and still refuses that call at compile time.

That leaves Java's two names for one thing, so both moved:

- Java's `Ref` — the static packer — is this port's `HollowRef`, because here it is the reference
  itself rather than a bag of helpers for manipulating one.
- Java's `HollowRef` — the base class for a record wrapper that holds one — is `HollowRefObject`,
  since a reference and an object holding a reference should not share a name.

Java's ordinal iterators are `IEnumerable<int>` here, and a map's entry cursor is
`IEnumerable<HollowRefEntry>`. A cursor with `next()` and `getKey()`/`getValue()` on the side is how
Java avoids allocating per entry; `foreach` over a sequence of `readonly record struct` costs the same
and reads as a sequence.

### The cache keeps what the last transition removed, on purpose

`HollowPerfApiCache<T>` holds one wrapper per ordinal and swaps its backing array on each transition,
keeping the previous state's entry for an ordinal the new state does not populate. That looks like a
leak and is not: Java's comment says it plainly — *"This is required if removed ordinals are queried in
the cache"* — because code holding a reference across a transition would otherwise get nothing back for
a record that has just gone.

It survives one transition, not two. `PerfApiTests` pins both halves of that, which is the useful
thing to know: a record removed in the last transition is still readable, and one removed two
transitions ago is not.


## Compaction

A record keeps its ordinal for as long as it exists, so a dataset that churns leaves gaps. A type whose
highest ordinal is a hundred but which holds twenty records still costs every consumer the full hundred
slots, because the fixed-length storage is one flat run indexed by ordinal. `HollowCompactor` closes the
gaps by producing a delta consisting of nothing but removals and re-additions of identical records at
lower ordinals.

It takes several deltas to finish. Relocating one type's records changes the ordinals its referencing
types point at, which churns those in turn, so a single cycle only compacts types that do not reference
one another. `CompactionConfig.ApproximateDeltaBytesPerCycle` bounds it further: a budgeted cycle
compacts only the type whose holes cost the most, and only as many of its records as the budget affords
once the referencing closure is counted.

### A budget too small is an answer, not a log line

A budget that cannot afford one record together with everything referencing it will never compact that
type, however many cycles run. Java logs a warning and returns zero, which is a decision a producer
cannot act on without reading logs. Here it is `HollowCompactor.SkippedTypes`: the type name mapped to
what went wrong and what budget would fix it. Everything else about the cost model is Java's.

Two small departures beyond that:

- `PreserveHashPositions` is a constant `false`. Java asks the read engine which types have a defined
  hash code, which only ever answers yes when a `HollowObjectHashCodeFinder` has been installed, and
  that hook is [not ported](#not-ported). The question has one answer here, so it is written as one.
- A type with no ordinals at all is refused as a candidate outright. Java divides by zero, compares the
  resulting `NaN` against the threshold, and arrives at the right answer by accident.


## Metrics

Two listener layers sit on the producer's and the consumer's events and turn them into numbers:
`ProducerMetricsListener` for cycles and announcements, `RefreshMetricsListener` for refreshes. Both are
abstract, because where the numbers go is a deployment's business.

Java names them `AbstractProducerMetricsListener` and `AbstractRefreshMetricsListener`; the `abstract`
keyword says that here. Two things Java needs and this does not:

- **The builders.** Each metric is built through a nested `Builder`, which is what a class with a dozen
  fields, half of them optional, requires in Java. An init-only record needs none.
- **The reporting interfaces.** `ProducerMetricsReporting` and `RefreshMetricsReporting` exist so that a
  subclass can implement one reporting method and ignore the other. `protected virtual` and
  `protected abstract` say the same thing without a second type, so neither interface is ported.

### The unit is in the type

Java reports every instant as a `long` and leaves the reader to work out what it means. Two different
things are hiding in there, and they are not interchangeable:

- The last cycle success, the last announcement success and the refresh end are monotonic readings with
  no relation to the wall clock. They are `Stopwatch.GetTimestamp` readings here, named
  `...Timestamp`, and `Stopwatch.GetElapsedTime` turns one into an age.
- The producer's cycle start and the announcement time are wall-clock Unix milliseconds out of a blob
  header or announcement metadata. They are `DateTimeOffset` here, named `...Time`.

Durations are `TimeSpan` rather than a `long` of milliseconds, and an absent optional is `null` rather
than an `OptionalLong`.

### A metric never fails a refresh

A reporter that throws inside `RefreshMetricsListener` would otherwise fail the consumer's refresh over
a metric. Java catches it and logs at severe; this port takes no logging dependency, so it
raises `ReportingFailed` instead — the same pattern the producer's listener support already uses for a
listener that throws.

A header tag that is absent or unparseable leaves its metric absent rather than wrong, on both sides.

### When an announcement time means anything

`RefreshMetricsListener` records the announcement timestamp for a version only when the namespace was
unpinned both before and now. A pinned namespace holds a consumer at a version announced long ago, and
the refresh that unpins it moves off one; in either case the gap between announcement and load is the
pin, not the consumer's lag, and reporting it as lag would be worse than reporting nothing. The same
reasoning drops it when a refresh stops short of the version it asked for.


## Sampling

Which fields does this application actually read? A dataset accumulates fields nothing has asked for
in years, and every one costs bytes in every record and every consumer's heap. Sampling answers the
question from a running process: turn counting on, let it run, read off what was touched.

Counting every read would cost more than the reads. A *director* decides, read by read, whether this
one counts — always, never, or for a slice of each interval. `TimeSliceSamplingDirector` counts for one
millisecond in every second by default, which is enough to tell a field nothing reads from one read a
million times, and cheap enough to leave on.

### What it costs when it is off

A sampler hangs off every type read state, created disabled, and every accessor that reads a field's
value records into it. With counting off that is one perfectly-predicted branch per read —
`HollowObjectSampler.RecordFieldAccess` tests a `bool` field before it touches a director. That is the
price of being able to turn sampling on in a running process rather than redeploying to find out what
is read, and it is the same trade Java makes.

The sampler is reached through `IHollowTypeDataAccess`, defaulted there to `NullSampler.Instance`, so
only a data access reading out of a type state has to say anything. That covers the missing, disabled
and historical accesses at once. The longevity proxy overrides it to answer from whichever state it
currently points at, so sampling keeps working underneath object longevity.

### Two things Java needs that this does not

**The boxed field access sampler.** Java carries a second sampler per object type, counting the boxed
getters separately, because it emits `getYear()` and `getYearBoxed()` and only the second allocates an
`Integer`. This port emits one `int?` accessor, and `Nullable<T>` is a struct: there is no boxing to
count, so there is no second sampler and no generated code recording into one.

**A thread that sleeps.** Java's time-slice director toggles its flag on a daemon thread that sleeps
between flips. This uses a `TimeProvider` timer, like the rest of the port's housekeeping, which means
no thread parked doing nothing and a test that can make an hour pass without waiting. Java's toggler
also writes its flag and its listener list without synchronisation; here the flag is volatile and the
list guarded, because the timer callback and the caller of `StartSampling` really are different threads.

### Where the numbers come out

`HollowReadStateEngine.GetSampleResults` reports every type, hottest first, omitting the types that
counted nothing — a model of hundreds of types would otherwise bury the handful that were read.
`HollowApi.GetSampleResults` reports only the types the client's own model declares, which is the more
useful answer for an application. Setting a director through the API likewise reaches only those types,
so the two seams do not quietly enable each other; `ApiSamplingTests` pins both halves of that.

### One duplication collapsed

Java writes `HollowListSampler` and `HollowSetSampler` out twice, identically, and `HollowMapSampler` a
third time with one extra counter for bucket reads. The shared part is a `HollowCollectionSampler` base
here and the three names remain over it, so each read state still names the sampler it holds.

The null samplers — what a type state with no type holds — are marked by a flag rather than by an empty
type name. That is how Java marks them, and it is why its object null sampler, built from a schema
named `test`, fails the very test the other three rely on; harmless only because that schema has no
fields to count.


## Optional blob parts

Most of a dataset's bytes usually sit in a few of its types, and most consumers do not read them. An
optional part is how a producer splits those types out: they are written to their own artifact,
published beside the blob, and fetched only by the consumers that want them. A consumer that names no
parts reads the main blob alone, gets a working dataset without those types, and never pays for the
bytes.

A producer assigns types to named parts with `OptionalBlobPartConfig`. Anything unassigned goes into
the blob, and a type belongs to at most one part — Java allows a type in two parts, which writes its
records twice and leaves a consumer holding whichever part it read last.

### The tags are what make a part safe

A part repeats the main blob's origin and destination randomized tags in its own header, and the
reader refuses a part whose tags disagree with the blob it was handed alongside. Without that, a part
from another state reads as records at ordinals that mean something else entirely — not an error, just
wrong answers. A part given under a name its own header contradicts is refused for the same reason.

That is the one check worth knowing about. The file names are a convention; the tags are the contract.

### A filter has to see every schema

Each part declares the schemas of the types it carries, and the main header declares the rest. A type
filter is resolved against all of them together, or a type that lives in a part reads as one the filter
never heard of and is silently dropped.

### The names in the blob store

`snapshot_<part>-<toVersion>`, `delta_<part>-<fromVersion>-<toVersion>`, and the same for
`reversedelta`. Those are the names a Java blob store writes, and the port matches them exactly — the
producer's publisher and the consumer's retriever agree on nothing else. A part file that is not in the
store is left out rather than refused, so a store missing one behaves like a store that never had it.

### One departure in shape

Java adds an output per part one at a time and then checks, at the point of writing, that every
configured part got one. `OptionalBlobPartConfig.NewOutputs` takes a function and binds them all at
once, so a part left without an output is refused where the caller can still do something about it.


## Status

### Ported and tested

| Java package | Notes |
| --- | --- |
| `core.memory` | `IByteData`, `ArrayByteData`, `ByteDataArray`, `SegmentedByteArray`, `SegmentedLongArray`, `ByteArrayOrdinalMap`, `FreeOrdinalTracker`, `ThreadSafeBitSet`, `IFixedLengthData`, `IVariableLengthData`, `MemoryMode`, `FixedLengthDataFactory`, `VariableLengthDataFactory` |
| `core.memory.encoding` | `ZigZag`, `VarInt`, `HashCodes`, `FixedLengthElementArray`, `FixedLengthMultipleOccurrenceElementArray`, `MemoryMappedBlob` (Java's `BlobByteBuffer`), `EncodedLongBuffer`, `EncodedByteBuffer`, and `DecimalBits` (port-specific; see the format extension above) |
| `core.memory.pool` | `IArraySegmentRecycler`, `WastefulRecycler`, `RecyclingRecycler` |
| `core.schema` | `HollowSchema` and the object/list/set/map schemas, `FieldType`, `SchemaType`, `SimpleHollowDataset`, `HollowSchemaSorter`, `HollowSchemaHash` |
| `core.index` | `FieldPaths` and the bound `FieldPath`/`FieldSegment`/`ObjectFieldSegment`/`FieldPathException` types, `HollowPrimaryKeyIndex`, `HollowUniqueKeyIndex`, `HollowHashIndex` and its builder, preindexer, field and result types, `HollowPrefixIndex` and the `TernarySearchTree` behind it, `HollowSparseIntegerSet`, `ValueFieldPath` (Java's package-private `core.index.FieldPath`), `GrowingSegmentedLongArray`, `MultiLinkedElementArray` |
| `core.index.traversal` | The traversal tree and `HollowIndexerValueTraverser`, which enumerate every combination of values a record's indexed paths reach |
| `core.index.key` | `PrimaryKey`, including its dataset-resolution helpers, and `HollowPrimaryKeyValueDeriver` |
| `core` | `HollowConstants`, `IHollowDataset`, `HollowHeaderTags` (the header tags `HollowStateEngine` declares) |
| `core.schema` (text) | `HollowSchemaParser`, which reads schemas back from the form `ToString` writes — see [Schemas as text](#schemas-as-text) |
| `api.consumer.data` | `HollowDataAccessor<T>`, over `RecordChangeSet` — see [What a transition changed](#what-a-transition-changed) — and `GenericHollowRecordDataAccessor` over it. Java's `AbstractHollowOrdinalIterable` has nothing to port; see below |
| `core.type.accessor` | The seven scalar data accessors, over one generic base |
| `api.codegen.api` (part) | The data accessor generator; Java's type-API and factory generators are covered by this port's own emitters |
| `core.util` | `BitSet` and `IntList` (port-specific stand-ins for `java.util.BitSet` and Hollow's `IntList`), `InvariantFormatting`, `HollowWriteStateCreator`, `HollowRecordCollection<T>`, `StateEngineRoundTripper`, `BlobCopy` |
| `tools.checksum` | `HollowChecksum` and `ApplyToChecksum` on the four read states, which the producer's integrity check compares |
| `core.write` | The write records (object, list, set, map), `FieldStatistics`, `HollowTypeWriteState` and its four subclasses including the four-way partitioned ordinal map, `HollowWriteStateEngine`, `HollowBlobHeaderWriter`, `HollowBlobWriter`, `HollowBlobOutput` |
| `core.write.copy` | `HollowRecordCopier` and the object/list/set/map copiers, plus `IOrdinalRemapper`/`IdentityOrdinalRemapper` (Java puts the remapper in `tools.combine`) |
| `core.write.objectmapper` | `HollowObjectMapper`, the four type mappers, and the `HollowTypeName`/`HollowInline`/`HollowTransient`/`HollowPrimaryKey`/`HollowHashKey`/`HollowShardLargeType`/`HollowCollectionTypeName`/`HollowMapTypeName` attributes, including Java's default hash-key derivation |
| `core.write.objectmapper.flatrecords` | `FlatRecord`, `FlatRecordWriter`, `FlatRecordReader`, `FlatRecordOrdinalReader`, `FlatRecordExtractor`, `FlatRecordDumper`, `FlatRecordStringifier`, the traversal nodes, and `IHollowSchemaIdentifierMapper` with a dataset implementation Java does not ship — see [Flat records](#flat-records) |
| `core.read` | `HollowBlobInput`, `HollowBlobHeaderReader`, `HollowBlobReader`, `HollowReadStateEngine`, `HollowTypeReadState`, `PopulatedOrdinalListener`, `SnapshotPopulatedOrdinalsReader`, the data-access interfaces, `ITypeFilter` |
| `core.read.engine.*` | Data elements and read states for object, list, set and map |
| `core.read.iterator` | `OrdinalEnumerables`, which walks a collection record's elements — including the potential-match walks used for key lookups |
| `core.memory.encoding` (delta) | `GapEncodedVariableLengthIntegerReader` |
| delta write path | `CalculateDelta`/`WriteCalculatedDelta` on all four type write states, `HollowBlobWriter.WriteDelta` |
| delta read path | `ApplyDelta` on the data elements and read states of all four record kinds, `HollowBlobReader.ApplyDelta` |
| reverse deltas | `HollowTypeWriteState.CalculateReverseDelta`, `HollowBlobWriter.WriteReverseDelta`; a consumer applies one through the same `ApplyDelta` |
| hash keys | `HollowWriteStateEnginePrimaryKeyHasher` on the write side, `SetMapKeyHasher` and `FindElement`/`FindKey`/`FindValue`/`FindEntry` on the read side |
| restore | `HollowWriteStateEngine.RestoreFrom` and `HollowTypeWriteState.RestoreFrom`, `HollowWriteStateCreator` |
| resharding (read) | `ShardsHolder`, `HollowTypeDataElements`/`HollowTypeReadStateShard`, the data element splitters and joiners for all four record kinds, `HollowTypeReshardingStrategy`, `GapEncodedVariableLengthIntegerReader.Split`/`Join` |
| resharding (write) | `HollowWriteStateEngine.AllowTypeResharding`, `GatherShardingStats` with its one-factor-of-two-per-cycle rule, `RevNumShards`/`RevMaxShardOrdinal` and the direction-aware delta writers, the `hollow.type.resharding.invoked` header tag |
| `tools.traverse` | `TransitiveSetTraverser` — `AddTransitiveMatches`, `RemoveReferencedOutsideClosure`, `AddReferencingOutsideClosure` |
| `api.consumer` | `HollowConsumer` and `HollowConsumerBuilder`, `Blob`/`HeaderBlob`/`BlobType`, `IBlobRetriever`, `IAnnouncementWatcher`/`VersionInfo`/`AnnouncementStatus`, `IRefreshListener`/`ITransitionAwareRefreshListener`/`IRefreshRegistrationListener`/`HollowRefreshListener`, `IDoubleSnapshotConfig`, `IUpdatePlanBlobVerifier`, `IObjectLongevityConfig` |
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
| `core.read.dataaccess.proxy` | `HollowProxyDataAccess`, `HollowTypeProxyDataAccess` and the object/list/set/map proxies; see [Object longevity](#object-longevity) |
| `core.read.dataaccess.disabled` | `HollowDisabledDataAccess` and its four type counterparts, plus `HollowDataAccessDisabledException` |
| object longevity (`api.client`) | `IObjectLongevityConfig`/`ObjectLongevityConfig`, `IObjectLongevityDetector`, `StaleReferenceDetector`, and `HollowConsumerBuilder.WithObjectLongevityConfig`; see [Object longevity](#object-longevity) |
| `api.codegen` (client API) | `HollowCodeGenerator` and its options, the model resolver, the naming rules and the emitters for all four record kinds, the API class, the API factory and the unique-key index |
| `api.codegen` (source generator) | `Hollow.SourceGenerator`: `HollowApiSourceGenerator`, `SymbolModel` and the `HollowGeneratedApi` attribute — no Java counterpart, since Java has nothing that runs inside the compiler |
| `tools.diff` | `HollowDiff`, `HollowTypeDiff`, `HollowDiffMatcher`, `HollowFieldDiff`, `HollowDiffNodeIdentifier`, the `diff.exact` equality mapping and its four mappers, and the `diff.count` counting tree |
| `hollow-explorer-ui` | `Hollow.Explorer`: four pages over a dataset; see [The explorer](#the-explorer) |
| `hollow-diff-ui` (`diffview`, `diff.ui`) | `Hollow.Explorer.Diff`: the effigy, pairers, row tree and renderer, and four pages over a calculated diff; see [The diff UI](#the-diff-ui) |
| `hollow-diff-ui` (`history.ui`) | `Hollow.Explorer.History`: the models, the record namer, the version-to-timestamp reading and six pages over a `HollowHistory`; see [The history UI](#the-history-ui) |
| `tools.history` | `HollowHistory` and `HollowHistoricalState`, `HollowHistoricalStateCreator` and the four delta historical state creators, the historical data accesses, the key index and its ordinal mapper, `IntMap`, `RemovedOrdinals`, `ObjectInternPool` and the two ordinal remappers |
| `tools.combine` | `HollowCombiner` and its five copy directors, `HollowCombinerOrdinalRemapper` and `HollowCombinerPrimaryKeyOrdinalRemapper`; see [Combining, splitting and patching](#combining-splitting-and-patching) |
| `tools.split` | `HollowSplitter`, `HollowSplitterShardCopier`, `HollowSplitterOrdinalRemapper` and the ordinal and primary-key copy directors; see [The splitter](#the-splitter) |
| `tools.patch` | `HollowStateEngineRecordPatcher` with `TypeMatchSpec` and `HollowPatcherCombinerCopyDirector`, and `HollowStateDeltaPatcher` with `PartialOrdinalRemapper`; see [The patchers](#the-patchers) |
| `api.perfapi` | `HollowRef`, `HollowPerformanceApi`, the four type performance APIs, the collection views and `HollowPerfApiCache<T>`; see [The performance API](#the-performance-api) |
| `api.metrics` | `HollowMetrics`, `HollowConsumerMetrics`, `HollowProducerMetrics`, `IHollowMetricsCollector<T>` |
| `api.producer` (minter) | `VersionMinterWithLookahead`, which reserves the next version before publishing this one |
| `tools.filter` | `FilteredHollowBlobWriter`, which drops types and fields from a blob by walking its bytes, and `BlobCopy` (Java's `IOUtils`) under it |
| `api.testdata` | `HollowTestRecord` and the four record kinds, `HollowTestDataset`; `StateEngineRoundTripper` moved out of the test project into `core.util` to support it |
| `tools.compact` | `HollowCompactor` and `CompactionConfig`; see [Compaction](#compaction) |
| `tools.diff.specific` | `HollowSpecificDiff`, and the subset-of-paths hash and equality overloads on `HollowIndexerValueTraverser` it needs |
| `api.producer.metrics` | `CycleMetrics`, `AnnouncementMetrics`, `ProducerMetricsListener` (Java's `AbstractProducerMetricsListener`); see [Metrics](#metrics) |
| `api.consumer.metrics` | `ConsumerRefreshMetrics`, `UpdatePlanDetails`, `RefreshMetricsListener` (Java's `AbstractRefreshMetricsListener`); see [Metrics](#metrics) |
| `api.sampling` | `HollowSamplingDirector` and the disabled, enabled and time-sliced directors, `ISamplingStatusListener`, `SampleResult`, `IHollowSampler`, the object, collection and creation samplers, and `NullSampler`; wired through `IHollowTypeDataAccess`, the four read states, `HollowReadStateEngine` and `HollowApi` — see [Sampling](#sampling) |
| `api.codegen.perfapi` | `HollowPerfApiGenerator` and its options, over `PerfApiEmitter` — one class per object type, and the built-in type APIs for the collections |
| `api.codegen.testdata` | `HollowTestDataGenerator` and its options, over `TestDataEmitter` — a fluent builder per type, typed by its parent, with shortcuts for single-field wrapper types |
| optional blob parts | `HollowBlobOptionalPartHeader` and its reader and writer, `OptionalBlobPartConfig`/`OptionalBlobPartOutputs`, `OptionalBlobPartInput`, the parts overloads on `HollowBlobWriter` and `HollowBlobReader`, and the staging, publishing and retrieval either side — see [Optional blob parts](#optional-blob-parts) |
| `core.write.objectmapper` (memoization) | `IMemoizedRecord` with `MemoizedList<T>`, `MemoizedSet<T>` and `MemoizedMap<TKey, TValue>` over it, and the remembering write path on the three collection mappers |
| `api.producer` (publish options) | `BlobStorageCleaner`, and `WithSnapshotPublishScheduler` for publishing the snapshot off the cycle thread |
| `hollow-ui-tools` | Only `HollowDiffUtil.formatBytes`, as `ByteSize.Format`; the rest is servlet plumbing ASP.NET Core replaces |

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

- **`AbstractHollowOrdinalIterable`**, which has nothing to port. It exists so that a generated hash
  index can turn a one-shot ordinal iterator into an `Iterable<T>`, and Java's own comment on it says
  its instances misbehave on a second iteration. Here `HollowHashIndexResult` is an
  `IEnumerable<int>` and `HashIndexSelect<T, TSelect, TQuery>.FindMatches` already returns typed
  records, both of them repeatable, so the class would be a worse version of what is already there.
- **The deprecated `api.client.HollowClient`**, superseded by `HollowConsumer`; only the parts of
  `api.client` that `HollowConsumer` uses are ported.
- **`api.codegen`'s POJO generator**, which emits classes that copy a record's fields out of the
  dataset — the one thing a Hollow client exists to avoid. Its performance API and test data
  generators are ported; see [The code generator](#the-code-generator). Also the adapter modules —
  `hollow-jsonadapter`, `hollow-protoadapter`, `hollow-zenoadapter`, `hollow-test`.
  `hollow-fakedata` is ported as `samples/Hollow.FakeData`
  ([The fake data generator](#the-fake-data-generator)) and `hollow-perf` as
  `benchmarks/Hollow.Benchmarks` ([The benchmarks](#the-benchmarks)).
  `hollow-explorer-ui` is ported as `Hollow.Explorer`
  ([The explorer](#the-explorer)), `hollow-diff-ui`'s `diffview` and `diff.ui` packages as
  `Hollow.Explorer.Diff` ([The diff UI](#the-diff-ui)), and its `history.ui` package as
  `Hollow.Explorer.History` ([The history UI](#the-history-ui)).
  Of `hollow-ui-tools`, only `HollowDiffUtil`'s `formatBytes` and `HtmlEscapingWriter` had anything
  to port — the rest is Jetty and servlet plumbing that ASP.NET Core replaces outright.
- **`HollowObjectHashCodeFinder` and `DefaultHashCodeFinder`**, the deprecated custom-hash-code
  mechanism. Four ported files already assume its absence in print — `HollowCombiner`,
  `HollowStateDeltaPatcher`, `HollowSplitter` and `HollowCompactor` — so porting it means revisiting
  all four.
- **`StackTraceRecorder`**, which attributes a stale read to the code that made it by capturing a
  stack trace per read.
- **`NullablePrimitiveBoolean`**, deprecated in Java itself, which names inlining a nullable boolean
  as the replacement. `[HollowInline]` on a `bool?` does that here.
- **`StageStats`**, an empty marker interface.
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
there is a feature on top rather than a gap in the loop, and the largest of them is the POJO
generator, which emits the one thing a Hollow client exists to avoid.

Two things about the producer's publish options are worth knowing before touching that code. A
snapshot published on a scheduler still holds up the end of its cycle: the cycle deletes its staged
files when it finishes, so `Artifacts.Cleanup` waits for the upload rather than racing it. And the
blob storage cleaner runs in a `finally` after each publish, so a failed publish still gets the chance
to tidy up after itself.

All three UIs are ported — the explorer, the diff and the history — and with the history went
`tools.history` underneath it. `tools` is now ported in full: `combine`, `split` and `patch` went in
last, and the delta patcher's tests turned up a real bug in the object delta applicator along the way
— see [A delta bug the patcher found](#a-delta-bug-the-patcher-found).

Object longevity went in after that — see [Object longevity](#object-longevity), where the tests turned
up a hole in Java's own stale-reference detector. Shared-memory mode went in last — see
[Shared-memory mode](#shared-memory-mode) — and with it the last of the read path's storage options.
`core`, `api` and `tools` are now ported in full.

**Whatever comes next, read [The explorer](#the-explorer), [The diff UI](#the-diff-ui) and
[The history UI](#the-history-ui) first if it has a page in it.** The decisions there about Razor,
escaping, session state and where rows are turned into values were made once and should not be made
differently a fourth time.
