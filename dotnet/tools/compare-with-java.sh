#!/usr/bin/env bash
#
#  Copyright 2016-2019 Netflix, Inc.
#
#     Licensed under the Apache License, Version 2.0 (the "License");
#     you may not use this file except in compliance with the License.
#     You may obtain a copy of the License at
#
#         http://www.apache.org/licenses/LICENSE-2.0
#
#     Unless required by applicable law or agreed to in writing, software
#     distributed under the License is distributed on an "AS IS" BASIS,
#     WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
#     See the License for the specific language governing permissions and
#     limitations under the License.
#
# Runs Netflix Hollow's benchmarks and this port's with the same parameters, and checks that both
# write the same bytes for the same dataset. See README.md next to this file.

set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
dotnet_root="$(dirname "$here")"
repo_root="$(dirname "$dotnet_root")"

# Java's own record counts are a million in places, so a full run takes a long time; --scale is how
# the .NET side is told to match a smaller one.
warmup=3
iterations=5
time=1
scale=1.0
only=""
phase="all"
with_decimal=0
out="$dotnet_root/artifacts/compare"

usage() {
    cat <<'USAGE'
compare-with-java.sh [options]

  --warmup N          Warmup iterations on both sides (default 3)
  --iterations N      Measured iterations on both sides (default 5)
  --time SECONDS      How long each iteration should take (default 1)
  --scale N           Multiply the .NET record counts by this (default 1.0)
  --only PATTERN      Run only benchmarks whose name contains this, on both sides
  --benchmarks        Run the benchmark comparison only
  --blobs             Run the blob comparison only
  --decimal           Include this port's Decimal field type in the blob dataset. Netflix
                      Hollow cannot read such a blob, so this disables the Java side of the
                      blob comparison rather than making it fail.
  --out DIR           Where to put results (default dotnet/artifacts/compare)
  -h, --help          This

Needs dotnet, or this repo's dn.sh. For the Java half, also a JDK and the Gradle wrapper at the
repo root. Without a JDK the script runs this port's half, says what it could not run, and exits
0 - an absent toolchain is not a failed comparison.
USAGE
}

while [ $# -gt 0 ]; do
    case "$1" in
        --warmup) warmup="$2"; shift 2 ;;
        --iterations) iterations="$2"; shift 2 ;;
        --time) time="$2"; shift 2 ;;
        --scale) scale="$2"; shift 2 ;;
        --only) only="$2"; shift 2 ;;
        --benchmarks) phase="benchmarks"; shift ;;
        --blobs) phase="blobs"; shift ;;
        --decimal) with_decimal=1; shift ;;
        --out) out="$2"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "compare-with-java.sh: unknown option $1" >&2; usage >&2; exit 1 ;;
    esac
done

mkdir -p "$out"

have_java=0
if command -v java >/dev/null 2>&1 && command -v javac >/dev/null 2>&1 \
        && [ -x "$repo_root/gradlew" ]; then
    have_java=1
fi

# `dotnet` on PATH where there is one, and the repo's own launcher where there is not: dn.sh points
# at an SDK unpacked outside the usual places, which is how this port is built on machines with no
# system-wide .NET.
if command -v dotnet >/dev/null 2>&1; then
    dn=(dotnet)
elif [ -x "$dotnet_root/dn.sh" ]; then
    dn=("$dotnet_root/dn.sh")
else
    echo "compare-with-java.sh: no dotnet on PATH and no dn.sh to fall back on" >&2
    exit 1
fi

say() { printf '\n== %s ==\n\n' "$1"; }

# The uber-jar JMH needs and the plain Hollow jar HollowCompat compiles against. Built once and
# reused: Gradle is slow enough that building per phase would dominate a small run.
java_jars() {
    local jmh_jar hollow_jar

    jmh_jar="$(ls "$repo_root"/hollow-perf/build/libs/hollow-perf-*-jmh.jar 2>/dev/null | head -1 || true)"

    if [ -z "$jmh_jar" ]; then
        say "Building the Java benchmarks (this takes a while the first time)"
        (cd "$repo_root" && ./gradlew --quiet :hollow-perf:jmhJar)
        jmh_jar="$(ls "$repo_root"/hollow-perf/build/libs/hollow-perf-*-jmh.jar | head -1)"
    fi

    hollow_jar="$(ls "$repo_root"/hollow/build/libs/hollow-*.jar 2>/dev/null \
        | grep -v -e sources -e javadoc | head -1 || true)"

    if [ -z "$hollow_jar" ]; then
        (cd "$repo_root" && ./gradlew --quiet :hollow:jar)
        hollow_jar="$(ls "$repo_root"/hollow/build/libs/hollow-*.jar \
            | grep -v -e sources -e javadoc | head -1)"
    fi

    printf '%s\n%s\n' "$jmh_jar" "$hollow_jar"
}

# ---------------------------------------------------------------- benchmarks

