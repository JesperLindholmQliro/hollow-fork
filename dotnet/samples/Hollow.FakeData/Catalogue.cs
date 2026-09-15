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
/// The catalogue the producer publishes, and the churn that makes each cycle differ from the last.
/// </summary>
/// <remarks>
/// Java keeps all of this in static fields on <c>FakeDataGenerator</c>. Here it is an object, so that
/// the seed, the counters and the books they produce belong to one another and a test could make two.
/// </remarks>
internal sealed class Catalogue(FakeText text, int maxCharactersInAChapter = 100)
{
    /// <summary>The country codes a book may be published in.</summary>
    /// <remarks>
    /// Java's list, verbatim — including <c>IRQ</c> and <c>Man</c>, which are not ISO codes. They are
    /// left alone: this is fake data, and changing them would change every key in the dataset.
    /// </remarks>
    private static readonly string[] Countries =
    [
        "US", "CA", "MX", "BE", "BZ", "BJ", "BM", "BT", "BR", "BG", "BI", "CV", "KH", "CM", "CL",
        "CN", "CW", "CY", "CZ", "DK", "DJ", "DM", "EC", "EG", "SV", "ER", "EE", "SZ", "ET", "FI",
        "FR", "GA", "GE", "DE", "GH", "GI", "GR", "GL", "GD", "GP", "GT", "GG", "GN", "GY", "HT",
        "HK", "HU", "IS", "IN", "ID", "IRQ", "IE", "Man", "IL", "IT", "JM", "JP", "JE", "JO", "KZ",
        "KE", "KI", "PK", "PW", "TK", "TO", "TN", "TR", "TM", "TV", "UG", "UA",
    ];

    private static readonly string[] Sizes = ["small", "medium", "large"];

    private readonly List<Artist> _artists = [];
    private readonly List<Book> _books = [];

    // Java stamps each piece of art with System.currentTimeMillis(), which leaves two runs of the
    // same seed holding different records. A clock that starts at a fixed moment and ticks a minute
    // per record says the same thing and keeps the catalogue reproducible.
    private long _clock = DateTimeOffset.UnixEpoch.AddYears(46).ToUnixTimeMilliseconds();

    private int _nextBookId = 1;

    /// <summary>Every book the catalogue has ever held, including the ones a cycle is dropping.</summary>
    /// <remarks>
    /// A removal is a book the next cycle does not publish, not a book forgotten: leaving it here is
    /// what lets a later cycle bring it back, which is the churn the delta chain is for.
    /// </remarks>
    internal IReadOnlyList<Book> Books => _books;

    /// <summary>Fills the pool of artists that the cover art draws from.</summary>
    /// <remarks>
    /// Java collects them into a <c>HashSet</c>, which does nothing — <c>Artist</c> declares no
    /// equality, so a thousand draws give a thousand entries however many names repeat. Since
    /// <c>Artist</c> is keyed on its name, two artists of one name with different cities are a
    /// duplicate key; deduplicating on the name is what that set was reaching for.
    /// </remarks>
    internal void PopulateArtists(int count)
    {
        Dictionary<string, Artist> byName = [];

        for (int i = 0; i < count; i++)
        {
            string name = text.PersonName();

            if (!byName.ContainsKey(name))
            {
                byName[name] = new Artist { Name = name, City = text.City() };
            }
        }

        _artists.AddRange(byName.Values);
    }

    /// <summary>Adds <paramref name="count"/> books, each published in a random set of countries.</summary>
    /// <remarks>
    /// <para>
    /// One book is one record per country it is published in, so this adds a good deal more than
    /// <paramref name="count"/> records — around half the country list per book.
    /// </para>
    /// <para>
    /// <strong>A Java bug is not reproduced here.</strong> Java's <c>populateCatalog</c> takes a start
    /// id and what it calls a count, and then loops <c>for (id = start; id &lt; count; id++)</c> —
    /// treating the count as an end. On the first call, from 1 to 10 000, that happens to be nearly
    /// right. On every later call, from a start already past 10 000 to a count below 200, the loop
    /// body never runs, so the adds-per-cycle entropy Java means to produce never happens at all.
    /// A count is a count here.
    /// </para>
    /// </remarks>
    internal void PopulateBooks(int count)
    {
        for (int i = 0; i < count; i++)
        {
            int bookId = _nextBookId++;

            foreach (string country in RandomCountries())
            {
                _books.Add(NewBook(bookId, country));
            }
        }
    }

