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

using Hollow.Core.Write.ObjectMapper;

namespace Hollow.Reference.Model;

/// <summary>
/// A cast member, referenced by every film they appear in rather than copied into each of them.
/// </summary>
/// <remarks>
/// Ported from <c>how.hollow.producer.datamodel.Actor</c>. Equality is deliberately left as reference
/// equality, as in the Java original: the source of truth hands out one instance per actor, and a
/// rename is meant to be seen by every film holding that instance.
/// </remarks>
[HollowPrimaryKey("ActorId")]
public sealed class Actor
{
    public Actor()
    {
    }

    public Actor(int actorId, string actorName)
    {
        ActorId = actorId;
        ActorName = actorName;
    }

    public int ActorId { get; set; }

    public string ActorName { get; set; } = string.Empty;
}
