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

using Hollow.Api.Codegen;
using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Reference.Model;

// The whole data model, and the only description of it there is. The producer maps these classes into
// records; the source generator reads the same declarations at compile time and emits the client the
// consumer reads them back through.
//
// Ported from how.hollow.producer.datamodel.Movie. Java's public fields become properties and its
// camelCase names become PascalCase, which changes the Hollow field names in the schema too — a
// dataset written by this producer is read by this consumer, not by the Java one.

/// <summary>
/// A film, and the root of the model.
/// </summary>
/// <remarks>
/// <para>
/// The properties are mutable because the source of truth mutates them: a title is edited in place
/// between cycles, and the producer is handed the same objects again. That is what the Java original
/// does, and it is what makes an edit to one <see cref="Actor"/> show up in every film that casts
/// them.
/// </para>
/// <para>
/// <c>[HollowGeneratedApi]</c> is what makes the generator emit a client, and every type reachable
/// from here is part of it. Java declared the equivalent in <c>build.gradle</c>'s <c>hollow { }</c>
/// block and in <c>APIGenerator</c>; here it is an attribute on the model itself, so there is nothing
/// to keep in step and nothing to regenerate by hand.
/// </para>
/// </remarks>
[HollowGeneratedApi(ApiClassName = "MovieApi")]
[HollowPrimaryKey("Id")]
public sealed class Movie
{
    public Movie()
    {
    }

    public Movie(int id, string title, HashSet<Actor> actors)
    {
        Id = id;
        Title = title;
        Actors = actors;
    }

    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// The cast, as a set rather than a list: two films with the same cast share one set record.
    /// </summary>
    /// <remarks>
    /// The hash key names the field a lookup into the set is made on, so a caller can ask for an actor
    /// by name rather than by building a whole <see cref="Actor"/> to hash. <see cref="Actor"/> does
    /// not override equality, so the set deduplicates by reference — which is right here, because the
    /// source of truth hands out one instance per actor.
    /// </remarks>
    [HollowHashKey("ActorName")]
    public HashSet<Actor> Actors { get; set; } = [];
}
