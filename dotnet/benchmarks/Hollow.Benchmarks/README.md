# Hollow.Benchmarks

The port of `hollow-perf`: what the parts of Hollow that sit on a hot path cost.

```
cd dotnet
dotnet run -c Release --project benchmarks/Hollow.Benchmarks
```

At the defaults that takes a long time — Java's record counts are a million in places — so start small:

```
dotnet run -c Release --project benchmarks/Hollow.Benchmarks -- --only HashCodes
dotnet run -c Release --project benchmarks/Hollow.Benchmarks -- --scale 0.01 --time 0.5
```

| Option | Default | What it is |
| --- | --- | --- |
| `--list` | | Print the suite names and stop |
| `--only <name>` | all | Run only the suites whose name contains this |
| `--scale <n>` | `1.0` | Multiply every record count by this; `1.0` is Java's numbers |
| `--warmup <n>` | `3` | Warmup iterations, thrown away |
| `--iterations <n>` | `5` | Measured iterations |
| `--time <seconds>` | `1` | How long each iteration should take |

Always measure a Release build. A Debug build measures the Debug build.

## Why there is a harness in here

Java runs these under JMH. The .NET equivalent is BenchmarkDotNet, and it is not used here: it cannot
be restored on every machine this port gets built on, and a benchmark project that will not build is
worth less than a rough one that will. `Harness.cs` does the parts that matter —

- a calibrated operation count, so the stopwatch's resolution does not become the measurement;
- warmup iterations that are discarded;
- an error bar, as half a 99.9% confidence interval, the same one JMH reports.

— and nothing else. In particular there is no process isolation per case, no pilot run subtracting
harness overhead, no outlier removal, and no memory diagnostics. **Read the numbers as ratios between
cases in one run**, not as absolutes to quote. If you need absolutes, add BenchmarkDotNet on a
machine that can restore it; the suites are ordinary classes and would port over in an afternoon.

## The suites

| Suite | Java | What it asks |
| --- | --- | --- |
| `HashCodes` | `HashCodesBenchmark` | What each hash the format depends on costs, over 1, 10 and 1000 bytes |
| `OrdinalMapResize` | `OrdinalMapResize` | What filling an ordinal map that has to grow costs against one sized up front |
| `FixedLengthElementArray` | `ReadWriteFixedLengthElementArrayTest` | Reading and writing the bit string every record's fixed-length fields live in |
| `HollowPrimaryKeyIndex` | `HollowPrimaryKeyIndexBenchmark` | Building the "which record has this key" index, and asking it |
| `HollowHashIndex` | `HollowHashIndexBenchmark` | The same, where several records may match |
| `HollowObjectTypeReadStateLong` | `HollowObjectTypeReadStateLongBenchmark` | Reading a long field at a width that fits one word and one that does not |
| `HollowObjectTypeReadStateShard` | `HollowObjectTypeReadStateShardBenchmark` | Reading a string back out of variable-length storage |
| `ChecksumCollections` | `CheckSumCollections` | Checksumming a list, a set and a map type |
| `ReadWriteStateEngine` | `ReadWriteStateEngineTest` | Snapshot round trip through memory, a file and a pipe |
| `DuplicateDataDetection` | `DuplicateDataDetectionValidatorBenchmark` | What a producer's duplicate-key validator costs |
| `ObjectTypeReadStateDeltaTransition` | `HollowObjectTypeReadStateDeltaTransitionBenchmark` | What a read costs while transitions keep replacing the storage under it |

## What is not ported, and why

**`FixedLengthElementArrayPlainPut` and `SegmentedLongArrayPlainPut`,** the two classes in
`hollow-perf/src/main`, and the `writePlain` case that uses them. They are copies of core classes
with `Unsafe.putOrderedLong` swapped for `Unsafe.putLong`, so that the release store in
`setElementValue` can be priced. This port has no release store there to price:
`SegmentedLongArray.Set` is an ordinary array store, and a record is published by one fence before
its ordinal is written rather than by ordering every word. The two classes and the comparison fall
away together.

**`deltaLaggedIndex`,** the third case of the duplicate-detection benchmark. It prices
`DuplicateDataDetectionValidator.findDuplicateKeysInDelta` — probing only the ordinals a delta added,
against an index built before it. That method does not exist in this port; the validator has only the
full-scan path. The two snapshot cases it would be compared against are here.

## Two places the port measures something slightly different

**`Read` and `ReadLarge` run the same code.** Java has two read paths through the bit string, an
unaligned single read for widths up to 56 bits and a two-word read above it. This port dropped the
first — `GetElementValue` forwards to `GetLargeElementValue` — so the two cases should measure the
same. Keeping both says so, and a run where they differ is worth looking into. The same goes for the
two `maxBits` values in `HollowObjectTypeReadStateLong`.

**The delta-transition suite takes a lock that Java's does not.** Applying a delta releases the
storage it replaced the moment the new storage is published, in this port and in Java alike, so a
read part way through the old elements will fault. That is what object longevity exists for, and it
is not what the benchmark is pricing — Java's version happens to survive it by reading only the first
few hundred ordinals over and over. Here the read and the transition are fenced against each other
with a `ReaderWriterLockSlim`, which costs an uncontended read lock per operation.

## Determinism

Every suite seeds its random number generator. Java uses `ThreadLocalRandom` or a bare `Random` in
most of these, which makes a run impossible to repeat — and the data decides what is being measured:
the string lengths in the read-string suite and the value magnitudes in the read-long one change the
answer, not just the noise around it.