    /// <summary>Changes a book's art and chapters, each with even odds.</summary>
    internal void ModifyBook(Book book)
    {
        ArgumentNullException.ThrowIfNull(book);

        // Java hands Map.remove a Map.Entry rather than a key, so its removal never removes anything
        // and a book's art only ever grows. Removing the key is what was meant.
        if (text.NextBoolean() && book.Images.Art.Count > 0)
        {
            book.Images.Art.Remove(book.Images.Art.Keys.First());
        }

        if (text.NextBoolean() && book.Images.Art.Count < Sizes.Length)
        {
            string size = Sizes.First(candidate => !book.Images.Art.ContainsKey(candidate));

            // Java builds this id from BookId.toString(), which has no override and so contributes a
            // hash code. The id of the art a book already has is built from the id's value; this
            // matches it, so replacing a size gives the record it replaced.
            book.Images.Art[size] = [NewArt(book.Id.Value, book.Country.Id)];
        }

        if (text.NextBoolean() && book.Metadata.Chapters.Count > 0)
        {
            book.Metadata.Chapters.RemoveAt(0);
        }

        if (text.NextBoolean())
        {
            book.Metadata.Chapters.Add(NewChapter(book.Id.Value, text.Next(1000)));
        }
    }

    /// <summary>Up to <paramref name="howMany"/> book ids, with the repeats collapsed.</summary>
    internal HashSet<int> RandomBookIds(int howMany)
    {
        HashSet<int> ids = [];

        for (int i = 0; i < Math.Max(1, howMany); i++)
        {
            ids.Add(1 + text.Next(_nextBookId - 1));
        }

        return ids;
    }

    private HashSet<string> RandomCountries()
    {
        int count = Math.Max(1, text.Next(Countries.Length));
        HashSet<string> countries = new(count);

        for (int i = 0; i < count; i++)
        {
            countries.Add(text.Pick(Countries));
        }

        return countries;
    }

    private Book NewBook(int bookId, string country)
    {
        // One page count for the whole book, as in Java: a chapter's page count says how long the
        // book is, not how long the chapter is.
        int pages = text.Next(1000);

        return new Book
        {
            Id = new BookId { Value = bookId },
            Country = new Country { Id = country },
            Images = new BookImages
            {
                Art = new Dictionary<string, List<Art>>
                {
                    ["small"] = [NewArt(bookId, country)],
                    ["medium"] = [NewArt(bookId, country)],
                    ["large"] = [NewArt(bookId, country)],
                },
            },
            Metadata = new BookMetadata
            {
                Name = text.BookTitle(),
                Genre = text.Pick(Enum.GetValues<Genre>()),
                Chapters = [NewChapter(bookId, pages), NewChapter(bookId, pages)],
            },
        };
    }

    private Art NewArt(int bookId, string country) =>
        new()
        {
            Id = $"{bookId}{country}",
            Artist = text.Pick(_artists),
            TimeOfCreation = _clock += 60_000,
            Size = text.Next(10_000),
        };

    private Chapter NewChapter(int bookId, int pages) =>
        new()
        {
            ChapterId = new ChapterId { Value = text.Identifier() },
            ChapterInfo = new ChapterInfo
            {
                BookId = new BookId { Value = bookId },
                Pages = pages,
                Content = text.Characters(10, maxCharactersInAChapter),
            },
            Scenes = [NewScene()],
        };

    private Scene NewScene() =>
        new()
        {
            Description = text.SceneDescription(),
            Popularity = text.Next(100_000),
            Characters = text.People(),
        };
}
