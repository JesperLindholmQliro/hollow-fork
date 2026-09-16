#!/usr/bin/env python3
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
"""Joins a JMH result file against this port's and prints the two side by side.

Both files are in JMH's shape, which is why there is one parser here rather than two: the .NET
harness writes that shape on purpose (see JmhJson.cs).

The names do not line up on their own. Java's are fully-qualified class names with a method on the
end; this port's are suite and case. SUITES below maps one onto the other, and is the same mapping
the benchmarks README documents in prose. A benchmark with no counterpart is listed rather than
dropped, because that usually means the mapping is out of date rather than that nobody ported it.
"""

import json
import sys

# Java simple class name -> this port's suite name.
SUITES = {
    "HashCodesBenchmark": "HashCodes",
    "OrdinalMapResize": "OrdinalMapResize",
    "ReadWriteFixedLengthElementArrayTest": "FixedLengthElementArray",
    "HollowPrimaryKeyIndexBenchmark": "HollowPrimaryKeyIndex",
    "HollowHashIndexBenchmark": "HollowHashIndex",
    "HollowObjectTypeReadStateLongBenchmark": "HollowObjectTypeReadStateLong",
    "HollowObjectTypeReadStateShardBenchmark": "HollowObjectTypeReadStateShard",
    "CheckSumCollections": "ChecksumCollections",
    "ReadWriteStateEngineTest": "ReadWriteStateEngine",
    "DuplicateDataDetectionValidatorBenchmark": "DuplicateDataDetection",
    "HollowObjectTypeReadStateDeltaTransitionBenchmark": "ObjectTypeReadStateDeltaTransition",
}

# JMH reports in whatever unit the benchmark declared; this port always writes nanoseconds.
TO_NANOSECONDS = {"ns/op": 1.0, "us/op": 1e3, "ms/op": 1e6, "s/op": 1e9}


def load(path):
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def nanoseconds(entry):
    metric = entry["primaryMetric"]
    unit = metric.get("scoreUnit", "ns/op")

    if unit not in TO_NANOSECONDS:
        raise SystemExit(f"{entry['benchmark']}: cannot compare a result in {unit}")

    return metric["score"] * TO_NANOSECONDS[unit]


def parameters(entry):
    """A stable string for the @Params, so two sides match on them as well as on the name."""
    params = entry.get("params") or {}

    return ", ".join(f"{name}={params[name]}" for name in sorted(params))


def java_key(entry):
    """`com.netflix...HashCodesBenchmark.hashInt` -> `("HashCodes", "hashint")`."""
    parts = entry["benchmark"].split(".")

    if len(parts) < 2:
        return None

    suite = SUITES.get(parts[-2])

    return None if suite is None else (suite, parts[-1].lower())


def dotnet_key(entry):
    """`HashCodes.HashInt` -> `("HashCodes", "hashint")`."""
    suite, _, case = entry["benchmark"].rpartition(".")

    return (suite, case.lower()) if suite else None


def index(path, key_of):
    indexed = {}

    for entry in load(path):
        key = key_of(entry)

        if key is not None:
            indexed[(key, parameters(entry))] = entry

    return indexed


def main():
    if len(sys.argv) != 3:
        raise SystemExit("usage: compare-benchmarks.py <java.json> <dotnet.json>")

    java = index(sys.argv[1], java_key)
    ported = index(sys.argv[2], dotnet_key)

    print(f"{'Benchmark':<50} {'(params)':<26} {'Java':>13} {'.NET':>13} {'ratio':>8}")

    matched = 0

    for key in sorted(java.keys() & ported.keys()):
        java_score = nanoseconds(java[key])
        dotnet_score = nanoseconds(ported[key])

        # .NET over Java: above 1.00 means this port is slower. Stated rather than left implicit,
        # because the opposite convention is just as common and reading it the wrong way round turns
        # a win into a loss.
        ratio = dotnet_score / java_score if java_score else float("inf")

        print(
            f"{ported[key]['benchmark']:<50} {key[1]:<26} "
            f"{java_score:>13,.1f} {dotnet_score:>13,.1f} {ratio:>8.2f}"
        )
        matched += 1

    print()
    print(f"{matched} case(s) compared, ns/op, ratio is .NET over Java (above 1.00 is slower here).")

    for label, source, only in (
        ("Java only", java, java.keys() - ported.keys()),
        ("This port only", ported, ported.keys() - java.keys()),
    ):
        if only:
            print()
            print(f"{label}:")

            # The entry's own name rather than the match key, which is lowercased and would print
            # names neither side actually uses.
            for key in sorted(only):
                print(f"  {source[key]['benchmark']} {key[1]}".rstrip())


if __name__ == "__main__":
    main()
