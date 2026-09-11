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

namespace Hollow.Core.Write.Copy;

/// <summary>
/// Translates the ordinals a record references as it is copied from one state into another.
/// </summary>
/// <remarks>
/// <para>
/// A copied record names its referenced records by ordinal, and those ordinals only mean anything
/// relative to the state they came from. Restoring keeps them as they are; combining or splitting
/// datasets does not, which is why the copiers take this rather than assuming identity.
/// </para>
/// <para>
/// Java places this in <c>tools.combine</c> as <c>OrdinalRemapper</c>; the <c>I</c> prefix follows the
/// .NET interface naming convention. It lives next to the copiers here because the copiers are the
/// only thing in this port that uses it.
/// </para>
/// </remarks>
public interface IOrdinalRemapper
{
    /// <summary>
    /// Returns the ordinal <paramref name="originalOrdinal"/> of <paramref name="type"/> maps to in the
    /// destination state.
    /// </summary>
    int GetMappedOrdinal(string type, int originalOrdinal);

    /// <summary>
    /// Records that <paramref name="originalOrdinal"/> of <paramref name="type"/> maps to
    /// <paramref name="mappedOrdinal"/> in the destination state.
    /// </summary>
    void RemapOrdinal(string type, int originalOrdinal, int mappedOrdinal);

    /// <summary>
    /// Whether a mapping for <paramref name="originalOrdinal"/> of <paramref name="type"/> is defined.
    /// </summary>
    bool OrdinalIsMapped(string type, int originalOrdinal);
}

/// <summary>
/// The remapper for copies that keep every ordinal exactly as it was, which is what restoring a write
/// state from a read state needs.
/// </summary>
public sealed class IdentityOrdinalRemapper : IOrdinalRemapper
{
    private IdentityOrdinalRemapper()
    {
    }

    /// <summary>The single instance; the type carries no state.</summary>
    public static IdentityOrdinalRemapper Instance { get; } = new();

    /// <inheritdoc />
    public int GetMappedOrdinal(string type, int originalOrdinal) => originalOrdinal;

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always: an identity mapping cannot be overridden.</exception>
    public void RemapOrdinal(string type, int originalOrdinal, int mappedOrdinal) =>
        throw new NotSupportedException($"Cannot remap ordinals in an {nameof(IdentityOrdinalRemapper)}");

    /// <inheritdoc />
    public bool OrdinalIsMapped(string type, int originalOrdinal) => true;
}
