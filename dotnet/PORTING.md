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

Both ends of the publish/consume loop that a delta chain exists to serve are here as well. A producer
that restarts calls `HollowWriteStateEngine.RestoreFrom` to rebuild its write state from the last
published read state and carries on producing deltas, rather than starting a new chain with a fresh
snapshot. On the other side, `HollowConsumer` keeps a local copy of the dataset up to date from a blob
store, following deltas where it can and loading a snapshot only where it must.

There is **one deliberate departure from the Hollow format**: a `Decimal` field type that stores a .NET
`decimal` exactly. It is opt-in — a dataset that declares no decimal field is byte-identical to what
Netflix Hollow produces. Read [Format extension: the `Decimal` field
type](#format-extension-the-decimal-field-type) before changing anything in the write or read path.

What is **not** here is resharding, object longevity, code generation, the diff/history tools, and the
producer API. The status section says exactly what is and is not ported.

## Building and testing

```
dotnet build
dotnet test
```

Requires the .NET 10 SDK. On a machine without ICU installed, set
`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` — nothing in the port depends on culture-sensitive
behaviour.

## Consuming a dataset

```csharp
using HollowConsumer consumer = new HollowConsumerBuilder()
    .WithLocalBlobStore("/var/hollow/blobs")
    .WithAnnouncementWatcher(new HollowFilesystemAnnouncementWatcher("/var/hollow/blobs"))
    .Build();

consumer.TriggerRefresh();

HollowPrimaryKeyIndex index = new(consumer.StateEngine!, new PrimaryKey("Movie", "id"));
int ordinal = index.GetMatchingOrdinal(42);
```

With an announcement watcher the consumer follows whatever the producer announces; without one, drive
it with `TriggerRefreshTo(version)`. Either way it follows deltas where it can, so `consumer.StateEngine`
keeps returning the same instance and an index built over it stays valid — register an
`IRefreshListener` to hear when that stops being true. There is no producer API yet, so the writing
half is a `HollowWriteStateEngine` and a `HollowBlobWriter`; see `FilesystemBlobStoreTests` for the
file names a `HollowFilesystemBlobRetriever` expects.

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
| package `com.netflix.hollow.core.memory.encoding` | namespace `Hollow.Core.Memory.Encoding` |
| package `com.netflix.hollow.api.error` | namespace `Hollow.Api.Error` |
| package `com.netflix.hollow.api.consumer` | namespace `Hollow.Api.Consumer` |
| package `com.netflix.hollow.api.client` | namespace `Hollow.Api.Client` |

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
- **`HollowConsumer.Builder.withX(...)` → `HollowConsumerBuilder.WithX(...)`.** Java makes the builder
  generic in itself so that a subclass keeps the fluent return type; C# extension methods cover that
  case, so the port's builder is a plain sealed class.

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

### Delta application copies record by record

Java's delta applicators have a fast path that bulk-copies runs of unchanged records with `copyBits`
and then fixes up their variable-length pointers with `incrementMany`. Only the record-at-a-time path
is ported. The output is identical — `DeltaTests` checks that applying a delta leaves a consumer in
exactly the state a snapshot of the same cycle would have produced — but applying a large delta is
slower than it needs to be. The fast path is the obvious next optimisation, and the tests already in
place would catch a mistake in it.

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

## Status

### Ported and tested

| Java package | Notes |
| --- | --- |
| `core.memory` | `IByteData`, `ArrayByteData`, `ByteDataArray`, `SegmentedByteArray`, `SegmentedLongArray`, `ByteArrayOrdinalMap`, `FreeOrdinalTracker`, `ThreadSafeBitSet`, `IFixedLengthData`, `IVariableLengthData`, `MemoryMode` |
| `core.memory.encoding` | `ZigZag`, `VarInt`, `HashCodes`, `FixedLengthElementArray`, and `DecimalBits` (port-specific; see the format extension above) |
| `core.memory.pool` | `IArraySegmentRecycler`, `WastefulRecycler`, `RecyclingRecycler` |
| `core.schema` | `HollowSchema` and the object/list/set/map schemas, `FieldType`, `SchemaType`, `SimpleHollowDataset`, `HollowSchemaSorter`, `HollowSchemaHash` |
| `core.index` | `FieldPaths` and the bound `FieldPath`/`FieldSegment`/`ObjectFieldSegment`/`FieldPathException` types, `HollowPrimaryKeyIndex`, `HollowUniqueKeyIndex`, `HollowHashIndex` and its builder, preindexer, field and result types, `GrowingSegmentedLongArray`, `MultiLinkedElementArray` |
| `core.index.traversal` | The traversal tree and `HollowIndexerValueTraverser`, which enumerate every combination of values a record's indexed paths reach |
| `core.index.key` | `PrimaryKey`, including its dataset-resolution helpers, and `HollowPrimaryKeyValueDeriver` |
| `core` | `HollowConstants`, `IHollowDataset`, `HollowHeaderTags` (the header tags `HollowStateEngine` declares) |
| `core.util` | `BitSet` and `IntList` (port-specific stand-ins for `java.util.BitSet` and Hollow's `IntList`), `InvariantFormatting`, `HollowWriteStateCreator` |
| `core.write` | The write records (object, list, set, map), `FieldStatistics`, `HollowTypeWriteState` and its four subclasses, `HollowWriteStateEngine`, `HollowBlobHeaderWriter`, `HollowBlobWriter`, `HollowBlobOutput` |
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
| `api.consumer` | `HollowConsumer` and `HollowConsumerBuilder`, `Blob`/`HeaderBlob`/`BlobType`, `IBlobRetriever`, `IAnnouncementWatcher`/`VersionInfo`/`AnnouncementStatus`, `IRefreshListener`/`ITransitionAwareRefreshListener`/`IRefreshRegistrationListener`/`HollowRefreshListener`, `IDoubleSnapshotConfig`, `IUpdatePlanBlobVerifier` |
| `api.consumer.fs` | `HollowFilesystemBlobRetriever`, `HollowFilesystemAnnouncementWatcher` |
| `api.client` | `HollowUpdatePlan`, `HollowUpdatePlanner`, `FailedTransitionTracker`, `HollowDataHolder`, `HollowClientUpdater` |
| `core.read` (field access) | `HollowReadFieldUtils` |

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

### Ported, not yet covered by a round-trip

`HollowObjectSchema.FilterSchema` and the read path honour a filter, but only the explicit include
sets of `TypeFilter` exist; the recursive rule DSL is still absent.

### Not ported

- **The rest of `core.index`.** `HollowPrefixIndex` and `HollowSparseIntegerSet`. `FieldPaths`
  supports the prefix-index binding mode the former needs, so it has its foundation.
- **Object longevity**, which serves reads of an older version from a live state. This is why Java has
  both `HollowPrimaryKeyIndex` and `HollowUniqueKeyIndex`; see the note below on what separates them
  here. It is also why `HollowConsumer` has no `ObjectLongevityConfig` or stale-reference detector:
  both exist to serve the proxy data access that is not ported.
- **Historical state creation**, which a consumer uses to serve queries against prior states.
- **Resharding.** A type's shard count is fixed when it is first written, and `RestoreFrom` refuses a
  read state whose shard count differs from the registered write state's rather than adapting.
- **Partitioned ordinal maps.** `HollowTypeWriteState` uses a single `ByteArrayOrdinalMap`, which is
  Java's default; the four-way partitioned variant is not ported. `RestoreFrom` and
  `HollowWriteStateCreator` are written against the single map, so adding the partitioned variant
  means revisiting the global-to-local ordinal split in both.
- **Shared-memory mode.** `MemoryMode.SharedMemoryLazy` and the `BlobByteBuffer`, `EncodedByteBuffer`
  and `EncodedLongBuffer` types behind it. Constructing a read state engine with it throws.
- **Optional blob parts**, which split a snapshot across several streams.
- **The producer API** (`api.producer`). A caller drives `HollowWriteStateEngine` and
  `HollowBlobWriter` directly, and `RestoreFrom` covers what the producer needs to resume a chain.
  What is missing is the cycle orchestration around it — versioning, validation, staging, publishing
  and announcing — including `api.producer.fs`, which is what would write the file layout
  `HollowFilesystemBlobRetriever` reads. That layout is documented on the retriever, and
  `FilesystemBlobStoreTests` reproduces it.
- **The consumer's optional layers.** Object longevity, metrics collection
  (`api.consumer.metrics`), `api.consumer.data`, and `api.consumer.index` — the last of which wraps
  the ported indexes in a generated-API-typed façade.
- **The deprecated `api.client.HollowClient`**, superseded by `HollowConsumer`; only the parts of
  `api.client` that `HollowConsumer` uses are ported.
- **Tools** (`core.tools`: diff, history, combine, split, patch, checksum),
  **code generation** (`api.codegen`), **sampling** (`api.sampling`), and every module outside
  `hollow` — `hollow-diff-ui`, `hollow-explorer-ui`, `hollow-jsonadapter`, `hollow-protoadapter`,
  `hollow-zenoadapter`, `hollow-test`, `hollow-fakedata`.
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

1. The producer API. Everything it needs from the engine is now here — deltas both ways, restore, and
   a consumer to check the result against — so what is left is the cycle orchestration: versioning,
   validation, staging, publishing, announcing, and the filesystem implementations of each. That also
   closes the one gap a caller trips over today, which is that nothing in the port writes the file
   layout `HollowFilesystemBlobRetriever` reads.
2. Resharding, which is the largest remaining piece of the engine and touches every write state.
3. `HollowPrefixIndex` and `HollowSparseIntegerSet`, the last of `core.index`.
4. The bulk-copy fast path in the delta applicators, which is a pure optimisation the existing tests
   already guard.
