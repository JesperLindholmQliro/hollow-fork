# tools

Checking this port against Netflix Hollow itself.

```
dotnet/tools/compare-with-java.sh
```

Two questions, answered separately:

- **Do the two agree on the format?** Both write the same dataset; the script compares the bytes.
- **What does this port cost against Java's?** Both run their benchmark suites with the same
  parameters; the script puts the numbers side by side.

`--blobs` or `--benchmarks` runs one of them. Results land in `dotnet/artifacts/compare`.

## What you need

`dotnet` (or this repo's `dn.sh`), and for the Java half a JDK and the Gradle wrapper at the repo
root. Without a JDK the script runs this port's half, prints what it could not run, and exits 0 — an
absent toolchain is not a failed comparison.

The first run builds `:hollow-perf:jmhJar` and `:hollow:jar`, which takes a while. Later runs reuse
whatever is already in `build/libs`.

## The blob comparison

`Hollow.Compat` (C#) and `tools/java/HollowCompat.java` write the same dataset — the one
`FormatCompatibilityTests` pins the digest of, using every field type Netflix Hollow defines, every
schema kind, null values in the variable-length fields, and a sharded type. Each writes four blobs:
a snapshot, a second snapshot, the delta between them and the reverse delta.

If the bytes match, the two implementations agree on the format, and there is nothing more to say.

If they do not, bytes alone say nothing useful: the difference may be in the encoding or in the
data, and only the second means the two disagree about what the dataset holds. So the script goes on
to ask, in order:

1. **`describe` on both sides, through `diff -u`.** Each tool prints the schemas and then every
   record as JSON, sorted by type name. Identical output with differing bytes means the
   disagreement is in the encoding alone.
2. **Netflix Hollow reads this port's snapshot.** `HollowCompat diff` over the two snapshots. If
   Java can read a blob this port wrote and finds the same records in it, the file is valid Hollow
   whatever else is true of it — which is the question that actually matters for interoperability.

`Hollow.Compat` runs any of the three by hand:

```
dotnet run --project tools/Hollow.Compat -- write <dir> [--decimal]
dotnet run --project tools/Hollow.Compat -- describe <blob>
dotnet run --project tools/Hollow.Compat -- diff <from> <to>
```

`diff` exits 2 when the two states differ, so a script can branch on it without parsing anything.

### Two things that have to be pinned, and are

**The randomized tag.** Both implementations mint it from a random number generator and write it
into the blob header, so every comparison would fail on the header alone if it were left alone. Both
sides set it explicitly — `0x0102030405060708` for the first cycle, `0x1112131415161718` for the
second — and both re-pin it after the cycle boundary, because preparing for the next cycle mints a
fresh one.

**The dataset itself.** `CanonicalDataset.cs` and `HollowCompat.java` must stay identical. If you
change one, change the other; otherwise the script reports a difference that is the harness's rather
than the format's.

### `--decimal`

`Decimal` is this port's own field type. Netflix Hollow has no name for it, so its reader fails on
the schema of such a blob, before any record.

The blob dataset therefore leaves it out by default, which is the whole point: a dataset using no
decimal field has to serialise exactly as Netflix Hollow would serialise it, and this script is one
of the two things that checks it — `FormatCompatibilityTests` is the other, from inside the test
suite.

`--decimal` adds the field. It does not make the comparison fail; it **disables the Java side**,
because there would be nothing to compare against, and says so. Use it to see this end's own bytes
change, not to compare against anything.

## `--skip-dotnet`, while you are getting the Java side working

Reuses whatever the last run left in `--out` instead of producing it again: the benchmark results
JSON, the blobs, and the `describe` text. Nothing on the .NET side runs, so a full pass drops from
minutes to milliseconds and you can iterate on the Java half — the Gradle build, the `javac`, the
classpath — without waiting for this port's benchmarks each time.

It is a workaround, not a feature, and it does not check anything:

- The reused results are whatever is on disk. Nothing verifies they came from the same `--scale`,
  `--iterations` or `--only` as the Java run they are about to be compared against, or that they
  came from the current source.
- So the ratios a `--skip-dotnet` run prints are only as trustworthy as your memory of how the
  reused file was produced. **Do not quote them.** Take one clean run without the flag before
  believing any number.

If the file it wants is not there it says so and exits 1, rather than comparing against nothing.

## The benchmark comparison

Java's run under JMH, this port's under the harness in `benchmarks/Hollow.Benchmarks`. Both are told
the same warmup count, measurement count and iteration time; `--scale` shrinks the .NET record
counts, since Java's are a million in places.

Both write JMH-shaped JSON, which is why `compare-benchmarks.py` has one parser rather than two. It
joins on suite, case and parameters — `SUITES` in that file maps Java's class names onto this port's
suite names, the same mapping the benchmarks README documents in prose — and prints ns/op for each
side with the ratio of .NET over Java, so **above 1.00 is slower here**.

A case on one side only is listed rather than dropped. Usually that means the mapping is out of date
or the two sides were given different parameters; occasionally it means a benchmark was deliberately
not ported, in which case the benchmarks README says why.

### Read these as rough

The .NET harness is not JMH and does not pretend to be: no process isolation per case, no pilot run
subtracting harness overhead, no outlier removal. `benchmarks/Hollow.Benchmarks/README.md` says
exactly what it does and does not do. A ratio of 1.1 against a JMH number means nothing; a ratio of
10 means something. Two cases measure something slightly different from Java's by design, and that
README names them.
