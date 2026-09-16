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

import com.netflix.hollow.core.index.key.PrimaryKey;
import com.netflix.hollow.core.read.engine.HollowBlobReader;
import com.netflix.hollow.core.read.engine.HollowReadStateEngine;
import com.netflix.hollow.core.schema.HollowListSchema;
import com.netflix.hollow.core.schema.HollowMapSchema;
import com.netflix.hollow.core.schema.HollowObjectSchema;
import com.netflix.hollow.core.schema.HollowObjectSchema.FieldType;
import com.netflix.hollow.core.schema.HollowSchema;
import com.netflix.hollow.core.schema.HollowSetSchema;
import com.netflix.hollow.core.write.HollowBlobWriter;
import com.netflix.hollow.core.write.HollowListTypeWriteState;
import com.netflix.hollow.core.write.HollowListWriteRecord;
import com.netflix.hollow.core.write.HollowMapTypeWriteState;
import com.netflix.hollow.core.write.HollowMapWriteRecord;
import com.netflix.hollow.core.write.HollowObjectTypeWriteState;
import com.netflix.hollow.core.write.HollowObjectWriteRecord;
import com.netflix.hollow.core.write.HollowSetTypeWriteState;
import com.netflix.hollow.core.write.HollowSetWriteRecord;
import com.netflix.hollow.core.write.HollowWriteStateEngine;
import com.netflix.hollow.tools.diff.HollowDiff;
import com.netflix.hollow.tools.diff.HollowTypeDiff;
import com.netflix.hollow.tools.diff.count.HollowFieldDiff;
import com.netflix.hollow.tools.stringifier.HollowRecordJsonStringifier;

import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.file.Files;
import java.nio.file.Path;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.ArrayList;
import java.util.BitSet;
import java.util.Comparator;
import java.util.List;

/**
 * The Java half of {@code dotnet/tools/compare-with-java.sh}: the same three commands
 * {@code Hollow.Compat} offers, against Netflix Hollow rather than against the .NET port.
 *
 * <p>A single file compiled on the fly by the script rather than a module in the Gradle build,
 * because it is a test fixture for the port and has no business in the Java project's artifacts.
 * It depends on nothing but Hollow itself.
 *
 * <p>The dataset below must stay identical to {@code Hollow.Compat}'s {@code CanonicalDataset}. If
 * you change one, change the other, or the comparison reports a difference that is the harness's
 * rather than the format's.
 */
public final class HollowCompat {

    /** How many records of each type the first cycle holds. */
    private static final int RECORDS = 20;

    /**
     * Fixed, because both implementations otherwise mint it at random and write it into the blob
     * header, so every comparison would fail on the header alone.
     */
    private static final long RANDOMIZED_TAG = 0x0102030405060708L;

    /** The same, for the second cycle. */
    private static final long SECOND_RANDOMIZED_TAG = 0x1112131415161718L;

    private HollowCompat() {
    }

    public static void main(String[] args) throws Exception {
        if (args.length == 0 || args[0].equals("-h") || args[0].equals("--help")) {
            System.out.println("HollowCompat write <dir> | describe <blob> | diff <from> <to>");
            System.exit(args.length == 0 ? 1 : 0);
        }

        switch (args[0]) {
            case "write":
                System.exit(write(args[1]));
                break;
            case "describe":
                System.exit(describe(args[1]));
                break;
            case "diff":
                System.exit(diff(args[1], args[2]));
                break;
            default:
                System.err.println("hollow-compat: unknown command " + args[0]);
                System.exit(1);
        }
    }

    // ----------------------------------------------------------------- write

    private static int write(String directory) throws IOException, NoSuchAlgorithmException {
        Files.createDirectories(Path.of(directory));

        HollowWriteStateEngine engine = firstCycle();
        engine.prepareForWrite();

        HollowBlobWriter writer = new HollowBlobWriter(engine);

        File snapshot = new File(directory, "snapshot");
        writeTo(snapshot, out -> writer.writeSnapshot(out));

        secondCycle(engine);
        engine.prepareForWrite();

        File secondSnapshot = new File(directory, "snapshot2");
        File delta = new File(directory, "delta");
        File reverseDelta = new File(directory, "reversedelta");

        writeTo(secondSnapshot, out -> writer.writeSnapshot(out));
        writeTo(delta, out -> writer.writeDelta(out));
        writeTo(reverseDelta, out -> writer.writeReverseDelta(out));

        File[] written = {snapshot, secondSnapshot, delta, reverseDelta};

        StringBuilder manifest = new StringBuilder()
                .append("{\"implementation\":\"Java\",\"decimal\":false,\"records\":")
                .append(RECORDS)
                .append(",\"files\":{");

        for (int i = 0; i < written.length; i++) {
            byte[] bytes = Files.readAllBytes(written[i].toPath());
            String digest = sha256(bytes);

            if (i > 0) {
                manifest.append(',');
            }

            manifest.append('"').append(written[i].getName()).append("\":{\"bytes\":")
                    .append(bytes.length).append(",\"sha256\":\"").append(digest).append("\"}");

            System.out.printf("%-14s %10d bytes  %s%n", written[i].getName(), bytes.length, digest);
        }

        manifest.append("}}");

        Files.writeString(Path.of(directory, "manifest.json"), manifest.toString());

        return 0;
    }

