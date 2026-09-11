# Porting Hollow to .NET 10

This directory holds a .NET 10 port of Netflix Hollow's core engine. All four record kinds — object,
list, set and map — round-trip end to end: a `HollowWriteStateEngine` writes a snapshot blob that a
`HollowReadStateEngine` reads back. An object mapper maps ordinary CLR types onto Hollow records, so
callers need not build records field by field.

Deltas are here for all four record kinds: the producer calculates and writes a delta, and a consumer
applies it to move forward one cycle without re-reading a snapshot.

Indexing is here too: field paths bind a declared key onto the schemas, `HollowPrimaryKeyIndex` looks
records up by key rather than by ordinal, and a set or map schema may declare a hash key, which the
producer honours when laying out each record's hash table.

There is **one deliberate departure from the Hollow format**: a `Decimal` field type that stores a .NET
`decimal` exactly. It is opt-in — a dataset that declares no decimal field is byte-identical to what
Netflix Hollow produces. Read [Format extension: the `Decimal` field
type](#format-extension-the-decimal-field-type) before changing anything in the write or read path.

What is **not** here is reverse deltas, resharding, restore, the diff/history tools, and the
producer/consumer APIs. The status section says exactly what is and is not ported.

## Building and testing

```
dotnet build
dotnet test
```

Requires the .NET 10 SDK. On a machine without ICU installed, set
`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` — nothing in the port depends on culture-sensitive
behaviour.

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

## Layout

| Java | .NET |
| --- | --- |
| `hollow/src/main/java/com/netflix/hollow/...` | `dotnet/src/Hollow/...` |
| `hollow/src/test/java/com/netflix/hollow/...` | `dotnet/tests/Hollow.Tests/...` |
| package `com.netflix.hollow.core.memory.encoding` | namespace `Hollow.Core.Memory.Encoding` |
| package `com.netflix.hollow.api.error` | namespace `Hollow.Api.Error` |

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
| `core.schema` | `HollowSchema` and the object/list/set/map schemas, `FieldType`, `SchemaType`, `SimpleHollowDataset` |
| `core.index` | `FieldPaths` and the bound `FieldPath`/`FieldSegment`/`ObjectFieldSegment`/`FieldPathException` types, `HollowPrimaryKeyIndex` |
| `core.index.key` | `PrimaryKey`, including its dataset-resolution helpers, and `HollowPrimaryKeyValueDeriver` |
| `core` | `HollowConstants`, `IHollowDataset` |
| `core.util` | `BitSet` (a port-specific stand-in for `java.util.BitSet`) |
| `core.write` | The write records (object, list, set, map), `FieldStatistics`, `HollowTypeWriteState` and its four subclasses, `HollowWriteStateEngine`, `HollowBlobHeaderWriter`, `HollowBlobWriter`, `HollowBlobOutput` |
| `core.write.objectmapper` | `HollowObjectMapper`, the four type mappers, and the `HollowTypeName`/`HollowInline`/`HollowTransient`/`HollowPrimaryKey`/`HollowHashKey`/`HollowShardLargeType` attributes, including Java's default hash-key derivation |
| `core.read` | `HollowBlobInput`, `HollowBlobHeaderReader`, `HollowBlobReader`, `HollowReadStateEngine`, `HollowTypeReadState`, `PopulatedOrdinalListener`, `SnapshotPopulatedOrdinalsReader`, the data-access interfaces, `ITypeFilter` |
| `core.read.engine.*` | Data elements and read states for object, list, set and map |
| `core.read.iterator` | The ordinal iterators, including the potential-match iterators used for key lookups |
| `core.memory.encoding` (delta) | `GapEncodedVariableLengthIntegerReader` |
| delta write path | `CalculateDelta`/`WriteCalculatedDelta` on all four type write states, `HollowBlobWriter.WriteDelta` |
| delta read path | `ApplyDelta` on the data elements and read states of all four record kinds, `HollowBlobReader.ApplyDelta` |
| hash keys | `HollowWriteStateEnginePrimaryKeyHasher` on the write side, `SetMapKeyHasher` and `FindElement`/`FindKey`/`FindValue`/`FindEntry` on the read side |
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

### Ported, not yet covered by a round-trip

`HollowObjectSchema.FilterSchema` and the read path honour a filter, but only the explicit include
sets of `TypeFilter` exist; the recursive rule DSL is still absent.

### Not ported

- **The rest of `core.index`.** `HollowHashIndex`, `HollowPrefixIndex`, `HollowUniqueKeyIndex`,
  `HollowSparseIntegerSet` and the traversal helpers. `FieldPaths` supports the hash-index and
  prefix-index binding modes those need, so they have their foundation.
- **Reverse deltas.** `CalculateReverseDelta` exists on the write state but nothing writes or applies
  one.
- **Historical state creation**, which a consumer uses to serve queries against prior states.
- **Restore and resharding.** Restoring a write state from a read state, and changing a type's shard
  count across cycles. A type's shard count is fixed when it is first written.
- **Partitioned ordinal maps.** `HollowTypeWriteState` uses a single `ByteArrayOrdinalMap`, which is
  Java's default; the four-way partitioned variant is not ported.
- **Shared-memory mode.** `MemoryMode.SharedMemoryLazy` and the `BlobByteBuffer`, `EncodedByteBuffer`
  and `EncodedLongBuffer` types behind it. Constructing a read state engine with it throws.
- **Optional blob parts**, which split a snapshot across several streams.
- **Tools** (`core.tools`: diff, history, combine,
  split, patch, checksum), **the producer and consumer APIs** (`api.producer`, `api.consumer`),
  **code generation** (`api.codegen`), **sampling** (`api.sampling`), and every module outside
  `hollow` — `hollow-diff-ui`, `hollow-explorer-ui`, `hollow-jsonadapter`, `hollow-protoadapter`,
  `hollow-zenoadapter`, `hollow-test`, `hollow-fakedata`.
- **`GarbageCollectorAwareRecycler`**, which picks a pooling strategy by inspecting the JVM's
  collector through `ManagementFactory`. The decision it encodes does not transfer to .NET; pick
  `RecyclingRecycler` or `WastefulRecycler` explicitly.

### Notes for whoever picks this up next

The one thing in this port that is not a faithful reproduction of Netflix Hollow is the `Decimal`
field type. Its compatibility rule — a dataset that uses no decimal field serialises exactly as
Netflix Hollow would serialise it — is enforced by `FormatCompatibilityTests` and has to survive every
subsequent change. If you are touching the write path, the read path, the delta applicators or the
field filter, read [Format extension: the `Decimal` field
type](#format-extension-the-decimal-field-type) first; the trap is that a decimal field is 128 bits
wide and every other fixed-length field is 64 or fewer.

### Suggested order for the remaining work

1. `HollowHashIndex` and `HollowUniqueKeyIndex`, which bind through the same `FieldPaths` machinery
   that is now in place and give a key-based lookup that survives more than two deltas.
2. Reverse deltas, which reuse the applicators the forward path already has —
   `CalculateReverseDelta` exists on the write state and produces the arrays; nothing writes or
   applies them yet.
3. Resharding and restore, which build on deltas.
4. The consumer API, now that the delta path is complete.
5. The bulk-copy fast path in the delta applicators, which is a pure optimisation the existing tests
   already guard.
