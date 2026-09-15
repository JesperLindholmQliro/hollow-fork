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

using Hollow.Api.Consumer;
using Hollow.Api.Consumer.Fs;
using Hollow.Api.Consumer.Index;
using Hollow.Core.Index;
using Hollow.Core.Read.Engine;
using Hollow.Core.Read.Filter;
using Hollow.Core.Read.Iterator;
using Hollow.Core.Types;
using Hollow.Sample.Model.Generated;

namespace Hollow.Sample;

// The reading half. A consumer follows the announced version, applying deltas where it can and falling
// back to a snapshot where it cannot, and hands out a typed client over whatever it currently holds.
// Every type below — CatalogueApi, Movie, MoviePrimaryKey, MovieUniqueKeyIndex — was written by the
// source generator from the model in Catalogue.cs. None of it is checked in.
internal static class Consuming
{
    internal static void ReadTheCatalogue(string blobDirectory, IReadOnlyList<long> versions)
    {
        Output.Act("Consuming");

        // No announcement watcher on this one, so it goes where it is told rather than where the
        // producer has got to. The watcher-driven consumer is the last step below.
        using HollowConsumer consumer = new HollowConsumerBuilder()
            .WithBlobRetriever(new HollowFilesystemBlobRetriever(blobDirectory))

            // Without this the consumer still holds the data; it just has no typed view of it. With it,
            // consumer.Api is a CatalogueApi built afresh whenever the data underneath is replaced.
            .WithApiFactory(new CatalogueApiFactory(CatalogueApi.DefaultCachedTypes))
            .Build();

        Output.Step("Loading the first version");
        consumer.TriggerRefreshTo(versions[0]);
        Output.Say(
            $"version {consumer.CurrentVersionId}, "
            + $"{Output.Number(Api(consumer).AllMovie.Count())} films");

        PrintCatalogue(consumer);
        FollowTheDeltas(consumer, versions);
        DescribeTheBlob(consumer);
        LookUpByKey(consumer);
        Query(consumer);
        SearchByPrefix(consumer);
        ReadWithoutAllocating(consumer);
        ReadPartOfTheData(blobDirectory, versions[^1]);
        FollowTheAnnouncement(blobDirectory, versions[^1]);
    }

    /// <summary>
    /// The typed client over whatever the consumer currently holds. It is replaced whenever the data is,
    /// so it is read rather than held.
    /// </summary>
    private static CatalogueApi Api(HollowConsumer consumer) =>
        (CatalogueApi)(consumer.Api ?? throw new InvalidOperationException("the consumer holds no data"));

    /// <summary>
    /// Every kind of field the model declares, read back through the generated wrappers: scalars stored
    /// in the record, references to shared records, an enum, a list, a set, and a map with a hash key.
    /// </summary>
    private static void PrintCatalogue(HollowConsumer consumer)
    {
        Output.Step("The catalogue, read through the generated client");

        foreach (Movie movie in Api(consumer).AllMovie.OrderBy(movie => movie.Id))
        {
            Output.Say($"{movie.Title} ({movie.ReleaseYear}) · {movie.Rating} · {Output.Money(movie.Budget)}");

            // A reference to a shared record: every Warner Bros. film points at one Studio record.
            Output.Item($"studio: {movie.Studio?.Name} ({movie.Studio?.Country})");

            // A list, in the order it was written.
            Output.Item(
                "cast: " + string.Join(
                    ", ",
                    ((IEnumerable<Actor>?)movie.Cast ?? [])
                        .OrderBy(actor => actor.BilledOrder)
                        .Select(actor => actor.Name)));

            // A set, which has no order of its own, so the sample gives it one.
            Output.Item(
                "tags: " + string.Join(
                    ", ",
                    ((IEnumerable<HString>?)movie.Tags ?? [])
                        .Select(tag => tag.Value)
                        .Order(StringComparer.Ordinal)));

            if (movie.AwardsWon is { Count: > 0 } awards)
            {
                // A map keyed by a record. The hash key on Award.Name is what lets the second lookup be
                // made with a string rather than with a whole Award record.
                Output.Item(
                    "awards: " + string.Join(
                        ", ", awards.Select(won => $"{won.Key.Name} × {won.Value.Value}")));

                Output.Item(
                    "…and by hash key, Academy Award × "
                    + (awards.FindValue("Academy Award")?.Value is { } count
                        ? Output.Number(count)
                        : "none"));
            }

            if (movie.Tagline is { } tagline)
            {
                Output.Item($"tagline: \"{tagline}\"");
            }
        }
    }

