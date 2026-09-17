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

namespace Hollow.Reference.Model;

/// <summary>
/// Stands in for whatever a real producer reads its data out of, handing back the whole catalogue
/// every time and changing a little of it in between.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>how.hollow.producer.SourceDataRetriever</c>, which lives in the producer package
/// there. It sits with the model here instead, because it is the model's fake database: the tests want
/// it without wanting a console application, and nothing else in the producer is data.
/// </para>
/// <para>
/// A seed may be given, which the Java original has no way to do — it makes a run reproducible, which
/// is what lets a test assert on what a cycle changed rather than only that something did.
/// </para>
/// <para>
/// Not thread-safe: one producer thread drives it, exactly as in the original.
/// </para>
/// </remarks>
public sealed class SourceDataRetriever
{
    private readonly List<Movie> _allMovies = [];
    private readonly List<Actor> _allActors = [];
    private readonly Random _random;

    private int _nextMovieId = 1_000_000;
    private int _nextActorId = 1_000_000;

    /// <param name="actorCount">How many actors to start with. The Java original uses 999.</param>
    /// <param name="movieCount">How many films to start with. The Java original uses 10,000.</param>
    /// <param name="seed">A seed for reproducible runs, or <see langword="null"/> for a random one.</param>
    public SourceDataRetriever(int actorCount = 999, int movieCount = 10_000, int? seed = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(actorCount, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(movieCount);

        _random = seed is { } value ? new Random(value) : new Random();

        for (int i = 0; i < actorCount; i++)
        {
            _allActors.Add(GenerateNewRandomActor());
        }

        for (int i = 0; i < movieCount; i++)
        {
            _allMovies.Add(GenerateNewRandomMovie());
        }
    }

    /// <summary>
    /// Retrieves every film from the source of truth, having first changed a little of it: a few
    /// titles, sometimes an actor's name, a few cast lists, and a handful of films added and removed.
    /// </summary>
    /// <remarks>
    /// The list is the retriever's own, returned rather than copied — the producer only reads it, and
    /// copying ten thousand films a cycle to prove the point would be the only expensive thing here.
    /// </remarks>
    public IReadOnlyList<Movie> RetrieveAllMovies()
    {
        ChangeSomeMovieTitles();
        MaybeChangeAnActorName();
        EditSomeCastLists();
        RemoveSomeMovies();
        AddSomeMovies();

        return _allMovies;
    }

    private void ChangeSomeMovieTitles()
    {
        int count = _random.Next(5);

        for (int i = 0; i < count && _allMovies.Count > 0; i++)
        {
            _allMovies[_random.Next(_allMovies.Count)].Title = GenerateRandomString();
        }
    }

    private void MaybeChangeAnActorName()
    {
        if (_random.Next(5) == 1)
        {
            _allActors[_random.Next(_allActors.Count)].ActorName = GenerateRandomString();
        }
    }

    private void EditSomeCastLists()
    {
        int count = _random.Next(5);

        for (int i = 0; i < count && _allMovies.Count > 0; i++)
        {
            Movie movie = _allMovies[_random.Next(_allMovies.Count)];

            int toRemove = Math.Min(_random.Next(4), movie.Actors.Count);
            int toAdd = _random.Next(4);

            for (int j = 0; j < toRemove; j++)
            {
                // Java takes the iterator's first element and removes it; a HashSet has no defined
                // order either way, so this is the same arbitrary choice.
                movie.Actors.Remove(movie.Actors.First());
            }

            for (int j = 0; j < toAdd; j++)
            {
                movie.Actors.Add(_allActors[_random.Next(_allActors.Count)]);
            }
        }
    }

    private void RemoveSomeMovies()
    {
        int count = _random.Next(3);

        for (int i = 0; i < count && _allMovies.Count > 0; i++)
        {
            _allMovies.RemoveAt(_random.Next(_allMovies.Count));
        }
    }

    private void AddSomeMovies()
    {
        int count = _random.Next(3);

        for (int i = 0; i < count; i++)
        {
            _allMovies.Add(GenerateNewRandomMovie());
        }
    }

    private Actor GenerateNewRandomActor() => new(++_nextActorId, GenerateRandomString());

    private Movie GenerateNewRandomMovie()
    {
        int castSize = _random.Next(25) + 1;
        HashSet<Actor> actors = [];

        for (int i = 0; i < castSize; i++)
        {
            actors.Add(_allActors[_random.Next(_allActors.Count)]);
        }

        return new Movie(++_nextMovieId, GenerateRandomString(), actors);
    }

    private string GenerateRandomString()
    {
        // Java constructs a fresh Random here, which makes the strings correlate with the clock rather
        // than with the seed. Using the one Random is both cheaper and what makes a seeded run repeat.
        int length = _random.Next(20) + 5;

        return string.Create(length, _random, static (characters, random) =>
        {
            for (int i = 0; i < characters.Length; i++)
            {
                characters[i] = (char)(random.Next(26) + 'a');
            }
        });
    }
}
