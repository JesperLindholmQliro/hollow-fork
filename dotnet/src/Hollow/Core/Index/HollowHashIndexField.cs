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

using Hollow.Core.Read.DataAccess;
using Hollow.Core.Schema;

namespace Hollow.Core.Index;

/// <summary>
/// One indexed field of a <see cref="HollowHashIndex"/>, split into the part the traverser resolves and
/// the object-field steps left over.
/// </summary>
/// <remarks>
/// A path is traversed only as far as the last record whose ordinal the index needs to store — the last
/// collection element, or the whole path when there is no collection. The remaining object-field steps
/// are walked on demand from that ordinal, which keeps the stored key narrow.
/// </remarks>
internal sealed class HollowHashIndexField(
    int baseIteratorFieldIndex,
    HollowHashIndexField.FieldPathSegment[] remainingPath,
    IHollowTypeDataAccess baseDataAccess,
    FieldType fieldType)
{
    /// <summary>The traverser field whose ordinal this path continues from.</summary>
    internal int BaseIteratorFieldIndex { get; } = baseIteratorFieldIndex;

    /// <summary>The object-field steps to walk from that ordinal.</summary>
    internal FieldPathSegment[] SchemaFieldPositionPath { get; } = remainingPath;

    /// <summary>The type the traverser leaves this path at.</summary>
    internal IHollowTypeDataAccess BaseDataAccess { get; } = baseDataAccess;

    /// <summary>The type of the value this path ends at.</summary>
    internal FieldType FieldType { get; } = fieldType;

    /// <summary>The last step of the path, which reads the value itself.</summary>
    internal FieldPathSegment LastFieldPositionPathElement => SchemaFieldPositionPath[^1];

    /// <summary>
    /// One object-field step of a path: a field position, and the type it is read from.
    /// </summary>
    internal sealed class FieldPathSegment(int fieldPosition, IHollowObjectTypeDataAccess objectTypeDataAccess)
    {
        /// <summary>The field's position within the type it is read from.</summary>
        internal int SegmentFieldPosition { get; } = fieldPosition;

        /// <summary>The type this step reads from.</summary>
        internal IHollowObjectTypeDataAccess ObjectTypeDataAccess { get; } = objectTypeDataAccess;

        /// <summary>The ordinal the field of <paramref name="ordinal"/>'s record refers to.</summary>
        internal int GetOrdinalForField(int ordinal) =>
            ObjectTypeDataAccess.ReadOrdinal(ordinal, SegmentFieldPosition);
    }
}
