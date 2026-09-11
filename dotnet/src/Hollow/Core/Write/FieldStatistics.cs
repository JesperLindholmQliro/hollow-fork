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

using System.Numerics;
using Hollow.Core.Schema;

namespace Hollow.Core.Write;

/// <summary>
/// Measures, across every record of an object type, how many bits each field needs, so that the
/// fixed-length portion of the type can be packed as tightly as the data allows.
/// </summary>
public sealed class FieldStatistics
{
    private readonly HollowObjectSchema _schema;
    private readonly int[] _maxBitsForField;
    private readonly long[] _nullValueForField;
    private readonly long[] _totalSizeOfVarLengthField;
    private readonly int[] _bitOffsetForField;

    /// <summary>
    /// Initialises empty statistics for <paramref name="schema"/>.
    /// </summary>
    public FieldStatistics(HollowObjectSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        _schema = schema;
        _maxBitsForField = new int[schema.FieldCount];
        _nullValueForField = new long[schema.FieldCount];
        _totalSizeOfVarLengthField = new long[schema.FieldCount];
        _bitOffsetForField = new int[schema.FieldCount];
    }

    /// <summary>The total width of one record, in bits.</summary>
    public int NumBitsPerRecord { get; private set; }

    /// <summary>The bit offset of a field within a record.</summary>
    public int GetFieldBitOffset(int fieldIndex) => _bitOffsetForField[fieldIndex];

    /// <summary>The width of a field, in bits.</summary>
    public int GetMaxBitsForField(int fieldIndex) => _maxBitsForField[fieldIndex];

    /// <summary>The all-ones value that marks a field as null.</summary>
    public long GetNullValueForField(int fieldIndex) => _nullValueForField[fieldIndex];

    /// <summary>
    /// Records that a fixed-length field needs at least <paramref name="numberOfBits"/> bits.
    /// </summary>
    public void AddFixedLengthFieldRequiredBits(int fieldIndex, int numberOfBits)
    {
        if (numberOfBits > _maxBitsForField[fieldIndex])
        {
            _maxBitsForField[fieldIndex] = numberOfBits;
        }
    }

    /// <summary>
    /// Records that a variable-length field contributes <paramref name="fieldSize"/> more bytes.
    /// </summary>
    public void AddVarLengthFieldSize(int fieldIndex, int fieldSize) =>
        _totalSizeOfVarLengthField[fieldIndex] += fieldSize;

    /// <summary>
    /// Finalises the per-field widths and the record layout. Call once every record has been measured.
    /// </summary>
    public void CompleteCalculations()
    {
        for (int i = 0; i < _schema.FieldCount; i++)
        {
            if (_schema.GetFieldType(i).IsVariableLength())
            {
                // A variable-length field stores the end offset of its range, plus one bit for null.
                _maxBitsForField[i] = BitsRequiredForRepresentation(_totalSizeOfVarLengthField[i]) + 1;
            }

            _nullValueForField[i] = _maxBitsForField[i] == 64 ? -1L : (1L << _maxBitsForField[i]) - 1;

            _bitOffsetForField[i] = NumBitsPerRecord;
            NumBitsPerRecord += _maxBitsForField[i];
        }
    }

    /// <summary>The combined size of every variable-length field, in bytes.</summary>
    public long GetTotalSizeOfAllVarLengthData()
    {
        long total = 0;
        foreach (long size in _totalSizeOfVarLengthField)
        {
            total += size;
        }

        return total;
    }

    private static int BitsRequiredForRepresentation(long value) =>
        64 - BitOperations.LeadingZeroCount((ulong)(value + 1));
}
