/*
 *  Copyright 2016-2019 Netflix, Inc.
 *
 *     Licensed under the Apache License, Version 2.0 (the "License");
 *     you may not use this file except in compliance with the License.
 *     You may obtain a copy of the License at
 *
 *         http://www.apache.org/licenses/LICENSE-2.0
 *
 *     Unless required by applicable law or agreed to in writing, software
 *     distributed under the License is distributed on an "AS IS" BASIS,
 *     WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *     See the License for the specific language governing permissions and
 *     limitations under the License.
 *
 */

using Hollow.Compat;

// Writes the canonical dataset's blobs, describes one, or diffs two — the .NET half of
// tools/compare-with-java.sh. See tools/README.md.
if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine(
        """
        Hollow.Compat — compare this port's blobs against another implementation's.

          write <dir> [--decimal]   Write the canonical dataset's snapshot and delta into <dir>,
                                    with a manifest naming each file's SHA-256.
          describe <blob>           Print what a blob holds, as text a diff(1) can compare.
          diff <from> <to>          Read two snapshots and report what differs, by record.

        --decimal adds this port's own Decimal field type to the dataset. Netflix Hollow's reader
        fails on the schema of such a blob, so it is off by default and the comparison script
        refuses to use it against Java.
        """);

    return args.Length == 0 ? 1 : 0;
}

try
{
    return args[0] switch
    {
        "write" => Commands.Write(Argument(1, "a directory to write into"), args.Contains("--decimal")),
        "describe" => Commands.Describe(Argument(1, "a blob to describe")),
        "diff" => Commands.Diff(Argument(1, "a blob to diff from"), Argument(2, "a blob to diff to")),
        _ => Fail($"unknown command {args[0]}"),
    };
}
catch (Exception failure) when (failure is IOException or InvalidOperationException or ArgumentException)
{
    return Fail(failure.Message);
}

string Argument(int index, string what) =>
    index < args.Length ? args[index] : throw new ArgumentException($"{args[0]} needs {what}");

static int Fail(string message)
{
    Console.Error.WriteLine($"hollow-compat: {message}");

    return 1;
}