    // -------------------------------------------------------------- describe

    private static int describe(String blob) throws IOException {
        HollowReadStateEngine engine = read(blob);
        HollowRecordJsonStringifier stringifier = new HollowRecordJsonStringifier(false, true);

        List<HollowSchema> schemas = new ArrayList<>(engine.getSchemas());
        schemas.sort(Comparator.comparing(HollowSchema::getName));

        for (HollowSchema schema : schemas) {
            System.out.println(schema.toString());
        }

        System.out.println();

        for (HollowSchema schema : schemas) {
            BitSet populated = engine.getTypeState(schema.getName()).getPopulatedOrdinals();

            for (int ordinal = populated.nextSetBit(0);
                    ordinal != -1;
                    ordinal = populated.nextSetBit(ordinal + 1)) {
                System.out.println(schema.getName() + "[" + ordinal + "] "
                        + stringifier.stringify(engine, schema.getName(), ordinal));
            }
        }

        return 0;
    }

    // ------------------------------------------------------------------ diff

    private static int diff(String from, String to) throws IOException {
        // Auto-discover type diffs, and include non-keyed types: a single-field wrapper like String
        // gets keyed on its one field, and leaving it out passes over most of the records.
        HollowDiff diff = new HollowDiff(read(from), read(to), true, true);
        diff.calculateDiffs();

        long score = 0;
        int unmatched = 0;

        List<HollowTypeDiff> typeDiffs = new ArrayList<>(diff.getTypeDiffs());
        typeDiffs.sort(Comparator.comparing(HollowTypeDiff::getTypeName));

        for (HollowTypeDiff typeDiff : typeDiffs) {
            System.out.printf("%s: %d -> %d, %d matched, %d only in from, %d only in to%n",
                    typeDiff.getTypeName(),
                    typeDiff.getTotalItemsInFromState(),
                    typeDiff.getTotalItemsInToState(),
                    typeDiff.getTotalNumberOfMatches(),
                    typeDiff.getUnmatchedOrdinalsInFrom().size(),
                    typeDiff.getUnmatchedOrdinalsInTo().size());

            List<HollowFieldDiff> fieldDiffs = new ArrayList<>(typeDiff.getFieldDiffs());
            fieldDiffs.sort(Comparator.comparingLong(HollowFieldDiff::getTotalDiffScore).reversed());

            for (HollowFieldDiff fieldDiff : fieldDiffs) {
                System.out.printf("    %s: %d record(s) differ%n",
                        fieldDiff.getFieldIdentifier(), fieldDiff.getNumDiffs());
                score += fieldDiff.getTotalDiffScore();
            }

            unmatched += typeDiff.getUnmatchedOrdinalsInFrom().size()
                    + typeDiff.getUnmatchedOrdinalsInTo().size();
        }

        System.out.println();
        System.out.println(score == 0 && unmatched == 0
                ? "The two states hold the same records."
                : "Total diff score " + score + ", with " + unmatched + " record(s) on one side only.");

        return score == 0 && unmatched == 0 ? 0 : 2;
    }

    // --------------------------------------------------------------- dataset

