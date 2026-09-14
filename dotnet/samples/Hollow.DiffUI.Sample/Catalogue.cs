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

namespace Hollow.DiffUI.Sample;

// A catalogue of films in two versions, shaped so that every page of the diff has something to show:
// records that changed, records only one side has, a changed field inside a referenced record, and a
// collection whose elements moved.
//
// There is no [HollowGeneratedApi] anywhere here, and no generated client. The diff reads schemas
// rather than classes, so it can be pointed at two blobs whose model this process has never seen.

[HollowPrimaryKey("Id")]
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

[HollowPrimaryKey("Name")]
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

// The two versions the sample compares. A diff is over two whole states, so these are complete
// pictures rather than a list of changes — working out what moved is the diff's job, which is the
// whole point of it.
internal static class Catalogue
{
    private static readonly Studio Warner = new() { Name = "Warner Bros.", Country = "US" };
    private static readonly Studio WarnerMoved = new() { Name = "Warner Bros.", Country = "USA" };
    private static readonly Studio Toho = new() { Name = "Toho", Country = "JP" };
    private static readonly Studio A24 = new() { Name = "A24", Country = "US" };

    private static readonly Actor Keanu = new() { Id = 10, Name = "Keanu Reeves" };
    private static readonly Actor Carrie = new() { Id = 11, Name = "Carrie-Anne Moss" };
    private static readonly Actor Laurence = new() { Id = 12, Name = "Laurence Fishburne" };
    private static readonly Actor Takashi = new() { Id = 13, Name = "Takashi Shimura" };
    private static readonly Actor Michelle = new() { Id = 14, Name = "Michelle Yeoh" };

    /// <summary>The earlier state.</summary>
    internal static IReadOnlyList<Film> Version1 =>
    [
        new Film
        {
            Id = 1,
            Title = "The Matrix",
            ReleaseYear = 1999,
            Studio = Warner,
            Cast = [Keanu, Carrie],
            Tagline = "Welcome to the real world.",
        },
        new Film
        {
            Id = 2,
            Title = "John Wick",
            ReleaseYear = 2014,
            Studio = Warner,
            Cast = [Keanu],
        },
        new Film
        {
            Id = 3,
            Title = "Seven Samurai",
            ReleaseYear = 1954,
            Studio = Toho,
            Cast = [Takashi],
            Tagline = "The mighty warriors who became the seven national heroes of a small town.",
        },
    ];

    /// <summary>
    /// The later state: a title and year changed, a cast member replaced, a country corrected in a
    /// record two films share, one film gone and one arrived.
    /// </summary>
    /// <remarks>
    /// Every one of those is a different thing for the diff to find, and between them they fill each of
    /// the four pages: the overview counts them, the type page groups them, the field page lists the
    /// pairs one field moved in, and the record page draws two records side by side.
    /// </remarks>
    internal static IReadOnlyList<Film> Version2 =>
    [
        new Film
        {
            Id = 1,
            Title = "The Matrix",
            ReleaseYear = 1999,
            Studio = WarnerMoved,
            Cast = [Keanu, Laurence],
            Tagline = "Welcome to the real world.",
        },
        new Film
        {
            Id = 2,
            Title = "John Wick: Chapter 2",
            ReleaseYear = 2017,
            Studio = WarnerMoved,
            Cast = [Keanu],
        },
        new Film
        {
            Id = 4,
            Title = "Everything Everywhere All at Once",
            ReleaseYear = 2022,
            Studio = A24,
            Cast = [Michelle],
            Tagline = "The universe is so much bigger than you realise.",
        },
    ];
}
