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

using System.Security.Cryptography;
using Hollow.Core.Schema;
using Hollow.Core.Write;

namespace Hollow.Tests.Core;

/// <summary>
/// Pins the bytes of a blob that uses only the field types Netflix Hollow defines.
/// </summary>
/// <remarks>
/// <para>
/// This port adds one field type Netflix Hollow does not have,
/// <see cref="FieldType.Decimal"/> — see the "Format extension" section of <c>PORTING.md</c>. The rule
/// that keeps the extension honest is that a dataset which uses no decimal field must serialise exactly
/// as it did before the extension existed, so that such a blob stays readable by a Java Hollow
/// consumer.
/// </para>
/// <para>
/// A digest is a blunt instrument, but that bluntness is the point: it fails on any change to the byte
/// stream, whether or not anybody thought to write a test for that part of it. If one of these fails,
/// the change under test altered the blob format. That is not necessarily wrong — but it has to be a
/// decision, not a side effect, and the "Format extension" section has to be updated to say so.
/// </para>
/// </remarks>
public class FormatCompatibilityTests
{
    /// <summary>
    /// Builds a dataset exercising every field type Netflix Hollow defines, every schema kind, null
    /// values, and a sharded type.
    /// </summary>
    private static HollowWriteStateEngine ClassicDataset()
    {
        HollowObjectSchema stringSchema = new("String", 1);
        stringSchema.AddField("value", FieldType.String);

        HollowObjectSchema everySchema = new("Every", 8, new Hollow.Core.Index.Key.PrimaryKey("Every", "i"));
        everySchema.AddField("i", FieldType.Int);
        everySchema.AddField("l", FieldType.Long);
        everySchema.AddField("b", FieldType.Boolean);
        everySchema.AddField("f", FieldType.Float);
        everySchema.AddField("d", FieldType.Double);
        everySchema.AddField("s", FieldType.String);
        everySchema.AddField("y", FieldType.Bytes);
        everySchema.AddField("r", FieldType.Reference, "String");

        HollowListSchema listSchema = new("ListOfEvery", "Every");
        HollowSetSchema setSchema = new("SetOfEvery", "Every", "i");
        HollowMapSchema mapSchema = new("MapOfEveryToString", "Every", "String", "i");

        HollowWriteStateEngine engine = new() { RandomizedTag = 0x0102030405060708L };
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

        for (int i = 0; i < 20; i++)
        {
            stringRecord.Reset();
            stringRecord.SetString("value", $"string-{i}");
            int stringOrdinal = engine.Add("String", stringRecord);

            everyRecord.Reset();
            everyRecord.SetInt("i", i);
            everyRecord.SetLong("l", (long)i * int.MaxValue);
            everyRecord.SetBoolean("b", i % 2 == 0);
            everyRecord.SetFloat("f", i / 4f);
            everyRecord.SetDouble("d", i / 8d);

            // Every third record leaves the variable-length fields null, which exercises the null flag
            // in the range pointers.
            if (i % 3 != 0)
            {
                everyRecord.SetString("s", $"every-{i}");
                everyRecord.SetBytes("y", [(byte)i, (byte)(i + 1), (byte)(i + 2)]);
            }

            everyRecord.SetReference("r", stringOrdinal);

            int everyOrdinal = engine.Add("Every", everyRecord);

            list.AddElement(everyOrdinal);
            set.AddElement(everyOrdinal);
            map.AddEntry(everyOrdinal, stringOrdinal);
        }

        engine.Add("ListOfEvery", list);
        engine.Add("SetOfEvery", set);
        engine.Add("MapOfEveryToString", map);

        return engine;
    }

    private static string Digest(Action<Stream> write)
    {
        using MemoryStream stream = new();
        write(stream);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    /// <summary>
    /// The snapshot of a dataset using no decimal field must be byte-for-byte what it was before the
    /// decimal extension was added.
    /// </summary>
    [Fact]
    public void AClassicSnapshotIsUnchangedByTheDecimalExtension()
    {
        Assert.Equal(
            "71D4DF4B6340D5B1CB62C2C6768492A77A53751E4BC93414C2EC801636CCDE59",
            Digest(stream => new HollowBlobWriter(ClassicDataset()).WriteSnapshot(stream)));
    }

    /// <summary>
    /// And so must a delta between two such datasets.
    /// </summary>
    [Fact]
    public void AClassicDeltaIsUnchangedByTheDecimalExtension()
    {
        Assert.Equal(
            "E165CF8074486515F6C1EE0C5FFDA055E64D9F1320EDA376340494AC6CC1D7F6",
            Digest(stream => new HollowBlobWriter(SecondCycle()).WriteDelta(stream)));
    }

    /// <summary>
    /// Runs a second cycle over the classic dataset: the even records are kept, the odd ones dropped,
    /// and five new ones added.
    /// </summary>
    private static HollowWriteStateEngine SecondCycle()
    {
        HollowWriteStateEngine engine = ClassicDataset();

        using (MemoryStream snapshot = new())
        {
            new HollowBlobWriter(engine).WriteSnapshot(snapshot);
        }

        engine.PrepareForNextCycle();
        engine.RandomizedTag = 0x1112131415161718L;

        HollowObjectSchema stringSchema = (HollowObjectSchema)engine.GetTypeState("String")!.Schema;
        HollowObjectSchema everySchema = (HollowObjectSchema)engine.GetTypeState("Every")!.Schema;

        HollowObjectWriteRecord stringRecord = new(stringSchema);
        HollowObjectWriteRecord everyRecord = new(everySchema);

        for (int i = 0; i < 25; i++)
        {
            if (i < 20 && i % 2 != 0)
            {
                continue;
            }

            stringRecord.Reset();
            stringRecord.SetString("value", $"string-{i}");
            int stringOrdinal = engine.Add("String", stringRecord);

            everyRecord.Reset();
            everyRecord.SetInt("i", i);
            everyRecord.SetLong("l", (long)i * int.MaxValue);
            everyRecord.SetBoolean("b", i % 2 == 0);
            everyRecord.SetFloat("f", i / 4f);
            everyRecord.SetDouble("d", i / 8d);

            if (i % 3 != 0)
            {
                everyRecord.SetString("s", $"every-{i}");
                everyRecord.SetBytes("y", [(byte)i, (byte)(i + 1), (byte)(i + 2)]);
            }

            everyRecord.SetReference("r", stringOrdinal);
            engine.Add("Every", everyRecord);
        }

        return engine;
    }

    /// <summary>
    /// A schema without a decimal field serialises to the same bytes whether or not the reader knows
    /// about decimals, because the extension adds a new field-type name rather than renumbering the
    /// existing ones.
    /// </summary>
    [Fact]
    public void TheOriginalFieldTypeNamesAreUnchanged()
    {
        Assert.Equal("REFERENCE", FieldType.Reference.ToWireName());
        Assert.Equal("INT", FieldType.Int.ToWireName());
        Assert.Equal("LONG", FieldType.Long.ToWireName());
        Assert.Equal("BOOLEAN", FieldType.Boolean.ToWireName());
        Assert.Equal("FLOAT", FieldType.Float.ToWireName());
        Assert.Equal("DOUBLE", FieldType.Double.ToWireName());
        Assert.Equal("STRING", FieldType.String.ToWireName());
        Assert.Equal("BYTES", FieldType.Bytes.ToWireName());

        // The extension's own name, which no Java Hollow consumer will recognise.
        Assert.Equal("DECIMAL", FieldType.Decimal.ToWireName());
    }
}
