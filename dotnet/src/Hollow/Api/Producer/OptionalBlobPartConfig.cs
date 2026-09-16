/*
 *  Copyright 2021 Netflix, Inc.
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

using Hollow.Core.Write;

namespace Hollow.Api.Producer;

/// <summary>
/// Which types a producer writes into optional parts rather than into the blob itself.
/// </summary>
/// <remarks>
/// <para>
/// A part is published as a separate artifact, so a consumer that does not need the types in it never
/// fetches it. That is the point: a dataset whose bulk sits in a handful of types can serve the
/// consumers that only want the rest at a fraction of the size.
/// </para>
/// <para>
/// A type belongs to at most one part. Anything not assigned goes into the main blob.
/// </para>
/// <para>
/// Named <c>ProducerOptionalBlobPartConfig</c> in Java, where the package cannot say <c>producer</c>
/// and the name has to.
/// </para>
/// </remarks>
public sealed class OptionalBlobPartConfig
{
    private readonly Dictionary<string, HashSet<string>> _parts = new(StringComparer.Ordinal);

    /// <summary>The names of the parts configured so far.</summary>
    public IReadOnlyCollection<string> Parts => _parts.Keys;

    /// <summary>
    /// Assigns <paramref name="types"/> to the part named <paramref name="partName"/>, creating it if
    /// this is the first mention of it.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// One of the types is already assigned to a different part.
    /// </exception>
    public void AddTypesToPart(string partName, params string[] types)
    {
        ArgumentException.ThrowIfNullOrEmpty(partName);
        ArgumentNullException.ThrowIfNull(types);

        if (types.Length == 0)
        {
            return;
        }

        foreach (string type in types)
        {
            // Java lets a type land in two parts, which writes its records twice and leaves a consumer
            // holding whichever part it read last. There is no use for that.
            foreach ((string other, HashSet<string> assigned) in _parts)
            {
                if (!string.Equals(other, partName, StringComparison.Ordinal) && assigned.Contains(type))
                {
                    throw new ArgumentException(
                        $"{type} is already assigned to the part '{other}'", nameof(types));
                }
            }
        }

        if (!_parts.TryGetValue(partName, out HashSet<string>? part))
        {
            part = new HashSet<string>(StringComparer.Ordinal);
            _parts[partName] = part;
        }

        part.UnionWith(types);
    }

    /// <summary>The types assigned to <paramref name="partName"/>.</summary>
    public IReadOnlySet<string> TypesInPart(string partName) =>
        _parts.TryGetValue(partName, out HashSet<string>? types)
            ? types
            : throw new ArgumentException($"there is no blob part named '{partName}'", nameof(partName));

    /// <summary>
    /// Binds an output to each configured part, ready to be written to.
    /// </summary>
    /// <param name="outputFor">Opens the output for one part, by name.</param>
    /// <exception cref="ArgumentException">One of the parts was given no output.</exception>
    public OptionalBlobPartOutputs NewOutputs(Func<string, HollowBlobOutput> outputFor)
    {
        ArgumentNullException.ThrowIfNull(outputFor);

        Dictionary<string, HollowBlobOutput> outputs = new(StringComparer.Ordinal);

        foreach (string part in _parts.Keys)
        {
            outputs[part] = outputFor(part)
                ?? throw new ArgumentException($"no output was opened for the part '{part}'", nameof(outputFor));
        }

        return new OptionalBlobPartOutputs(_parts, outputs);
    }
}

/// <summary>
/// The outputs a producer writes one cycle's optional parts to.
/// </summary>
/// <remarks>
/// Java lets outputs be added one at a time and then checks, at the point of use, that every part got
/// one. Binding them all at once means the check happens where it can still be acted on.
/// </remarks>
public sealed class OptionalBlobPartOutputs
{
    private readonly Dictionary<string, HollowBlobOutput> _outputs;

    internal OptionalBlobPartOutputs(
        IReadOnlyDictionary<string, HashSet<string>> parts, Dictionary<string, HollowBlobOutput> outputs)
    {
        _outputs = outputs;

        Dictionary<string, HollowBlobOutput> byType = new(StringComparer.Ordinal);
        Dictionary<string, string> partNameByType = new(StringComparer.Ordinal);

        foreach ((string part, HashSet<string> types) in parts)
        {
            foreach (string type in types)
            {
                byType[type] = outputs[part];
                partNameByType[type] = part;
            }
        }

        OutputByType = byType;
        PartNameByType = partNameByType;
    }

    /// <summary>Where each assigned type's records go.</summary>
    public IReadOnlyDictionary<string, HollowBlobOutput> OutputByType { get; }

    /// <summary>Which part each assigned type belongs to.</summary>
    public IReadOnlyDictionary<string, string> PartNameByType { get; }

    /// <summary>The output of each part, by name.</summary>
    public IReadOnlyDictionary<string, HollowBlobOutput> Outputs => _outputs;

    /// <summary>Flushes every part's output.</summary>
    public void Flush()
    {
        foreach (HollowBlobOutput output in _outputs.Values)
        {
            output.Flush();
        }
    }
}