    /// <summary>
    /// Following the chain rather than reloading it. A delta is applied in place, so the consumer's
    /// state engine is the same object afterwards — and what the cycle did to the data can be asked of
    /// it directly, because a delta leaves the previous cycle's ordinals behind.
    /// </summary>
    private static void FollowTheDeltas(HollowConsumer consumer, IReadOnlyList<long> versions)
    {
        Output.Step("Following the delta chain");

        HollowReadStateEngine before = consumer.StateEngine!;

        consumer.TriggerRefreshTo(versions[1]);

        Output.Say(
            $"version {consumer.CurrentVersionId}, "
            + (ReferenceEquals(before, consumer.StateEngine)
                ? "reached by applying a delta in place"
                : "reached by loading a whole snapshot"));

        DescribeTheChange(consumer);

        Output.Say($"on to the incremental cycle, {versions[2]}");
        consumer.TriggerRefreshTo(versions[2]);

        Output.Say(
            $"version {consumer.CurrentVersionId}, "
            + $"{Output.Number(Api(consumer).AllMovie.Count())} films");

        DescribeTheChange(consumer);
    }

    private static void DescribeTheChange(HollowConsumer consumer)
    {
        RecordChangeSet changes = RecordChangeSet.Compute(consumer.StateEngine!, "Movie");
        CatalogueApi api = Api(consumer);

        foreach (int ordinal in changes.Added.EnumerateSetBits())
        {
            Output.Item($"added: {api.GetMovie(ordinal)?.Title}");
        }

        foreach (int ordinal in changes.Removed.EnumerateSetBits())
        {
            // Still readable: the record is gone from the current version, but a delta keeps the
            // previous one's records around until the ordinals are reused.
            Output.Item($"removed: {api.GetMovie(ordinal)?.Title}");
        }

        foreach (UpdatedRecord updated in changes.Updated)
        {
            Output.Item(
                $"changed: {api.GetMovie(updated.FromOrdinal)?.Title} "
                + $"→ {api.GetMovie(updated.ToOrdinal)?.Title}");
        }
    }

    /// <summary>
    /// What the blob says about itself: the header tags the producer wrote, and how much of each type
    /// the consumer is holding.
    /// </summary>
    private static void DescribeTheBlob(HollowConsumer consumer)
    {
        Output.Step("What the data says about itself");

        HollowReadStateEngine stateEngine = consumer.StateEngine!;

        foreach (KeyValuePair<string, string> tag in stateEngine.HeaderTags
            .OrderBy(tag => tag.Key, StringComparer.Ordinal))
        {
            Output.Item($"{tag.Key} = {tag.Value}");
        }

        Output.Say(
            "records: " + string.Join(
                ", ",
                stateEngine.TypeStates.Values
                    .OrderBy(type => type.Schema.Name, StringComparer.Ordinal)
                    .Select(type =>
                        $"{type.Schema.Name} {Output.Number(type.PopulatedOrdinals.Cardinality())}")));
    }

