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

using Hollow.Api.Codegen;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Sample.Model;

// The whole data model, and the only hand-written description of it there is. The producer maps these
// classes into records; the source generator reads the same declarations at compile time and emits the
// client the consumer reads them back through. Nothing is checked in twice and nothing can drift.

/// <summary>
/// The root of the model: marking it is what makes the generator emit a client, and every type
/// reachable from here is part of that client.
/// </summary>
/// <remarks>
/// <c>CachedTypes</c> names the types whose wrappers are built once and kept, rather than being
/// constructed per read. Worth it for a small type read over and over — a <c>Studio</c> here is shared
/// by many films — and not worth it for a large one.
/// </remarks>
[HollowGeneratedApi(ApiClassName = "CatalogueApi", CachedTypes = ["Studio", "Rating"])]
[HollowPrimaryKey("Id")]
public sealed class Movie
{
    /// <summary>
    /// A non-nullable int, so it is stored in the record itself rather than as a reference to a shared
    /// <c>Integer</c> type. The same is true of every non-nullable primitive below.
    /// </summary>
    public required int Id { get; init; }

    public required string Title { get; init; }

    public required int ReleaseYear { get; init; }

    /// <summary>
    /// A decimal, which Java's Hollow has no field type for. This port adds one rather than making the
    /// caller choose between losing precision as a double and losing arithmetic as a string.
    /// </summary>
    public required decimal Budget { get; init; }

    /// <summary>An enum, stored as its name so that adding a value does not renumber the others.</summary>
    public required Rating Rating { get; init; }

    /// <summary>
    /// A reference to a shared record: two films from the same studio point at one <c>Studio</c>, which
    /// is deduplicated away to a single copy.
    /// </summary>
    public required Studio Studio { get; init; }

    /// <summary>An ordered collection, becoming a <c>ListOfActor</c> type.</summary>
    public List<Actor> Cast { get; init; } = [];

    /// <summary>An unordered one, becoming a <c>SetOfString</c>.</summary>
    public HashSet<string> Tags { get; init; } = [];

    /// <summary>
    /// A map keyed by a record rather than by a scalar. The hash key says which of the key record's
    /// fields to hash, so a lookup can be made with a value rather than with a whole key record.
    /// </summary>
    [HollowHashKey("Name")]
    public Dictionary<Award, int> AwardsWon { get; init; } = [];

    /// <summary>Nullable, so a film with no tagline simply has no reference here.</summary>
    public string? Tagline { get; init; }

    /// <summary>Not part of the dataset at all — computed, and never written.</summary>
    [HollowTransient]
    public string Display => $"{Title} ({ReleaseYear})";
}

public enum Rating
{
    G,
    PG,
    PG13,
    R,
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

    /// <summary>
    /// Inlined rather than shared. A billing position is a small number attached to one role, so a
    /// reference to a shared <c>Integer</c> would cost more than the value it points at.
    /// </summary>
    [HollowInline]
    public required int BilledOrder { get; init; }
}

[HollowPrimaryKey("Name")]
public sealed class Award
{
    public required string Name { get; init; }

    public required string Category { get; init; }
}
