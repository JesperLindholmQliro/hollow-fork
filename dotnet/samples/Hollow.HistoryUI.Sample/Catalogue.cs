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

using Hollow.Core.Write.ObjectMapper;

namespace Hollow.HistoryUI.Sample;

// A catalogue of films in four versions, shaped so that every page of the history has something to
// show: records that change, records that arrive and go, a change inside a shared record, a
// collection whose elements move, and one record that changes more than once so that following it
// through several versions is worth doing.
//
// There is no [HollowGeneratedApi] anywhere here, and no generated client. A history reads schemas
// rather than classes, so it can follow a delta chain whose model this process has never seen.

// Keyed by id and by the country of the studio that made it. A composite key is what makes the
// state-type page's grouping worth anything: with one field there is nothing to group by.
[HollowPrimaryKey("Id", "Studio.Country")]
public sealed class Film
{
    public required int Id { get; init; }

    public required string Title { get; init; }

    public required int ReleaseYear { get; init; }

    /// <summary>A shared record, so a change inside it shows up under several films at once.</summary>
    public required Studio Studio { get; init; }

    /// <summary>A collection, so the record view has something to pair element by element.</summary>
    public List<Actor> Cast { get; init; } = [];

    /// <summary>Nullable, so a field can arrive or go away rather than only change.</summary>
    public string? Tagline { get; init; }
}

public sealed class Studio
{
    public required string Name { get; init; }

    public required string Country { get; init; }
}

[HollowPrimaryKey("Id")]
public sealed class Actor
{
    public required int Id { get; init; }

    public required string Name { get; init; }
}

// The four versions the history is built across. Each is a complete picture rather than a list of
// changes — working out what moved between them is the history's job.
//
// The versions are clock-stamped, which is how a producer usually numbers them, and is what lets the
// pages show a moment rather than a seventeen-digit number.
internal static class Catalogue
{
    private static readonly Studio Warner = new() { Name = "Warner Bros.", Country = "US" };
    private static readonly Studio WarnerMoved = new() { Name = "Warner Bros.", Country = "USA" };
    private static readonly Studio Toho = new() { Name = "Toho", Country = "JP" };
    private static readonly Studio A24 = new() { Name = "A24", Country = "US" };
    private static readonly Studio Pathe = new() { Name = "Pathe", Country = "FR" };

    private static readonly Actor Keanu = new() { Id = 10, Name = "Keanu Reeves" };
    private static readonly Actor Carrie = new() { Id = 11, Name = "Carrie-Anne Moss" };
    private static readonly Actor Laurence = new() { Id = 12, Name = "Laurence Fishburne" };
    private static readonly Actor Takashi = new() { Id = 13, Name = "Takashi Shimura" };
    private static readonly Actor Michelle = new() { Id = 14, Name = "Michelle Yeoh" };
    private static readonly Actor Audrey = new() { Id = 15, Name = "Audrey Tautou" };

    /// <summary>
    /// The versions, oldest first, each with the catalogue as it stood at that moment.
    /// </summary>
    internal static IReadOnlyList<(long Version, IReadOnlyList<Film> Films)> Versions { get; } =
    [
        // 2024-01-15 09:30 UTC — the starting point. The history is initialised here, so this version
        // has no line of its own on the overview: there is no transition into it to describe.
        (20240115093000000L,
        [
            Matrix(Warner, [Keanu, Carrie]),
            JohnWick("John Wick", 2014, Warner),
            SevenSamurai(),
            Amelie(),
        ]),

        // 2024-01-16 09:30 UTC — Seven Samurai goes, Everything Everywhere arrives, and The Matrix
        // swaps a cast member. One of each thing the overview counts.
        (20240116093000000L,
        [
            Matrix(Warner, [Keanu, Laurence]),
            JohnWick("John Wick", 2014, Warner),
            Everything(),
            Amelie(),
        ]),

        // 2024-01-17 09:30 UTC — one title and year change together, and nothing else moves. This is
        // the version to open when the question is what a single record did.
        (20240117093000000L,
        [
            Matrix(Warner, [Keanu, Laurence]),
            JohnWick("John Wick: Chapter 2", 2017, Warner),
            Everything(),
            Amelie(),
        ]),

        // 2024-01-18 09:30 UTC — a country corrected inside a record two films share. Both of them
        // change, and because the country is part of their key, both change key as well.
        (20240118093000000L,
        [
            Matrix(WarnerMoved, [Keanu, Laurence]),
            JohnWick("John Wick: Chapter 2", 2017, WarnerMoved),
            Everything(),
            Amelie(),
        ]),
    ];

    private static Film Matrix(Studio studio, List<Actor> cast) =>
        new()
        {
            Id = 1,
            Title = "The Matrix",
            ReleaseYear = 1999,
            Studio = studio,
            Cast = cast,
            Tagline = "Welcome to the real world.",
        };

    private static Film JohnWick(string title, int year, Studio studio) =>
        new()
        {
            Id = 2,
            Title = title,
            ReleaseYear = year,
            Studio = studio,
            Cast = [Keanu],
        };

    private static Film SevenSamurai() =>
        new()
        {
            Id = 3,
            Title = "Seven Samurai",
            ReleaseYear = 1954,
            Studio = Toho,
            Cast = [Takashi],
            Tagline = "The mighty warriors who became the seven national heroes of a small town.",
        };

    private static Film Everything() =>
        new()
        {
            Id = 4,
            Title = "Everything Everywhere All at Once",
            ReleaseYear = 2022,
            Studio = A24,
            Cast = [Michelle],
            Tagline = "The universe is so much bigger than you realise.",
        };

    /// <summary>A film that never changes, so that a search can turn up nothing worth showing.</summary>
    private static Film Amelie() =>
        new()
        {
            Id = 5,
            Title = "Amelie",
            ReleaseYear = 2001,
            Studio = Pathe,
            Cast = [Audrey],
        };
}
