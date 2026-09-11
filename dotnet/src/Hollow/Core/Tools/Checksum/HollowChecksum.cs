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

using System.Globalization;
using Hollow.Core.Memory.Encoding;
using Hollow.Core.Read.Engine;
using Hollow.Core.Schema;

namespace Hollow.Core.Tools.Checksum;

/// <summary>
/// A checksum over the data a <see cref="HollowReadStateEngine"/> holds.
/// </summary>
/// <remarks>
/// <para>
/// This is what a producer's integrity check compares. Having written a snapshot, a delta and a
/// reverse delta for a cycle, the producer reads the snapshot into a fresh state, applies the delta to
/// the previous state, and checks that the two agree — and likewise backwards. A delta that is subtly
/// wrong produces data that looks plausible field by field, so comparing every record is the only
/// check worth making.
/// </para>
/// <para>
/// The checksum covers ordinals and bucket positions, not just values. Two states holding the same
/// records at different ordinals, or a set whose elements landed in different buckets, do not match —
/// which is the point, because a consumer's ordinals have to agree with the producer's.
/// </para>
/// </remarks>
public sealed class HollowChecksum : IEquatable<HollowChecksum>
{
    private int _currentChecksum;

    /// <summary>The per-type checksums this one was built from, in type-name order.</summary>
    public IReadOnlyList<TypeChecksum> SortedTypeChecksums { get; private set; } = [];

    /// <summary>The checksum as an integer.</summary>
    public int IntValue => _currentChecksum;

    /// <summary>Folds <paramref name="value"/> into this checksum.</summary>
    public void ApplyInt(int value)
    {
        _currentChecksum ^= HashCodes.HashInt(value);
        _currentChecksum = HashCodes.HashInt(_currentChecksum);
    }

    /// <summary>Folds <paramref name="value"/> into this checksum.</summary>
    public void ApplyLong(long value)
    {
        _currentChecksum ^= HashCodes.HashLong(value);
        _currentChecksum = HashCodes.HashInt(_currentChecksum);
    }

    /// <summary>
    /// Computes the checksum of everything <paramref name="stateEngine"/> holds.
    /// </summary>
    public static HollowChecksum ForStateEngine(HollowReadStateEngine stateEngine) =>
        ForStateEngineWithCommonSchemas(stateEngine, stateEngine);

    /// <summary>
    /// Computes the checksum of <paramref name="stateEngine"/> over only the types and fields it shares
    /// with <paramref name="commonSchemasWith"/>.
    /// </summary>
    /// <remarks>
    /// Two states either side of a schema change still have to agree about the data they both describe,
    /// so the comparison is made over the intersection rather than refused.
    /// </remarks>
    public static HollowChecksum ForStateEngineWithCommonSchemas(
        HollowReadStateEngine stateEngine, HollowReadStateEngine commonSchemasWith)
    {
        ArgumentNullException.ThrowIfNull(stateEngine);
        ArgumentNullException.ThrowIfNull(commonSchemasWith);

        List<TypeChecksum> typeChecksums = [];

        foreach (HollowTypeReadState typeState in stateEngine.TypeStates.Values)
        {
            if (commonSchemasWith.GetTypeState(typeState.TypeName) is not { } counterpart)
            {
                continue;
            }

            typeChecksums.Add(new TypeChecksum(typeState.TypeName, typeState.GetChecksum(counterpart.Schema)));
        }

        typeChecksums.Sort(static (first, second) =>
            string.CompareOrdinal(first.TypeName, second.TypeName));

        HollowChecksum total = new();
        foreach (TypeChecksum typeChecksum in typeChecksums)
        {
            total.ApplyInt(typeChecksum.Checksum);
        }

        total.SortedTypeChecksums = typeChecksums;

        return total;
    }

    /// <inheritdoc />
    public bool Equals(HollowChecksum? other) => other is not null && other._currentChecksum == _currentChecksum;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as HollowChecksum);

    /// <inheritdoc />
    public override int GetHashCode() => _currentChecksum;

    /// <inheritdoc />
    public override string ToString() => _currentChecksum.ToString("x", CultureInfo.InvariantCulture);

    /// <summary>
    /// One type's contribution to a state's checksum.
    /// </summary>
    public sealed class TypeChecksum
    {
        internal TypeChecksum(string typeName, HollowChecksum checksum)
        {
            TypeName = typeName;
            Checksum = checksum.IntValue;
        }

        /// <summary>The type this covers.</summary>
        public string TypeName { get; }

        /// <summary>The checksum of that type's records.</summary>
        public int Checksum { get; }

        /// <inheritdoc />
        public override string ToString() =>
            $"{TypeName}={Checksum.ToString("x", CultureInfo.InvariantCulture)}";
    }
}
