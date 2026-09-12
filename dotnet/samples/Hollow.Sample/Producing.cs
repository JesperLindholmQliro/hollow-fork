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

using Hollow.Api.Producer;
using Hollow.Api.Producer.Fs;
using Hollow.Api.Producer.Listener;
using Hollow.Api.Producer.Validation;
using Hollow.Core.Schema;
using Hollow.Core.Write.ObjectMapper;
using Hollow.Sample.Model;

// The generated client's wrapper is also called Movie, since it is named after the type it reads. The
// producer writes the model class and reads back the wrapper, so this file needs both.
using CatalogueApi = Hollow.Sample.Model.Generated.CatalogueApi;
using MovieRecord = Hollow.Sample.Model.Generated.Movie;

namespace Hollow.Sample;

// The writing half. A producer is handed the whole dataset each cycle, writes a snapshot and the deltas
// to and from the version before it, reads its own blobs back to check they agree, runs the validators,
// and only then announces the version. Everything below is one of those steps being exercised.
internal static class Producing
{
    /// <summary>
    /// Publishes four cycles into <paramref name="blobDirectory"/> and returns the versions that were
    /// announced, in order.
    /// </summary>
    internal static IReadOnlyList<long> PublishTheCatalogue(string blobDirectory)
    {
        Output.Act("Producing");

        HollowProducer producer = new HollowProducerBuilder()
            .WithPublisher(new HollowFilesystemPublisher(blobDirectory))
            .WithAnnouncer(new HollowFilesystemAnnouncer(blobDirectory))
            .WithBlobStagingDirectory(Path.Combine(blobDirectory, "staging"))
            .WithListeners(new CyclePrinter())
            .WithValidators(
                // Two records of one film is a bug upstream, not a catalogue.
                new DuplicateDataDetectionValidator("Movie"),

                // A catalogue this small is a sign the upstream feed failed, whatever it said.
                new MinimumRecordCountValidator("Movie", 3),

                // Replacing every film at once is not an edit, it is an accident.
                new RecordCountPercentChangeValidator(
                    "Movie", ChangeThreshold.Create().WithRemoved(0.5f).WithUpdated(0.9f).Build()),

                // A film without an id cannot be looked up by the thing the model says identifies it.
                new NullPrimaryKeyFieldValidator(typeof(Movie)),

                // And the one rule about the transition rather than the state: a film may be retitled,
                // as cycle 2 retitles The Matrix, but it may not move to another year. Reading the old
                // and the new record is what the generated client is for — on this side of the wire too.
                new ObjectModificationValidator<MovieRecord>(
                    "Movie",
                    (before, after) => before.ReleaseYear == after.ReleaseYear,
                    (dataAccess, ordinal) => new CatalogueApi(dataAccess).GetMovie(ordinal)!))
            .Build();

        // Registering the model up front means the schemas exist before any record does, so the first
        // cycle publishes the full shape of the data even where a type happens to have no records yet.
        producer.InitializeDataModel(typeof(Movie));
        PrintSchemas(producer);

        List<long> versions = [];

        Output.Step("Cycle 1 — the first version of the catalogue, published as a snapshot");
        versions.Add(producer.RunCycle(WholeCatalogue(CatalogueData.Version1)));

        Output.Step("Cycle 2 — one film retitled, one dropped, one added, published as a delta");
        versions.Add(producer.RunCycle(WholeCatalogue(CatalogueData.Version2)));

        Output.Step("Cycle 3 — an incremental cycle, which restates nothing");
        versions.Add(Incremental(producer));

        // Last, because a refused cycle's blobs are written and published before the validators ever
        // run — only the announcement is withheld. Putting it in the middle would leave a second delta
        // hanging off the version before it, and a consumer can only follow one.
        Output.Step("Cycle 4 — a cycle the validators refuse");
        RefusedCycle(producer);

        Output.Step("Published");
        Output.Say($"announced versions: {string.Join(" → ", versions)}");

        foreach (IGrouping<string, string> kind in BlobNames(blobDirectory)
            .GroupBy(name => name.Split('-')[0], StringComparer.Ordinal))
        {
            Output.Item($"{Output.Number(kind.Count())} × {kind.Key}");
        }

        return versions;
    }

    /// <summary>
    /// A populator describes the whole state rather than the change: add every record the version
    /// should hold and the producer works out the difference from the version before it.
    /// </summary>
    private static Populator WholeCatalogue(IReadOnlyList<Movie> movies) =>
        state =>
        {
            foreach (Movie movie in movies)
            {
                state.Add(movie);
            }
        };

    /// <summary>
    /// An incremental cycle reports only what changed. The producer starts from the previous version's
    /// records and applies the changes to them, so a dataset of millions can be edited by the handful.
    /// </summary>
    private static long Incremental(HollowProducer producer) =>
        producer.RunIncrementalCycle(state =>
        {
            state.AddOrModify(CatalogueData.Addition);

            // By key rather than by record: a deletion does not need the thing being deleted.
            state.Delete(new RecordPrimaryKey("Movie", 2));
        });

    /// <summary>
    /// A cycle whose data breaks a rule is written and read back like any other — and then not
    /// announced. Consumers stay on the version before it, and the producer carries on from there.
    /// </summary>
    private static void RefusedCycle(HollowProducer producer)
    {
        try
        {
            producer.RunCycle(WholeCatalogue([CatalogueData.Addition]));

            Output.Say("the cycle was announced, which it should not have been");
        }
        catch (ValidationStatusException e)
        {
            foreach (ValidationResult result in e.ValidationStatus.Results.Where(r => !r.IsPassed))
            {
                Output.Item($"{result.Name}: {result.Message}");
            }

            Output.Note("nothing was announced, so consumers never saw this version");
        }
    }

    private static void PrintSchemas(HollowProducer producer)
    {
        Output.Step("The schemas the model maps to");

        foreach (HollowSchema schema in producer.WriteEngine.OrderedTypeStates
            .Select(state => state.Schema)
            .OrderBy(schema => schema.Name, StringComparer.Ordinal))
        {
            // The same text the code generator reads, and the same text a .hollow schema file holds.
            Output.Item(schema.ToString()!.ReplaceLineEndings(" ").Replace("  ", " ", StringComparison.Ordinal));
        }
    }

    private static IEnumerable<string> BlobNames(string blobDirectory) =>
        Directory.EnumerateFiles(blobDirectory)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => name!)
            .Order(StringComparer.Ordinal);

    /// <summary>
    /// A listener sees every step of a cycle. Java's producer reports through the same interfaces; this
    /// one prints them so the sample shows the order they happen in.
    /// </summary>
    private sealed class CyclePrinter : HollowProducerListener
    {
        public override void OnCycleStart(long version) => Output.Say($"cycle {version} started");

        public override void OnBlobPublish(Status status, Blob blob, TimeSpan elapsed) =>
            Output.Item($"published {blob.BlobType} blob for version {blob.ToVersion}");

        public override void OnValidationStatusComplete(
            ValidationStatus status, long version, TimeSpan elapsed) =>
            Output.Item(
                $"{Output.Number(status.Results.Count)} validators ran, "
                + (status.Passed ? "all passed" : "and the version was refused"));

        public override void OnAnnouncementComplete(
            Status status, IReadState? readState, long version, TimeSpan elapsed) =>
            Output.Say($"version {version} announced");
    }
}
