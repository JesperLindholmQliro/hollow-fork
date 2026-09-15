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

namespace Hollow.FakeData;

/// <summary>
/// The words the fake catalogue is made of, and the one random number generator behind all of them.
/// </summary>
/// <remarks>
/// <para>
/// Java uses javafaker. This is a few word lists instead, for two reasons. The generator has no
/// business pulling a dependency in to produce strings nobody reads, and — more to the point — every
/// draw goes through <em>one</em> seeded <see cref="Random"/>, so the same seed gives the same
/// catalogue. Java calls <c>new Random()</c> at each use site, which means its dataset cannot be
/// reproduced: a bug you find in the explorer at cycle 60 is gone the next time you look.
/// </para>
/// <para>
/// Not thread-safe, and not meant to be: the generator runs on the cycle loop.
/// </para>
/// </remarks>
internal sealed class FakeText(int seed)
{
    private static readonly string[] Forenames =
    [
        "Ada", "Bram", "Cecile", "Dmitri", "Esme", "Farid", "Greta", "Hallur", "Ines", "Jorge",
        "Kaisa", "Lorcan", "Maja", "Nils", "Orla", "Piet", "Quentin", "Rosa", "Stig", "Thea",
        "Ulla", "Viggo", "Wren", "Xenia", "Yusuf", "Zara",
    ];

    private static readonly string[] Surnames =
    [
        "Almqvist", "Bergqvist", "Castellan", "Dubois", "Eriksen", "Ferreira", "Grimaldi", "Haugen",
        "Ingersoll", "Jokinen", "Kowalski", "Lindholm", "Magnusson", "Novak", "Okonkwo", "Petrova",
        "Quintana", "Ricci", "Sorensen", "Tanaka", "Uribe", "Vasquez", "Whitlock", "Ziegler",
    ];

    private static readonly string[] Cities =
    [
        "Reykjavik", "Lisbon", "Tbilisi", "Valletta", "Ljubljana", "Tallinn", "Cork", "Bergen",
        "Palermo", "Gdansk", "Bruges", "Salzburg", "Porto", "Aarhus", "Vilnius", "Trieste",
    ];

    private static readonly string[] Adjectives =
    [
        "Restless", "Copper", "Patient", "Hollow", "Crooked", "Quiet", "Seventh", "Brackish",
        "Unwritten", "Gilded", "Reluctant", "Distant", "Glassy", "Borrowed", "Stubborn", "Tidal",
    ];

    private static readonly string[] Nouns =
    [
        "Lighthouse", "Cartographer", "Orchard", "Ledger", "Harbour", "Almanac", "Cipher", "Aviary",
        "Foundry", "Meridian", "Reliquary", "Switchboard", "Tributary", "Windmill", "Zeppelin",
        "Bellringer",
    ];

    private static readonly string[] Jobs =
    [
        "lighthouse keeper", "typesetter", "glassblower", "cartographer", "cooper", "farrier",
        "milliner", "piano tuner", "stonemason", "sailmaker", "beekeeper", "clockmaker",
    ];

    private static readonly string[] Animals =
    [
        "pangolin", "capybara", "axolotl", "narwhal", "okapi", "lemur", "puffin", "wombat",
        "tapir", "ibex", "quokka", "manatee",
    ];

    private static readonly string[] Menaces =
    [
        "the Tuesday Fog", "a committee", "the Clockwork Gull", "an unpaid invoice",
        "the Second Moon", "a very patient bureaucrat", "the Quiet Tide", "a rogue metronome",
    ];

    private readonly Random _random = new(seed);

    /// <summary>A number in <c>[0, exclusiveMax)</c>, or zero if that range is empty.</summary>
    /// <remarks>
    /// Java's <c>nextInt</c> throws on a bound of zero, and the generator guards each call site
    /// differently. Answering zero once, here, is the same thing said in one place.
    /// </remarks>
    internal int Next(int exclusiveMax) => exclusiveMax <= 0 ? 0 : _random.Next(exclusiveMax);

    /// <summary>A coin toss.</summary>
    internal bool NextBoolean() => _random.Next(2) == 1;

    /// <summary>One of <paramref name="items"/>, uniformly.</summary>
    internal T Pick<T>(IReadOnlyList<T> items) => items[_random.Next(items.Count)];

    /// <summary>A person's name.</summary>
    internal string PersonName() => $"{Pick(Forenames)} {Pick(Surnames)}";

    /// <summary>A place an artist might live.</summary>
    internal string City() => Pick(Cities);

    /// <summary>A title a book might have.</summary>
    internal string BookTitle() => $"The {Pick(Adjectives)} {Pick(Nouns)}";

    /// <summary>The one sentence every scene in the catalogue is a variation of.</summary>
    /// <remarks>Java builds the same sentence out of five javafaker calls.</remarks>
    internal string SceneDescription() =>
        $"{Pick(Adjectives)} {Pick(Nouns)}, a {Pick(Jobs)}, teams up with a pet {Pick(Animals)} "
        + $"to rescue the planet from {Pick(Menaces)}.";

    /// <summary>Between none and four names, as a set.</summary>
    internal HashSet<string> People()
    {
        int count = Next(5);
        HashSet<string> people = new(count);

        for (int i = 0; i < count; i++)
        {
            people.Add(PersonName());
        }

        return people;
    }

    /// <summary>
    /// Between <paramref name="minimum"/> and <paramref name="maximum"/> lower-case letters, as the
    /// bytes of a chapter.
    /// </summary>
    /// <remarks>Java's <c>faker.lorem().characters(min, max).getBytes()</c>, without the round trip.</remarks>
    internal byte[] Characters(int minimum, int maximum)
    {
        int length = minimum + Next(maximum - minimum + 1);
        byte[] content = new byte[length];

        for (int i = 0; i < length; i++)
        {
            content[i] = (byte)('a' + _random.Next(26));
        }

        return content;
    }

    /// <summary>A unique identifier, drawn from the same seed as everything else.</summary>
    /// <remarks>
    /// Java uses <see cref="Guid.NewGuid"/>'s equivalent, which would leave the one thing a chapter
    /// is identified by different on every run. Drawing the bytes keeps the catalogue reproducible;
    /// they are still unique within a run, which is all a chapter id has to be.
    /// </remarks>
    internal string Identifier()
    {
        byte[] bytes = new byte[16];
        _random.NextBytes(bytes);

        return new Guid(bytes).ToString();
    }
}
