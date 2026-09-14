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

using Hollow.Core.Schema;

namespace Hollow.Core.Tools.History;

/// <summary>
/// A type whose schema changed at some point in the history: what it was, and what it became.
/// </summary>
/// <remarks>
/// <para>
/// Either side may be absent — a type that arrived has no before, and one that went has no after.
/// </para>
/// <para>
/// Named <c>HollowHistoricalSchemaChange</c> in Java, where both sides are plain nullable fields; a
/// record says the same thing and makes the nullability explicit.
/// </para>
/// </remarks>
/// <param name="BeforeSchema">The schema as it was, or <see langword="null"/> if the type arrived.</param>
/// <param name="AfterSchema">The schema as it became, or <see langword="null"/> if the type went.</param>
public sealed record HollowHistoricalSchemaChange(HollowSchema? BeforeSchema, HollowSchema? AfterSchema);