run_benchmarks() {
    say "This port's benchmarks"

    local args=(
        --warmup "$warmup" --iterations "$iterations" --time "$time" --scale "$scale"
        --json "$out/dotnet-benchmarks.json"
    )

    if [ -n "$only" ]; then
        args+=(--only "$only")
    fi

    (cd "$dotnet_root" && "${dn[@]}" run -c Release --project benchmarks/Hollow.Benchmarks -- "${args[@]}")

    if [ "$have_java" -eq 0 ]; then
        echo
        echo "No JDK on this machine, so Netflix Hollow's benchmarks were not run."
        echo "This port's results are in $out/dotnet-benchmarks.json."
        return 0
    fi

    local jmh_jar
    jmh_jar="$(java_jars | sed -n 1p)"

    say "Netflix Hollow's benchmarks"

    # -f 1 matches the single fork the benchmark classes declare; -r and -w take seconds. A trailing
    # argument is JMH's own benchmark selector, which --only feeds.
    local jmh_args=(
        -wi "$warmup" -i "$iterations" -r "${time}s" -w "${time}s" -f 1
        -rf json -rff "$out/java-benchmarks.json"
    )

    if [ -n "$only" ]; then
        jmh_args+=("$only")
    fi

    java -jar "$jmh_jar" "${jmh_args[@]}"

    say "Side by side"

    python3 "$here/compare-benchmarks.py" \
        "$out/java-benchmarks.json" "$out/dotnet-benchmarks.json" | tee "$out/benchmarks.txt"
}

# --------------------------------------------------------------------- blobs

run_blobs() {
    say "This port's blobs"

    local args=(write "$out/dotnet-blobs")

    if [ "$with_decimal" -eq 1 ]; then
        args+=(--decimal)
    fi

    (cd "$dotnet_root" && "${dn[@]}" run -c Release --project tools/Hollow.Compat -- "${args[@]}")

    if [ "$with_decimal" -eq 1 ]; then
        echo
        echo "--decimal was given, so the dataset carries a field type Netflix Hollow has no name"
        echo "for. Its reader fails on the schema, so the Java side is not run and nothing is"
        echo "compared. The blobs in $out/dotnet-blobs are this port's alone."
        return 0
    fi

    if [ "$have_java" -eq 0 ]; then
        echo
        echo "No JDK on this machine, so Netflix Hollow's blobs were not written and nothing was"
        echo "compared. This port's blobs are in $out/dotnet-blobs."
        return 0
    fi

    local hollow_jar classes
    hollow_jar="$(java_jars | sed -n 2p)"
    classes="$out/java-classes"

    mkdir -p "$classes"
    javac -nowarn -cp "$hollow_jar" -d "$classes" "$here/java/HollowCompat.java"

    say "Netflix Hollow's blobs"

    java -cp "$hollow_jar:$classes" HollowCompat write "$out/java-blobs"

    say "The bytes"

    local status=0

    for blob in snapshot snapshot2 delta reversedelta; do
        if cmp -s "$out/java-blobs/$blob" "$out/dotnet-blobs/$blob"; then
            printf '%-14s identical\n' "$blob"
        else
            printf '%-14s DIFFERS (%s bytes vs %s)\n' "$blob" \
                "$(wc -c < "$out/java-blobs/$blob")" "$(wc -c < "$out/dotnet-blobs/$blob")"
            status=1
        fi
    done

    if [ "$status" -eq 0 ]; then
        echo
        echo "Both implementations wrote the same bytes for the same dataset."
        return 0
    fi

    # Differing bytes prove nothing on their own: the difference may be in the encoding or in the
    # data, and only the second means the two disagree about what the dataset holds. The next two
    # steps say which.
    say "What each side reads back"

    java -cp "$hollow_jar:$classes" HollowCompat describe "$out/java-blobs/snapshot" \
        > "$out/java-snapshot.txt"
    (cd "$dotnet_root" && "${dn[@]}" run -c Release --project tools/Hollow.Compat -- \
        describe "$out/dotnet-blobs/snapshot") > "$out/dotnet-snapshot.txt"

    if diff -u "$out/java-snapshot.txt" "$out/dotnet-snapshot.txt" > "$out/snapshot.diff"; then
        echo "The bytes differ but the records do not, so the difference is in the encoding."
        echo "That is still a format difference, but it is not a disagreement about the data."
    else
        echo "The records differ too. $out/snapshot.diff has the detail; the first few lines:"
        head -20 "$out/snapshot.diff"
    fi

    say "Cross-reading"

    # The strongest check available once the bytes have diverged: Netflix Hollow reads the blob this
    # port wrote. If that works, the file is valid Hollow whatever else is true of it, which is the
    # question that actually matters for interoperability.
    if java -cp "$hollow_jar:$classes" HollowCompat diff \
            "$out/java-blobs/snapshot" "$out/dotnet-blobs/snapshot"; then
        echo "Netflix Hollow reads this port's snapshot and finds the same records in it."
    else
        echo "Netflix Hollow read this port's snapshot and found a difference (above)."
    fi

    return "$status"
}

case "$phase" in
    benchmarks) run_benchmarks ;;
    blobs) run_blobs ;;
    all) run_benchmarks; run_blobs ;;
esac