    /// <summary>
    /// Four ways to find one record by its key, from the shortest to the most explicit.
    /// </summary>
    private static void LookUpByKey(HollowConsumer consumer)
    {
        Output.Step("Finding one record by its key");

        CatalogueApi api = Api(consumer);

        // The shortest: the API does it itself. The key is a record generated from
        // [HollowPrimaryKey("Id")], so the field is named and typed rather than passed as an object in
        // an order only the schema knows. The index behind this is built on first use and kept.
        Output.Item($"api.FindMovie(new MoviePrimaryKey(5)) → {api.FindMovie(new MoviePrimaryKey(5))?.Title}");

        // A key of a different type, on a different type, resolved through the reference into the
        // shared String type — so it is a string, not an object.
        Output.Item(
            "api.FindStudio(new StudioPrimaryKey(\"Toho\")) → "
            + $"{api.FindStudio(new StudioPrimaryKey("Toho"))?.Country}");

        Output.Item($"a key that matches nothing → {api.FindMovie(new MoviePrimaryKey(99))?.Title ?? "null"}");

        // The standalone index, for an application that wants the build to happen on refresh rather
        // than on the first lookup. Registered with the consumer, it follows the data.
        using MovieUniqueKeyIndex byId = new(consumer);
        consumer.AddRefreshListener(byId);

        Output.Item($"the standalone index, id 5 → {byId.FindMatch(new MoviePrimaryKey(5))?.Title}");

        // An index on a path the model does not declare as unique, for when the key you have is not
        // the key the producer enforces. The path is a value rather than a string: the compiler wrote
        // it from the model, and the index takes its key type from where the path arrives.
        using UniqueKeyIndex<Movie, string> byTitle =
            UniqueKeyIndex.From<Movie>(consumer).UsingPath(CataloguePaths.Movie.Title.Value);

        Output.Item($"by title, \"Casablanca\" → released {byTitle.FindMatch("Casablanca")?.ReleaseYear}");

        // And what all of them are built on, which returns an ordinal rather than a record.
        using HollowPrimaryKeyIndex raw = new(consumer.StateEngine!, CataloguePaths.Movie.Id);

        Output.Item($"the core index, id 5 → ordinal {Output.Number(raw.GetMatchingOrdinal(5))}");
    }

    /// <summary>
    /// A hash index, where a query matches many records rather than one — and can return something
    /// other than the record it matched against.
    /// </summary>
    private static void Query(HollowConsumer consumer)
    {
        Output.Step("Finding many records by a value");

        // Every film carrying a tag. The path walks into the set, so one film with three tags is found
        // by any of them — and it walks there through Element, which is the set's own step.
        HashIndex<Movie, string> byTag =
            HashIndex.From<Movie>(consumer).UsingPath(CataloguePaths.Movie.Tags.Element.Value);

        foreach (string tag in (string[])["science fiction", "drama", "animation"])
        {
            Output.Item(
                $"tagged \"{tag}\": "
                + string.Join(
                    ", ",
                    byTag.FindMatches(tag).Select(movie => movie.Title).Order(StringComparer.Ordinal)));
        }

        // Matching on one type and returning another: query by studio, get the actors. The select path
        // names a record rather than a value, which is what makes the result an Actor.
        HashIndexSelect<Movie, Actor, string> castByStudio = HashIndex.From<Movie>(consumer)
            .SelectField(CataloguePaths.Movie.Cast.Element)
            .UsingPath(CataloguePaths.Movie.Studio.Name.Value);

        Output.Item(
            "everyone billed in a Toho film: "
            + string.Join(
                ", ",
                castByStudio.FindMatches("Toho").Select(actor => actor.Name).Order(StringComparer.Ordinal)));
    }

    /// <summary>
    /// Prefix search, which neither of the hash-based indexes can do: they match a whole value, this
    /// matches the start of one.
    /// </summary>
    private static void SearchByPrefix(HollowConsumer consumer)
    {
        Output.Step("Searching titles by prefix");

        // The tokenizer decides what a prefix is a prefix of. Splitting titles into words is what makes
        // "away" match "Spirited Away" rather than only titles that begin with it.
        using HollowPrefixIndex titles = new(
            consumer.StateEngine!,
            CataloguePaths.Movie.Title.Value,
            tokenizer: keys => keys.SelectMany(key => key.Split(' ', StringSplitOptions.RemoveEmptyEntries)));

        CatalogueApi api = Api(consumer);

        foreach (string prefix in (string[])["the", "away", "z"])
        {
            List<string> matches = [];

            foreach (int ordinal in titles.FindKeysWithPrefix(prefix))
            {
                matches.Add(api.GetMovie(ordinal)?.Title ?? "?");
            }

            Output.Item(
                $"\"{prefix}\": "
                + (matches.Count > 0 ? string.Join(", ", matches.Order(StringComparer.Ordinal)) : "no match"));
        }
    }