    private static HollowWriteStateEngine firstCycle() {
        HollowObjectSchema stringSchema = new HollowObjectSchema("String", 1);
        stringSchema.addField("value", FieldType.STRING);

        HollowObjectSchema everySchema =
                new HollowObjectSchema("Every", 8, new PrimaryKey("Every", "i"));
        everySchema.addField("i", FieldType.INT);
        everySchema.addField("l", FieldType.LONG);
        everySchema.addField("b", FieldType.BOOLEAN);
        everySchema.addField("f", FieldType.FLOAT);
        everySchema.addField("d", FieldType.DOUBLE);
        everySchema.addField("s", FieldType.STRING);
        everySchema.addField("y", FieldType.BYTES);
        everySchema.addField("r", FieldType.REFERENCE, "String");

        HollowListSchema listSchema = new HollowListSchema("ListOfEvery", "Every");
        HollowSetSchema setSchema = new HollowSetSchema("SetOfEvery", "Every", "i");
        HollowMapSchema mapSchema = new HollowMapSchema("MapOfEveryToString", "Every", "String", "i");

        HollowWriteStateEngine engine = new HollowWriteStateEngine();
        engine.overrideNextStateRandomizedTag(RANDOMIZED_TAG);

        engine.addTypeState(new HollowObjectTypeWriteState(stringSchema));
        engine.addTypeState(new HollowObjectTypeWriteState(everySchema, 2));
        engine.addTypeState(new HollowListTypeWriteState(listSchema));
        engine.addTypeState(new HollowSetTypeWriteState(setSchema));
        engine.addTypeState(new HollowMapTypeWriteState(mapSchema));

        HollowObjectWriteRecord stringRecord = new HollowObjectWriteRecord(stringSchema);
        HollowObjectWriteRecord everyRecord = new HollowObjectWriteRecord(everySchema);

        HollowListWriteRecord list = new HollowListWriteRecord();
        HollowSetWriteRecord set = new HollowSetWriteRecord();
        HollowMapWriteRecord map = new HollowMapWriteRecord();

        for (int i = 0; i < RECORDS; i++) {
            int stringOrdinal = addString(engine, stringRecord, i);
            int everyOrdinal = addEvery(engine, everyRecord, i, stringOrdinal);

            list.addElement(everyOrdinal);
            set.addElement(everyOrdinal);
            map.addEntry(everyOrdinal, stringOrdinal);
        }

        engine.add("ListOfEvery", list);
        engine.add("SetOfEvery", set);
        engine.add("MapOfEveryToString", map);

        return engine;
    }

    private static void secondCycle(HollowWriteStateEngine engine) {
        engine.prepareForNextCycle();

        // prepareForNextCycle mints a fresh tag; pin it, or the delta's header carries a random number.
        engine.overrideNextStateRandomizedTag(SECOND_RANDOMIZED_TAG);

        HollowObjectSchema stringSchema =
                (HollowObjectSchema) engine.getTypeState("String").getSchema();
        HollowObjectSchema everySchema =
                (HollowObjectSchema) engine.getTypeState("Every").getSchema();

        HollowObjectWriteRecord stringRecord = new HollowObjectWriteRecord(stringSchema);
        HollowObjectWriteRecord everyRecord = new HollowObjectWriteRecord(everySchema);

        HollowListWriteRecord list = new HollowListWriteRecord();
        HollowSetWriteRecord set = new HollowSetWriteRecord();
        HollowMapWriteRecord map = new HollowMapWriteRecord();

        for (int i = 0; i < RECORDS + 5; i++) {
            // The odd records of the first cycle are simply not re-added, which is how a record is
            // removed; 20..24 are new.
            if (i < RECORDS && i % 2 != 0) {
                continue;
            }

            int stringOrdinal = addString(engine, stringRecord, i);
            int everyOrdinal = addEvery(engine, everyRecord, i, stringOrdinal);

            list.addElement(everyOrdinal);
            set.addElement(everyOrdinal);
            map.addEntry(everyOrdinal, stringOrdinal);
        }

        engine.add("ListOfEvery", list);
        engine.add("SetOfEvery", set);
        engine.add("MapOfEveryToString", map);
    }

    private static int addString(
            HollowWriteStateEngine engine, HollowObjectWriteRecord record, int i) {
        record.reset();
        record.setString("value", "string-" + i);

        return engine.add("String", record);
    }

    private static int addEvery(
            HollowWriteStateEngine engine, HollowObjectWriteRecord record, int i, int stringOrdinal) {
        record.reset();
        record.setInt("i", i);
        record.setLong("l", (long) i * Integer.MAX_VALUE);
        record.setBoolean("b", i % 2 == 0);
        record.setFloat("f", i / 4f);
        record.setDouble("d", i / 8d);

        // Every third record leaves the variable-length fields null, which exercises the null flag in
        // the range pointers.
        if (i % 3 != 0) {
            record.setString("s", "every-" + i);
            record.setBytes("y", new byte[] {(byte) i, (byte) (i + 1), (byte) (i + 2)});
        }

        record.setReference("r", stringOrdinal);

        return engine.add("Every", record);
    }

    // -------------------------------------------------------------- plumbing

    /** A write that may fail, so the try-with-resources below stays one line. */
    private interface BlobWrite {
        void writeTo(OutputStream out) throws IOException;
    }

    private static void writeTo(File file, BlobWrite write) throws IOException {
        try (OutputStream out = new FileOutputStream(file)) {
            write.writeTo(out);
        }
    }

    private static HollowReadStateEngine read(String blob) throws IOException {
        HollowReadStateEngine engine = new HollowReadStateEngine();

        try (InputStream in = new FileInputStream(blob)) {
            new HollowBlobReader(engine).readSnapshot(in);
        }

        return engine;
    }

    private static String sha256(byte[] bytes) throws NoSuchAlgorithmException {
        StringBuilder hex = new StringBuilder();

        for (byte b : MessageDigest.getInstance("SHA-256").digest(bytes)) {
            hex.append(String.format("%02X", b));
        }

        return hex.toString();
    }
}
