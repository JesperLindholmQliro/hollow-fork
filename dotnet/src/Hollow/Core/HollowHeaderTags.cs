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

namespace Hollow.Core;

/// <summary>
/// The names of the header tags Hollow itself understands.
/// </summary>
/// <remarks>
/// <para>
/// Header tags are free-form producer metadata carried in every blob's header and mirrored into the
/// announcement. These are the few a consumer acts on; everything else is the producer's own.
/// </para>
/// <para>
/// Java declares these on the <c>HollowStateEngine</c> interface, which is a constant holder; a static
/// class is the idiomatic C# equivalent. Only the tags this port uses are here.
/// </para>
/// </remarks>
public static class HollowHeaderTags
{
    /// <summary>
    /// A hash of the producer's data model, which lets a consumer notice a schema change and load a
    /// snapshot rather than carry on with deltas that cannot carry one.
    /// </summary>
    public const string SchemaHash = "hollow.schema.hash";

    /// <summary>Whether the data model changed since the previous version.</summary>
    public const string SchemaChange = "hollow.schema.changedFromPriorVersion";

    /// <summary>The version the producer intended this blob to produce.</summary>
    public const string ProducerToVersion = "hollow.blob.to.version";

    /// <summary>How many versions this delta chain has been through.</summary>
    public const string DeltaChainVersionCounter = "hollow.delta.chain.version.counter";
}
