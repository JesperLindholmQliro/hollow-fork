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

namespace Hollow.FakeData;

// The data model of hollow-fakedata: a book catalogue, deep enough that every kind of record the
// format has appears somewhere in it.
//
// A book references an id, a country, its images and its metadata; the images are a map of size name
// to a list of art; the metadata holds an enum and a list of chapters; a chapter holds a byte array
// and a list of scenes; a scene holds a set of character names. Object, list, set and map records,
// inline and referenced scalars, an enum and a byte array — all of it, on purpose.
//
// Java's classes carry constructors and package-private fields. The .NET object mapper reads public
// members, so these are public init-only properties instead, and the members are PascalCase, which is
// what the schemas end up saying.

/// <summary>One book, in one country.</summary>
/// <remarks>
/// Java writes the key as <c>{"id", "country"}</c> and lets Hollow expand each step to the single
/// field of the type it lands on. The same two paths here, for the same reason: neither
/// <c>BookId</c> nor <c>Country</c> holds anything else.
/// </remarks>
[HollowPrimaryKey("Id", "Country")]
public sealed class Book
{
    public required BookId Id { get; init; }

    public required Country Country { get; init; }

    public required BookImages Images { get; init; }

    /// <remarks>Java calls this <c>bookMetadata</c>; inside <c>Book</c> the prefix says nothing.</remarks>
    public required BookMetadata Metadata { get; init; }
}

/// <summary>A book's identifier, as a type of its own so that it deduplicates.</summary>
public sealed class BookId
{
    public required int Value { get; init; }
}

/// <summary>Where a book was published.</summary>
public sealed class Country
{
    /// <remarks>
    /// Inline, so the code is stored in the record rather than as a reference to a shared
    /// <c>String</c>. A two-letter code is cheaper stored than pointed at.
    /// </remarks>
    [HollowInline]
    public required string Id { get; init; }
}

/// <summary>A book's cover art, by size.</summary>
public sealed class BookImages
{
    /// <summary>Size name — <c>small</c>, <c>medium</c>, <c>large</c> — to the art at that size.</summary>
    /// <remarks>Mutable: a cycle's churn adds and drops sizes on a book already in the catalogue.</remarks>
    public required Dictionary<string, List<Art>> Art { get; init; }
}

/// <summary>One piece of cover art.</summary>
public sealed class Art
{
    public required string Id { get; init; }

    public required Artist Artist { get; init; }

    public required long TimeOfCreation { get; init; }

    public required long Size { get; init; }
}

/// <summary>Whoever made a piece of art.</summary>
/// <remarks>
/// Keyed on the name, which is why the generator deduplicates artists by name rather than trusting
/// a set of references to do it.
/// </remarks>
[HollowPrimaryKey("Name")]
public sealed class Artist
{
    public required string Name { get; init; }

    public required string City { get; init; }
}

/// <summary>What a book is, as opposed to which book it is.</summary>
public sealed class BookMetadata
{
    public required string Name { get; init; }

    public required Genre Genre { get; init; }

    /// <remarks>Mutable: a cycle's churn adds and drops chapters.</remarks>
    public required List<Chapter> Chapters { get; init; }
}

/// <summary>What shelf a book belongs on.</summary>
/// <remarks>
/// Java spells the constants in upper case; .NET spells them in Pascal case. The mapper writes an
/// enum as a one-field record holding the member's name, so these names are what the blob carries.
/// </remarks>
public enum Genre
{
    Action,
    Classic,
    Art,
    Travel,
    Guide,
    Psychology,
    Business,
    Drama,
    Poetry,
    Romance,
    Fiction,
}

/// <summary>One chapter of one book.</summary>
public sealed class Chapter
{
    public required ChapterId ChapterId { get; init; }

    public required ChapterInfo ChapterInfo { get; init; }

    public required List<Scene> Scenes { get; init; }
}

/// <summary>A chapter's identifier.</summary>
public sealed class ChapterId
{
    /// <remarks>Java calls this <c>val</c>, which is not a word.</remarks>
    public required string Value { get; init; }
}

/// <summary>A chapter's text and where it sits.</summary>
public sealed class ChapterInfo
{
    public required BookId BookId { get; init; }

    public required int Pages { get; init; }

    /// <remarks>
    /// A byte array maps as a reference to a <c>Bytes</c> record, not as a field of the chapter — so
    /// two chapters with the same text share one copy of it.
    /// </remarks>
    public required byte[] Content { get; init; }
}

/// <summary>One scene of one chapter.</summary>
public sealed class Scene
{
    public required string Description { get; init; }

    public required long Popularity { get; init; }

    /// <summary>Who is in it — a set, so the order it was written in is not part of the record.</summary>
    public required HashSet<string> Characters { get; init; }
}
