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

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Hollow.Core.Read.Engine;
using Hollow.Core.Schema;
using Hollow.Core.Tools.Diff;
using Hollow.Core.Tools.Stringifier;
using Hollow.Core.Write;

namespace Hollow.Compat;

/// <summary>What the three subcommands do.</summary>
internal static class Commands
{
    /// <summary>
    /// Writes the canonical dataset's blobs, and a manifest naming each file's digest.
    /// </summary>
    internal static int Write(string directory, bool withDecimal)
    {
        Directory.CreateDirectory(directory);

        HollowWriteStateEngine engine = CanonicalDataset.FirstCycle(withDecimal);
        engine.PrepareForWrite();

        HollowBlobWriter writer = new(engine);

        string snapshot = Path.Combine(directory, "snapshot");
        WriteTo(snapshot, writer.WriteSnapshot);

        CanonicalDataset.SecondCycle(engine, withDecimal);
        engine.PrepareForWrite();

        string secondSnapshot = Path.Combine(directory, "snapshot2");
        string delta = Path.Combine(directory, "delta");
        string reverseDelta = Path.Combine(directory, "reversedelta");

        WriteTo(secondSnapshot, writer.WriteSnapshot);
        WriteTo(delta, writer.WriteDelta);
        WriteTo(reverseDelta, writer.WriteReverseDelta);

        JsonObject digests = [];

        foreach (string file in new[] { snapshot, secondSnapshot, delta, reverseDelta })
        {
            byte[] bytes = File.ReadAllBytes(file);
            string digest = Convert.ToHexString(SHA256.HashData(bytes));

            digests[Path.GetFileName(file)] = new JsonObject
            {
                ["bytes"] = bytes.Length,
                ["sha256"] = digest,
            };

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{Path.GetFileName(file),-14} {bytes.Length,10} bytes  {digest}"));
        }

        File.WriteAllText(
            Path.Combine(directory, "manifest.json"),
            new JsonObject
            {
                ["implementation"] = ".NET",
                ["decimal"] = withDecimal,
                ["records"] = CanonicalDataset.Records,
                ["files"] = digests,
            }.ToString());

        return 0;
    }

    /// <summary>
    /// Prints what a snapshot holds, as text two implementations can be compared on with
    /// <c>diff(1)</c>.
    /// </summary>
    /// <remarks>
    /// The fallback for when the bytes differ. Identical bytes prove the two agree; differing bytes
    /// prove nothing on their own, because the difference may be in the encoding or in the data, and
    /// only the second means they disagree about what the dataset holds. This says which.
    /// </remarks>
    internal static int Describe(string blob)
    {
        HollowReadStateEngine engine = Read(blob);
        HollowRecordJsonStringifier stringifier = new(prettyPrint: false);

        // By name, so that neither side's declaration order decides the output.
        HollowSchema[] schemas = [.. engine.Schemas.OrderBy(schema => schema.Name, StringComparer.Ordinal)];

        foreach (HollowSchema schema in schemas)
        {
            Console.WriteLine(schema.ToString());
        }

        Console.WriteLine();

        foreach (HollowSchema schema in schemas)
        {
            HollowTypeReadState state = engine.GetTypeState(schema.Name)!;

            foreach (int ordinal in state.PopulatedOrdinals.EnumerateSetBits())
            {
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{schema.Name}[{ordinal}] {stringifier.Stringify(engine, schema.Name, ordinal)}"));
            }
        }

        return 0;
    }

    /// <summary>Reports what differs between two snapshots, by record rather than by byte.</summary>
    internal static int Diff(string from, string to)
    {
        // Non-keyed types too: a single-field wrapper like String gets keyed on its one field, and
        // leaving it out would pass over most of the records in a typical dataset.
        HollowDiff diff = new(Read(from), Read(to), includeNonPrimaryKeyTypes: true);
        diff.CalculateDiffs();

        long score = 0;
        int unmatched = 0;

        foreach (HollowTypeDiff typeDiff in
            diff.TypeDiffs.OrderBy(typeDiff => typeDiff.TypeName, StringComparer.Ordinal))
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{typeDiff.TypeName}: {typeDiff.TotalItemsInFromState} -> {typeDiff.TotalItemsInToState}, "
                + $"{typeDiff.TotalNumberOfMatches} matched, "
                + $"{typeDiff.UnmatchedOrdinalsInFrom.Count} only in from, "
                + $"{typeDiff.UnmatchedOrdinalsInTo.Count} only in to"));

            foreach (HollowFieldDiff fieldDiff in
                typeDiff.FieldDiffs.OrderByDescending(fieldDiff => fieldDiff.TotalDiffScore))
            {
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"    {fieldDiff.FieldIdentifier}: {fieldDiff.NumDiffs} record(s) differ"));
            }

            score += typeDiff.TotalDiffScore;
            unmatched += typeDiff.UnmatchedOrdinalsInFrom.Count + typeDiff.UnmatchedOrdinalsInTo.Count;
        }

        Console.WriteLine();

        // A score of zero means no matched record differs in any field. It says nothing about records
        // that are only on one side, and reporting "the same" on the strength of it alone would be
        // wrong for exactly the case this tool exists to catch.
        Console.WriteLine(
            score == 0 && unmatched == 0
                ? "The two states hold the same records."
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"Total diff score {score}, with {unmatched} record(s) on one side only."));

        // The exit status is the answer, so that a script can branch on it without parsing this.
        return score == 0 && unmatched == 0 ? 0 : 2;
    }

    private static void WriteTo(string path, Action<Stream> write)
    {
        using FileStream stream = File.Create(path);
        write(stream);
    }

    private static HollowReadStateEngine Read(string blob)
    {
        HollowReadStateEngine engine = new();

        using FileStream stream = File.OpenRead(blob);
        new HollowBlobReader(engine).ReadSnapshot(stream);

        return engine;
    }
}