    /// <summary>
    /// Reading a string out of the blob without building one. The characters are already there, encoded
    /// in the record; a caller that only wants to compare or copy them does not need an allocation.
    /// </summary>
    private static void ReadWithoutAllocating(HollowConsumer consumer)
    {
        Output.Step("Reading a string without allocating one");

        Movie movie = Api(consumer).AllMovie.OrderBy(each => each.Id).First();
        HString title = movie.TitleRecord!;

        Span<char> buffer = stackalloc char[64];
        ReadOnlySpan<char> characters = title.GetString("value", buffer);

        Output.Item(
            $"{Output.Number(characters.Length)} characters read into a stack buffer: \"{characters}\"");

        Output.Item($"…and the same field compared in place: {title.IsValueEqual(movie.Title)}");
    }

    /// <summary>
    /// A consumer can decline to load part of the dataset. What it declines is then genuinely absent
    /// rather than empty, and the generated client says so instead of failing.
    /// </summary>
    private static void ReadPartOfTheData(string blobDirectory, long version)
    {
        Output.Step("Reading only part of the dataset");

        using HollowConsumer consumer = new HollowConsumerBuilder()
            .WithBlobRetriever(new HollowFilesystemBlobRetriever(blobDirectory))
            .WithApiFactory(new CatalogueApiFactory())
            .WithTypeFilter(TypeFilter.Include(["Movie", "String", "Studio", "Rating", "SetOfString"]))
            .Build();

        consumer.TriggerRefreshTo(version);

        CatalogueApi api = Api(consumer);

        Output.Say("loaded types: Movie, String, Studio, Rating, SetOfString");
        Output.Item($"Studio present: {api.StudioTypeApi.IsTypePresent}");
        Output.Item($"Award present: {api.AwardTypeApi.IsTypePresent}");

        Movie movie = api.AllMovie.OrderBy(each => each.Id).First();

        Output.Item($"a film still reads: {movie.Title}, {movie.Studio?.Name}");

        // The awards map was left out too, so this wrapper stands over data that is not there. It reads
        // as empty rather than throwing, which is what lets one generated client serve consumers that
        // each load a different part of the same dataset.
        Output.Item(
            $"its awards: {Output.Number(movie.AwardsWon?.Count ?? -1)}, from a type that is "
            + (api.MapOfAwardToIntegerTypeApi.IsTypePresent ? "present" : "absent"));
    }

    /// <summary>
    /// What a consumer does when nobody tells it which version to be on: it watches for announcements
    /// and follows them, which is how one runs in production.
    /// </summary>
    private static void FollowTheAnnouncement(string blobDirectory, long announced)
    {
        Output.Step("Following the announcement instead of a version");

        using HollowFilesystemAnnouncementWatcher watcher = new(blobDirectory);

        using HollowConsumer consumer = new HollowConsumerBuilder()
            .WithBlobRetriever(new HollowFilesystemBlobRetriever(blobDirectory))
            .WithAnnouncementWatcher(watcher)
            .WithApiFactory(new CatalogueApiFactory(CatalogueApi.DefaultCachedTypes))
            .Build();

        consumer.TriggerRefresh();

        Output.Say(
            $"the watcher found version {consumer.CurrentVersionId}, which is "
            + (consumer.CurrentVersionId == announced ? "the announced one" : "not what was announced"));

        Output.Item(
            $"{Output.Number(Api(consumer).AllMovie.Count())} films, and a refusal to be moved by hand:");

        try
        {
            consumer.TriggerRefreshTo(announced - 1);
        }
        catch (NotSupportedException e)
        {
            Output.Item(e.Message);
        }
    }
}
