/*
 *  Copyright 2016 Netflix, Inc.
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

namespace Hollow.Reference.Infrastructure.Storage;

/// <summary>
/// A blob fetched out of a remote store and put somewhere Hollow can read it more than once.
/// </summary>
/// <remarks>
/// <para>
/// Hollow reads a blob by seeking around it, and reads some of them twice — a snapshot is read once to
/// load it and again to checksum it. A response stream from S3 or Azure Blob Storage can do neither,
/// so it is written to a temporary file first, exactly as the Java reference implementation's
/// <c>S3BlobRetriever</c> does with its <c>TransferManager</c> download.
/// </para>
/// <para>
/// The file deletes itself when the stream is closed, so the caller has nothing to clean up and a
/// crashed process leaves at most the blobs it was reading at the time.
/// </para>
/// </remarks>
internal static class DownloadedBlob
{
    /// <summary>A path to download <paramref name="key"/> to, in a directory that exists.</summary>
    internal static string ReserveFileFor(string key)
    {
        string directory = Path.Combine(Path.GetTempPath(), "hollow-reference-blobs");
        Directory.CreateDirectory(directory);

        // The key is turned into a file name rather than hashed, so that whoever is looking at the
        // temporary directory while this runs can tell which blob is which.
        string name = key.Replace('/', '-').Replace('\\', '-');

        return Path.Combine(directory, $"{name}.{Guid.NewGuid():n}");
    }

    /// <summary>Opens a downloaded file, which is deleted as soon as the stream is closed.</summary>
    internal static FileStream OpenAndDeleteOnClose(string path) =>
        new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.DeleteOnClose | FileOptions.SequentialScan);

    /// <summary>Deletes a download that never became a stream, ignoring whatever goes wrong.</summary>
    internal static void Discard(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
