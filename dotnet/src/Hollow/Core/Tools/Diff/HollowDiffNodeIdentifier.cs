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

using System.Text;

namespace Hollow.Core.Tools.Diff;

/// <summary>
/// Where a field sits in a type's hierarchy, as something that can be compared and shown.
/// </summary>
/// <remarks>
/// The diff splits the difference between two records across the fields it is spread over, and a field
/// three references down needs naming by the whole route taken to reach it — <c>Movie.Cast.element.Name
/// (String)</c>. This is that route: comparable, so results found separately can be added together, and
/// readable, so a page can show it.
/// </remarks>
public sealed class HollowDiffNodeIdentifier : IEquatable<HollowDiffNodeIdentifier>
{
    /// <summary>Names the root of a type's hierarchy.</summary>
    public HollowDiffNodeIdentifier(string typeName)
        : this(null, null, typeName)
    {
    }

    /// <summary>Names a node reached from <paramref name="parent"/> through <paramref name="viaFieldName"/>.</summary>
    public HollowDiffNodeIdentifier(
        HollowDiffNodeIdentifier? parent, string? viaFieldName, string nodeName)
    {
        Parents = parent is null ? [] : [.. parent.Parents, parent];
        ViaFieldName = viaFieldName;
        NodeName = nodeName;
    }

    /// <summary>The nodes passed through to reach this one, outermost first.</summary>
    public IReadOnlyList<HollowDiffNodeIdentifier> Parents { get; }

    /// <summary>The field this node was reached through, or nothing at the root.</summary>
    public string? ViaFieldName { get; }

    /// <summary>The type this node is, or the field type where it is a value.</summary>
    public string NodeName { get; }

    /// <inheritdoc />
    public bool Equals(HollowDiffNodeIdentifier? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null || other.Parents.Count != Parents.Count)
        {
            return false;
        }

        // From the leaf end, because two routes that differ usually differ late.
        for (int i = Parents.Count - 1; i >= 0; i--)
        {
            if (!Parents[i].ShallowEquals(other.Parents[i]))
            {
                return false;
            }
        }

        return ShallowEquals(other);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as HollowDiffNodeIdentifier);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = default;

        foreach (HollowDiffNodeIdentifier parent in Parents)
        {
            if (parent.ViaFieldName is not null)
            {
                hash.Add(parent.ViaFieldName, StringComparer.Ordinal);
            }

            hash.Add(parent.NodeName, StringComparer.Ordinal);
        }

        if (ViaFieldName is not null)
        {
            hash.Add(ViaFieldName, StringComparer.Ordinal);
        }

        hash.Add(NodeName, StringComparer.Ordinal);

        return hash.ToHashCode();
    }

    /// <summary>The route to this field, written the way a person reads it.</summary>
    public override string ToString()
    {
        StringBuilder builder = new();

        if (Parents.Count > 0)
        {
            builder.Append(Parents[0].NodeName);
        }

        for (int i = 1; i < Parents.Count; i++)
        {
            builder.Append('.').Append(Parents[i].ViaFieldName);
        }

        return builder.Append('.').Append(ViaFieldName).Append(" (").Append(NodeName).Append(')').ToString();
    }

    private bool ShallowEquals(HollowDiffNodeIdentifier other) =>
        string.Equals(ViaFieldName, other.ViaFieldName, StringComparison.Ordinal)
        && string.Equals(NodeName, other.NodeName, StringComparison.Ordinal);
}
