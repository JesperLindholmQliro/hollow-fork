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
using Hollow.Core.Index.Key;
using Hollow.Core.Schema;
using Hollow.Core.Write;

namespace Hollow.Compat;

/// <summary>
/// The dataset both implementations write, so that the bytes can be compared.
/// </summary>
/// <remarks>
/// <para>
/// The same dataset <c>FormatCompatibilityTests</c> pins the digest of, and for the same reason: it
/// uses every field type Netflix Hollow defines, every schema kind, null values in the
/// variable-length fields, and a sharded type. If two implementations agree on this, they agree on
/// the format.
/// </para>
/// <para>
/// Everything about it is fixed — the randomized tag, the record count, the values — because a
/// comparison of bytes is worth nothing if either side is free to vary. The Java half,
/// <c>tools/java/HollowCompat.java</c>, builds the identical dataset; if you change anything here,
/// change it there too, or the comparison reports a difference that is the harness's.
/// </para>
/// </remarks>
internal static class CanonicalDataset
{
    /// <summary>How many records of each type the first cycle holds.</summary>
    internal const int Records = 20;

    /// <summary>
    /// The tag a consumer uses to tell one delta chain from another.
    /// </summary>
    /// <remarks>
    /// Both implementations mint this from a random number generator and write it into the blob
    /// header, so it has to be pinned on both sides or every comparison fails on the header alone.
    /// </remarks>
    internal const long RandomizedTag = 0x0102030405060708L;

    /// <summary>The same, for the second cycle.</summary>
    internal const long SecondRandomizedTag = 0x1112131415161718L;

    /// <summary>
    /// Builds the first cycle.
    /// </summary>
    /// <param name="withDecimal">
    /// Whether to add a decimal field. This port's own field type, which Netflix Hollow has no name
    /// for — its reader fails on the schema, before any record — so the cross-implementation
    /// comparison leaves it off. Turning it on is a way to see this end's own bytes change, not a way
    /// to compare against anything.
    /// </param>
    internal static HollowWriteStateEngine FirstCycle(bool withDecimal)
    {
        HollowObjectSchema stringSchema = new("String", 1);
        stringSchema.AddField("value", FieldType.String);

        HollowObjectSchema everySchema = new("Every", withDecimal ? 9 : 8, new PrimaryKey("Every", "i"));
        everySchema.AddField("i", FieldType.Int);
        everySchema.AddField("l", FieldType.Long);
        everySchema.AddField("b", FieldType.Boolean);
        everySchema.AddField("f", FieldType.Float);
        everySchema.AddField("d", FieldType.Double);
        everySchema.AddField("s", FieldType.String);
        everySchema.AddField("y", FieldType.Bytes);
        everySchema.AddField("r", FieldType.Reference, "String");

        if (withDecimal)
        {
            everySchema.AddField("m", FieldType.Decimal);
        }

        HollowListSchema listSchema = new("ListOfEvery", "Every");
        HollowSetSchema setSchema = new("SetOfEvery", "Every", "i");
        HollowMapSchema mapSchema = new("MapOfEveryToString", "Every", "String", "i");

        HollowWriteStateEngine engine = new() { RandomizedTag = RandomizedTag };
        engine.AddTypeState(new HollowObjectTypeWriteState(stringSchema));
        engine.AddTypeState(new HollowObjectTypeWriteState(everySchema, numShards: 2));
        engine.AddTypeState(new HollowListTypeWriteState(listSchema));
        engine.AddTypeState(new HollowSetTypeWriteState(setSchema));
        engine.AddTypeState(new HollowMapTypeWriteState(mapSchema));

        HollowObjectWriteRecord stringRecord = new(stringSchema);
        HollowObjectWriteRecord everyRecord = new(everySchema);

        HollowListWriteRecord list = new();
        HollowSetWriteRecord set = new();
        HollowMapWriteRecord map = new();

        for (int i = 0; i < Records; i++)
        {
            int stringOrdinal = AddString(engine, stringRecord, i);
            int everyOrdinal = AddEvery(engine, everyRecord, i, stringOrdinal, withDecimal);

            list.AddElement(everyOrdinal);
            set.AddElement(everyOrdinal);
            map.AddEntry(everyOrdinal, stringOrdinal);
        }

        engine.Add("ListOfEvery", list);
        engine.Add("SetOfEvery", set);
        engine.Add("MapOfEveryToString", map);

        return engine;
    }

    /// <summary>
    /// Runs a second cycle over <paramref name="engine"/>: the even records are kept, the odd ones
    /// dropped, and five new ones added.
    /// </summary>
    /// <remarks>
    /// A delta is where two implementations have the most room to disagree — removals, additions and
    /// the gap encoding that carries them — so the comparison covers one.
    /// </remarks>
    internal static void SecondCycle(HollowWriteStateEngine engine, bool withDecimal)
    {
        engine.PrepareForNextCycle();

        // PrepareForNextCycle mints a fresh tag; pin it, or the delta's header carries a random number.
        engine.RandomizedTag = SecondRandomizedTag;

        HollowObjectSchema stringSchema = (HollowObjectSchema)engine.GetNonNullSchema("String");
        HollowObjectSchema everySchema = (HollowObjectSchema)engine.GetNonNullSchema("Every");

        HollowObjectWriteRecord stringRecord = new(stringSchema);
        HollowObjectWriteRecord everyRecord = new(everySchema);

        HollowListWriteRecord list = new();
        HollowSetWriteRecord set = new();
        HollowMapWriteRecord map = new();

        for (int i = 0; i < Records + 5; i++)
        {
            // The odd records of the first cycle are simply not re-added, which is how a record is
            // removed; 20..24 are new.
            if (i < Records && i % 2 != 0)
            {
                continue;
            }

            int stringOrdinal = AddString(engine, stringRecord, i);
            int everyOrdinal = AddEvery(engine, everyRecord, i, stringOrdinal, withDecimal);

            list.AddElement(everyOrdinal);
            set.AddElement(everyOrdinal);
            map.AddEntry(everyOrdinal, stringOrdinal);
        }

        engine.Add("ListOfEvery", list);
        engine.Add("SetOfEvery", set);
        engine.Add("MapOfEveryToString", map);
    }

    private static int AddString(HollowWriteStateEngine engine, HollowObjectWriteRecord record, int i)
    {
        record.Reset();
        record.SetString("value", string.Create(CultureInfo.InvariantCulture, $"string-{i}"));

        return engine.Add("String", record);
    }

    private static int AddEvery(
        HollowWriteStateEngine engine,
        HollowObjectWriteRecord record,
        int i,
        int stringOrdinal,
        bool withDecimal)
    {
        record.Reset();
        record.SetInt("i", i);
        record.SetLong("l", (long)i * int.MaxValue);
        record.SetBoolean("b", i % 2 == 0);
        record.SetFloat("f", i / 4f);
        record.SetDouble("d", i / 8d);

        // Every third record leaves the variable-length fields null, which exercises the null flag in
        // the range pointers.
        if (i % 3 != 0)
        {
            record.SetString("s", string.Create(CultureInfo.InvariantCulture, $"every-{i}"));
            record.SetBytes("y", [(byte)i, (byte)(i + 1), (byte)(i + 2)]);
        }

        record.SetReference("r", stringOrdinal);

        if (withDecimal)
        {
            record.SetDecimal("m", i / 100m);
        }

        return engine.Add("Every", record);
    }
}
