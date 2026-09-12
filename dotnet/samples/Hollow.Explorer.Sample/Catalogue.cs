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

namespace Hollow.Explorer.Sample;

// A catalogue of films, shaped so that every page of the explorer has something to show: a type with a
// key and types without, a shared record, a collection, a nullable field, and a type that only turns up
// by following a reference.
//
// There is no [HollowGeneratedApi] anywhere here, and no generated client. The explorer reads schemas
// rather than classes, so it can be pointed at a dataset whose model this process has never seen.

[HollowPrimaryKey("Id")]
public sealed class Film
{
    public required int Id { get; init; }

    public required string Title { get; init; }

    public required int ReleaseYear { get; init; }

    /// <summary>A shared record, so the schema page has a reference to open.</summary>
    public required Studio Studio { get; init; }

    /// <summary>A collection, so the browse page has something nested to lay out.</summary>
    public List<Actor> Cast { get; init; } = [];

    public HashSet<string> Tags { get; init; } = [];

    /// <summary>Nullable, so some records have it and some do not.</summary>
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

// The catalogue as the sample's imaginary upstream system holds it. A producer is given the whole
// dataset each cycle and works out the difference itself, so these are complete pictures.
internal static class Catalogue
{
    private static readonly Studio Warner = new() { Name = "Warner Bros.", Country = "US" };
    private static readonly Studio Svensk = new() { Name = "Svensk Filmindustri", Country = "SE" };
    private static readonly Studio Toho = new() { Name = "Toho", Country = "JP" };

    private static readonly Actor Keanu = new() { Id = 10, Name = "Keanu Reeves" };
    private static readonly Actor Carrie = new() { Id = 11, Name = "Carrie-Anne Moss" };
    private static readonly Actor Liv = new() { Id = 12, Name = "Liv Ullmann" };
    private static readonly Actor Takashi = new() { Id = 13, Name = "Takashi Shimura" };

    /// <summary>The first version of the catalogue.</summary>
    internal static IReadOnlyList<Film> Version1 =>
    [
        new Film
        {
            Id = 1,
            Title = "The Matrix",
            ReleaseYear = 1999,
            Studio = Warner,
            Cast = [Keanu, Carrie],
            Tags = ["science fiction", "action"],
            Tagline = "Welcome to the real world.",
        },
        new Film
        {
            Id = 2,
            Title = "Persona",
            ReleaseYear = 1966,
            Studio = Svensk,
            Cast = [Liv],
            Tags = ["drama"],
        },
        new Film
        {
            Id = 3,
            Title = "Seven Samurai",
            ReleaseYear = 1954,
            Studio = Toho,
            Cast = [Takashi],
            Tags = ["drama", "action"],
        },
    ];

    /// <summary>
    /// The second: one film retitled, one dropped, one added.
    /// </summary>
    /// <remarks>
    /// Publishing this is what the sample's Publish button does, so that the explorer can be watched
    /// following a delta — and so that the hole the dropped film leaves behind shows up on the home
    /// page as a type costing more than its records do.
    /// </remarks>
    internal static IReadOnlyList<Film> Version2 =>
    [
        new Film
        {
            Id = 1,
            Title = "The Matrix (remastered)",
            ReleaseYear = 1999,
            Studio = Warner,
            Cast = [Keanu, Carrie],
            Tags = ["science fiction", "action"],
            Tagline = "Welcome to the real world.",
        },
        new Film
        {
            Id = 3,
            Title = "Seven Samurai",
            ReleaseYear = 1954,
            Studio = Toho,
            Cast = [Takashi],
            Tags = ["drama", "action"],
        },
        new Film
        {
            Id = 4,
            Title = "Rashomon",
            ReleaseYear = 1950,
            Studio = Toho,
            Cast = [Takashi],
            Tags = ["drama", "mystery"],
            Tagline = "Four witnesses. Four truths.",
        },
    ];
}
