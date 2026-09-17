/*
 *  Copyright 2016 Netflix, Inc.
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
using Hollow.Api.Consumer;
using Hollow.Api.Consumer.Index;
using Hollow.Reference.Model.Generated;
using Microsoft.Extensions.Logging;

namespace Hollow.Reference.Consumer;

/// <summary>
/// How the data is used from code, as opposed to looked at in a browser.
/// </summary>
/// <remarks>
/// Ported from <c>Consumer.hereIsHowToUseTheDataProgrammatically</c>. Every type named below —
/// <c>MovieApi</c>, <c>Movie</c>, <c>MoviePrimaryKey</c>, <c>MoviePaths</c> — was written by the source
/// generator from the model in <c>Hollow.Reference.Model</c>, and none of it is checked in.
/// </remarks>
internal static class CatalogueQueries
{
    /// <summary>
    /// Looks a film up by its key, then finds every film each of its cast appears in.
    /// </summary>
    /// <remarks>
    /// Both indexes are built over whatever the consumer currently holds. An application that queries
    /// continuously registers them with the consumer instead, so they follow the data as it moves —
    /// see the generated <c>MovieUniqueKeyIndex</c>, which is an <c>IRefreshListener</c> for exactly
    /// that.
    /// </remarks>
    internal static void Run(HollowConsumer consumer, ILogger logger)
    {
        if (consumer.Api is not MovieApi api)
        {
            logger.LogWarning("The consumer holds no data yet; there is nothing to query.");
            return;
        }

        // The whole catalogue is here; the first film by id is only a film to have one in hand. Java
        // hard-codes 1000004, which is the fifth film of a run that started from an empty store.
        if (api.AllMovie.OrderBy(movie => movie.Id).FirstOrDefault() is not { Id: { } id } first)
        {
            logger.LogWarning("The dataset holds no films.");
            return;
        }

        // Found by its primary key. The key is a record generated from [HollowPrimaryKey("Id")], so the
        // field is named and typed rather than passed as an object in an order only the schema knows.
        // The index behind this is built on first use and kept.
        Movie? found = api.FindMovie(new MoviePrimaryKey(id));

        logger.LogInformation(
            "Film {Id} is {Title}, with {CastSize} in the cast.",
            id,
            found?.Title,
            found?.Actors?.Count ?? 0);

        // An index where a query matches many records rather than one. The path walks into the set
        // through its element step and on into the shared String the name is stored as — which is what
        // makes the query a string rather than an Actor.
        HashIndex<Movie, string> moviesByActorName = HashIndex
            .From<Movie>(consumer)
            .UsingPath(MoviePaths.Movie.Actors.Element.ActorName.Value);

        foreach (Actor actor in ((IEnumerable<Actor>?)found?.Actors ?? []).Take(3))
        {
            if (actor.ActorName is not { } name)
            {
                continue;
            }

            string titles = string.Join(
                ", ",
                moviesByActorName
                    .FindMatches(name)
                    .Select(movie => movie.Title ?? "?")
                    .Order(StringComparer.Ordinal)
                    .Take(5));

            logger.LogInformation("{Actor} starred in {Titles}.", name, titles);
        }
    }

    /// <summary>A line of prose about what the consumer is holding, for the home page.</summary>
    internal static string Describe(HollowConsumer consumer)
    {
        if (consumer.StateEngine is not { } stateEngine)
        {
            return "nothing yet";
        }

        return string.Join(
            ", ",
            stateEngine.TypeStates.Values
                .OrderBy(type => type.Schema.Name, StringComparer.Ordinal)
                .Select(type => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{type.Schema.Name} {type.PopulatedOrdinals.Cardinality()}")));
    }
}
