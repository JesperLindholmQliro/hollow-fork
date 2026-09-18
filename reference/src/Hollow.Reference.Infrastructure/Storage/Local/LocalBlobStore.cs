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

using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Hollow.Reference.Infrastructure.Storage.Local;

/// <summary>
/// A blob store that is a directory, standing in for S3 or Azure Blob Storage.
/// </summary>
/// <remarks>
/// <para>
/// Keys become paths, so the layout on disk is the layout in the bucket: a key of
/// <c>hollow-reference/snapshot/6f3a1c-20240101</c> is that path under <c>blobs/</c>. What a bucket
/// stores as user metadata is kept in a sidecar file beside the blob, which is the only part of this
/// that a real object store does not have to invent.
/// </para>
/// <para>
/// Writes land atomically: the content goes to a temporary name and is then moved over the target, so
/// a consumer listing the directory never sees a half-written blob. S3 gives that for free, and a
/// local mode that did not would fail in ways the real one cannot.
/// </para>
/// </remarks>
public sealed class LocalBlobStore : IHollowBlobStore
{
    private const string MetadataSuffix = ".metadata";

    private static readonly JsonSerializerOptions MetadataJson = new() { WriteIndented = false };

    private readonly string _blobDirectory;
    private readonly SimulatedLatency _latency;

    public LocalBlobStore(string rootPath, SimulatedLatency? latency = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        _blobDirectory = Path.Combine(rootPath, "blobs");
        _latency = latency ?? SimulatedLatency.None;

        Directory.CreateDirectory(_blobDirectory);
    }

    /// <summary>The directory blobs are kept in, for anything that wants to say where they went.</summary>
    public string BlobDirectory => _blobDirectory;

    /// <inheritdoc />
    public string Description => Path.GetFullPath(_blobDirectory);

    public async Task WriteAsync(
        string key,
        Stream content,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(metadata);

        string path = ResolvePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        string temporary = $"{path}.{Guid.NewGuid():n}.tmp";
        long written;

        try
        {
            await using (FileStream target = File.Create(temporary))
            {
                await content.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                written = target.Length;
            }

            await _latency.DelayAsync(written, cancellationToken).ConfigureAwait(false);

            await File.WriteAllTextAsync(
                temporary + MetadataSuffix,
                JsonSerializer.Serialize(metadata, MetadataJson),
                cancellationToken).ConfigureAwait(false);

            // The metadata moves first so that a blob is never visible without it. A reader that finds
            // the metadata but not yet the blob simply finds nothing, which is the case it already
            // handles; the other way round it would read a blob and believe it had no versions.
            File.Move(temporary + MetadataSuffix, path + MetadataSuffix, overwrite: true);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            Delete(temporary);
            Delete(temporary + MetadataSuffix);
            throw;
        }

        static void Delete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Cleaning up after a failure is best-effort; the original failure is the interesting one.
            }
        }
    }

    public async Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        string path = ResolvePath(key);

        if (!File.Exists(path))
        {
            await _latency.DelayAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return null;
        }

        await _latency.DelayAsync(new FileInfo(path).Length, cancellationToken).ConfigureAwait(false);

        try
        {
            return new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            // Deleted between the check and the open, which in a bucket would be a 404 too.
            return null;
        }
    }

    public async Task<IReadOnlyDictionary<string, string>?> ReadMetadataAsync(
        string key, CancellationToken cancellationToken = default)
    {
        string path = ResolvePath(key);

        await _latency.DelayAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!File.Exists(path))
        {
            return null;
        }

        string metadataPath = path + MetadataSuffix;

        if (!File.Exists(metadataPath))
        {
            return Empty();
        }

        try
        {
            string json = await File.ReadAllTextAsync(metadataPath, cancellationToken).ConfigureAwait(false);

            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, MetadataJson) ?? Empty();
        }
        catch (Exception e) when (e is FileNotFoundException or JsonException)
        {
            return Empty();
        }

        static Dictionary<string, string> Empty() => new(StringComparer.Ordinal);
    }

    public async IAsyncEnumerable<string> ListKeysAsync(
        string keyPrefix,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyPrefix);

        await _latency.DelayAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!Directory.Exists(_blobDirectory))
        {
            yield break;
        }

        foreach (string path in Directory.EnumerateFiles(_blobDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (path.EndsWith(MetadataSuffix, StringComparison.Ordinal)
                || path.EndsWith(".tmp", StringComparison.Ordinal))
            {
                continue;
            }

            string key = Path
                .GetRelativePath(_blobDirectory, path)
                .Replace(Path.DirectorySeparatorChar, '/');

            if (key.StartsWith(keyPrefix, StringComparison.Ordinal))
            {
                yield return key;
            }
        }
    }

    private string ResolvePath(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        string path = Path.GetFullPath(
            Path.Combine(_blobDirectory, key.Replace('/', Path.DirectorySeparatorChar)));

        // Keys are ours rather than a caller's, but a store that writes wherever it is pointed is worth
        // ruling out once rather than reasoning about at every call site.
        if (!path.StartsWith(_blobDirectory, StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{key}' resolves outside the blob directory.", nameof(key));
        }

        return path;
    }
}
