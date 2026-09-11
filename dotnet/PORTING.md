# Porting Hollow to .NET 10

This directory holds a .NET 10 port of Netflix Hollow's core engine. All four record kinds — object,
list, set and map — round-trip end to end: a `HollowWriteStateEngine` writes a snapshot blob that a
`HollowReadStateEngine` reads back. An object mapper maps ordinary CLR types onto Hollow records, so
callers need not build records field by field.

Deltas are **partly** here: the producer can calculate and write a delta for all four record kinds,
and a consumer can apply one for object types. Applying a collection-type delta is not implemented, so
the writer refuses to produce one rather than emit a blob no consumer here could read.

What is **not** here is resharding, restore, indexing, the diff/history tools, and the
producer/consumer APIs. The status section says exactly what is and is not ported.

## Building and testing

```
dotnet build
dotnet test
```

Requires the .NET 10 SDK. On a machine without ICU installed, set
`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` — nothing in the port depends on culture-sensitive
behaviour.

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

### NaN bit patterns

`HollowObjectWriteRecord` derives its float and double null sentinels from Java's canonical NaN
(`0x7FC00000` / `0x7FF8000000000000`) plus one. .NET's `float.NaN` and `double.NaN` have the sign bit
set, so deriving the sentinel the way Java does would produce a different, incompatible value. The
sentinels are written as literals, and a NaN *value* is canonicalised to Java's pattern before being
stored, matching `Float.floatToIntBits` rather than `floatToRawIntBits`.

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
| `core.memory.encoding` | `ZigZag`, `VarInt`, `HashCodes`, `FixedLengthElementArray` |
| `core.memory.pool` | `IArraySegmentRecycler`, `WastefulRecycler`, `RecyclingRecycler` |
| `core.schema` | `HollowSchema` and the object/list/set/map schemas, `FieldType`, `SchemaType`, `SimpleHollowDataset` |
| `core.index.key` | `PrimaryKey` (identity and accessors only) |
| `core` | `HollowConstants`, `IHollowDataset` |
| `core.util` | `BitSet` (a port-specific stand-in for `java.util.BitSet`) |
| `core.write` | The write records (object, list, set, map), `FieldStatistics`, `HollowTypeWriteState` and its four subclasses, `HollowWriteStateEngine`, `HollowBlobHeaderWriter`, `HollowBlobWriter`, `HollowBlobOutput` |
| `core.write.objectmapper` | `HollowObjectMapper`, the four type mappers, and the `HollowTypeName`/`HollowInline`/`HollowTransient`/`HollowPrimaryKey`/`HollowShardLargeType` attributes |
| `core.read` | `HollowBlobInput`, `HollowBlobHeaderReader`, `HollowBlobReader`, `HollowReadStateEngine`, `HollowTypeReadState`, `PopulatedOrdinalListener`, `SnapshotPopulatedOrdinalsReader`, the data-access interfaces, `ITypeFilter` |
| `core.read.engine.*` | Data elements and read states for object, list, set and map |
| `core.read.iterator` | The ordinal iterators, including the potential-match iterators used for key lookups |
| `core.memory.encoding` (delta) | `GapEncodedVariableLengthIntegerReader` |
| delta write path | `CalculateDelta`/`WriteCalculatedDelta` on all four type write states, `HollowBlobWriter.WriteDelta` |
| delta read path | `HollowObjectTypeDataElements.ApplyDelta`, `HollowObjectTypeReadState.ApplyDelta`, `HollowBlobReader.ApplyDelta` (object types only) |

Test coverage is carried over from the Java tests where they exist — `VarIntTest`, `HashCodesTest`,
`FixedLengthElementArrayTest`, `FreeOrdinalTrackerTest`, `ThreadSafeBitSetTest`,
`ByteArrayOrdinalTest`, and the schema tests — with additional cases for the port-specific IO layer.
`HashCodesTests` pins MurmurHash3 output against values produced by an independent transcription of
the Java algorithm, so the port cannot drift from the blob layout contract unnoticed.

`RoundTripTests`, `CollectionRoundTripTests` and `ObjectMapperTests` cover the write and read engines
together, which is the only way to check the blob format itself: the bit packing, the variable-length
ranges, the null sentinels, the hash-table layout, the shard layout and the trailing
populated-ordinals bit set all have to agree for a record to survive the trip.

### Ported, not yet covered by a round-trip

`HollowObjectSchema.FilterSchema` and the read path honour a filter, but only the explicit include
sets of `TypeFilter` exist; the recursive rule DSL is still absent.

### Not ported

- **Schema-declared hash keys on sets and maps.** Java re-hashes elements through
  `HollowWriteStateEnginePrimaryKeyHasher` so a consumer can find an element by key rather than by
  ordinal. That hasher needs the field-path machinery in `core.index`, which is not ported, so a set
  or map schema carrying a hash key is written with ordinal hashing. Lookups by ordinal are
  unaffected; lookups by key are not available.
- **Collection-type delta application.** The write side produces deltas for list, set and map types,
  but no applicator exists for them, so `HollowBlobWriter.WriteDelta` throws rather than writing one.
  The object applicator shows the shape; the collection ones are simpler, since a collection record is
  a run of elements rather than a set of independently sized fields.
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
- **Indexing** (`core.index` beyond `PrimaryKey`), **tools** (`core.tools`: diff, history, combine,
  split, patch, checksum), **the producer and consumer APIs** (`api.producer`, `api.consumer`),
  **code generation** (`api.codegen`), **sampling** (`api.sampling`), and every module outside
  `hollow` — `hollow-diff-ui`, `hollow-explorer-ui`, `hollow-jsonadapter`, `hollow-protoadapter`,
  `hollow-zenoadapter`, `hollow-test`, `hollow-fakedata`.
- **`GarbageCollectorAwareRecycler`**, which picks a pooling strategy by inspecting the JVM's
  collector through `ManagementFactory`. The decision it encodes does not transfer to .NET; pick
  `RecyclingRecycler` or `WastefulRecycler` explicitly.

### Suggested order for the remaining work

1. Delta applicators for list, set and map, which completes the delta path. Each follows the object
   applicator's shape and is simpler.
2. `core.index` — `FieldPaths` and the primary key index — which also unlocks schema-declared hash
   keys on sets and maps.
3. Resharding and restore, which build on deltas.
4. The consumer API, once the delta path is complete.
