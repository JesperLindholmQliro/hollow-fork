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

using Hollow.Sample.Model;

namespace Hollow.Sample;

// The catalogue as the sample's imaginary upstream system holds it. A producer is given the whole
// dataset each cycle and works out the difference itself, so these are complete pictures rather than
// changes.
internal static class CatalogueData
{
    private static readonly Studio Warner = new() { Name = "Warner Bros.", Country = "US" };
    private static readonly Studio Svensk = new() { Name = "Svensk Filmindustri", Country = "SE" };
    private static readonly Studio Toho = new() { Name = "Toho", Country = "JP" };

    private static readonly Award Oscar = new() { Name = "Academy Award", Category = "Best Picture" };
    private static readonly Award Palme = new() { Name = "Palme d'Or", Category = "Best Film" };

    /// <summary>The first version of the catalogue.</summary>
    internal static IReadOnlyList<Movie> Version1 =>
    [
        new Movie
        {
            Id = 1,
            Title = "The Matrix",
            ReleaseYear = 1999,
            Budget = 63_000_000m,
            Rating = Model.Rating.R,
            Studio = Warner,
            Cast =
            [
                new Actor { Id = 10, Name = "Keanu Reeves", BilledOrder = 1 },
                new Actor { Id = 11, Name = "Carrie-Anne Moss", BilledOrder = 2 },
            ],
            Tags = ["science fiction", "action"],
            Tagline = "Welcome to the real world.",
        },
        new Movie
        {
            Id = 2,
            Title = "Persona",
            ReleaseYear = 1966,
            Budget = 150_000m,
            Rating = Model.Rating.R,
            Studio = Svensk,
            Cast =
            [
                new Actor { Id = 20, Name = "Bibi Andersson", BilledOrder = 1 },
                new Actor { Id = 21, Name = "Liv Ullmann", BilledOrder = 2 },
            ],
            Tags = ["drama"],
            AwardsWon = { [Palme] = 1 },
        },
        new Movie
        {
            Id = 3,
            Title = "Seven Samurai",
            ReleaseYear = 1954,
            Budget = 500_000m,
            Rating = Model.Rating.PG13,
            Studio = Toho,
            Cast = [new Actor { Id = 30, Name = "Toshiro Mifune", BilledOrder = 1 }],
            Tags = ["drama", "action"],
        },
        new Movie
        {
            Id = 4,
            Title = "Casablanca",
            ReleaseYear = 1942,
            Budget = 1_000_000m,
            Rating = Model.Rating.PG,
            Studio = Warner,
            Cast = [new Actor { Id = 40, Name = "Humphrey Bogart", BilledOrder = 1 }],
            Tags = ["drama", "romance"],
            AwardsWon = { [Oscar] = 3 },
            Tagline = "They had a date with fate in Casablanca!",
        },
    ];

    /// <summary>
    /// The second version: one film retitled, one dropped, one added, and one left exactly alone. Four
    /// different kinds of change, so a consumer following the delta has all of them to observe.
    /// </summary>
    internal static IReadOnlyList<Movie> Version2 =>
    [
        // Same key, different title — a modification rather than a removal and an addition.
        new Movie
        {
            Id = 1,
            Title = "The Matrix (Remastered)",
            ReleaseYear = 1999,
            Budget = 63_000_000m,
            Rating = Model.Rating.R,
            Studio = Warner,
            Cast =
            [
                new Actor { Id = 10, Name = "Keanu Reeves", BilledOrder = 1 },
                new Actor { Id = 11, Name = "Carrie-Anne Moss", BilledOrder = 2 },
            ],
            Tags = ["science fiction", "action"],
            Tagline = "Welcome to the real world.",
        },
        new Movie
        {
            Id = 2,
            Title = "Persona",
            ReleaseYear = 1966,
            Budget = 150_000m,
            Rating = Model.Rating.R,
            Studio = Svensk,
            Cast =
            [
                new Actor { Id = 20, Name = "Bibi Andersson", BilledOrder = 1 },
                new Actor { Id = 21, Name = "Liv Ullmann", BilledOrder = 2 },
            ],
            Tags = ["drama"],
            AwardsWon = { [Palme] = 1 },
        },

        // Seven Samurai is gone.

        new Movie
        {
            Id = 4,
            Title = "Casablanca",
            ReleaseYear = 1942,
            Budget = 1_000_000m,
            Rating = Model.Rating.PG,
            Studio = Warner,
            Cast = [new Actor { Id = 40, Name = "Humphrey Bogart", BilledOrder = 1 }],
            Tags = ["drama", "romance"],
            AwardsWon = { [Oscar] = 3 },
            Tagline = "They had a date with fate in Casablanca!",
        },
        new Movie
        {
            Id = 5,
            Title = "Spirited Away",
            ReleaseYear = 2001,
            Budget = 19_000_000m,
            Rating = Model.Rating.PG,
            Studio = Toho,
            Cast = [new Actor { Id = 50, Name = "Rumi Hiiragi", BilledOrder = 1 }],
            Tags = ["animation", "fantasy"],
            AwardsWon = { [Oscar] = 1 },
        },
    ];

    /// <summary>A film to add through the incremental producer, without restating the rest.</summary>
    internal static Movie Addition =>
        new()
        {
            Id = 6,
            Title = "Stalker",
            ReleaseYear = 1979,
            Budget = 1_000_000m,
            Rating = Model.Rating.PG,
            Studio = Svensk,
            Cast = [new Actor { Id = 60, Name = "Alexander Kaidanovsky", BilledOrder = 1 }],
            Tags = ["science fiction", "drama"],
        };
}
