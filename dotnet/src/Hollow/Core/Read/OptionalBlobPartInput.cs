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

using Hollow.Core.Memory;

namespace Hollow.Core.Read;

/// <summary>
/// The optional blob parts a consumer fetched, ready to be read alongside the main blob.
/// </summary>
/// <remarks>
/// A part is named, because the main blob's header says which types live in which part and the reader
/// matches them up by that name. Handing over a part under the wrong name is refused rather than
/// silently read.
/// </remarks>
public sealed class OptionalBlobPartInput : IDisposable
{
    private readonly Dictionary<string, Func<MemoryMode, HollowBlobInput>> _inputsByPartName =
        new(StringComparer.Ordinal);

    private readonly List<IDisposable> _opened = [];

    /// <summary>The names of the parts held.</summary>
    public IReadOnlyCollection<string> PartNames => _inputsByPartName.Keys;

    /// <summary>Adds a part read from a file, which a memory-mapped read needs a path for.</summary>
    public void AddInput(string partName, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(partName);
        ArgumentException.ThrowIfNullOrEmpty(path);

        _inputsByPartName[partName] = mode => mode == MemoryMode.SharedMemoryLazy
            ? HollowBlobInput.Mapped(path)
            : HollowBlobInput.Serial(File.OpenRead(path));
    }

    /// <summary>Adds a part read from a stream.</summary>
    /// <remarks>
    /// A stream can only be read once, so the part it carries can only be applied once — which is all
    /// a transition needs.
    /// </remarks>
    public void AddInput(string partName, Stream stream)
    {
        ArgumentException.ThrowIfNullOrEmpty(partName);
        ArgumentNullException.ThrowIfNull(stream);

        _inputsByPartName[partName] = _ => HollowBlobInput.Serial(stream);
    }

    /// <summary>Adds a part read from bytes already in hand.</summary>
    public void AddInput(string partName, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(partName);
        ArgumentNullException.ThrowIfNull(bytes);

        _inputsByPartName[partName] = _ => HollowBlobInput.Serial(bytes);
    }

    /// <summary>
    /// Opens every part for reading in <paramref name="memoryMode"/>.
    /// </summary>
    /// <remarks>
    /// The inputs are held for disposal, so that a caller closing this closes what it opened.
    /// </remarks>
    public IReadOnlyDictionary<string, HollowBlobInput> OpenByPartName(MemoryMode memoryMode)
    {
        Dictionary<string, HollowBlobInput> inputs = new(StringComparer.Ordinal);

        foreach ((string part, Func<MemoryMode, HollowBlobInput> open) in _inputsByPartName)
        {
            HollowBlobInput input = open(memoryMode);

            _opened.Add(input);
            inputs[part] = input;
        }

        return inputs;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        List<Exception>? failures = null;

        foreach (IDisposable input in _opened)
        {
            try
            {
                input.Dispose();
            }
            catch (IOException e)
            {
                // Every part gets closed even if one of them will not, and the caller hears about all
                // of the failures rather than the last one.
                (failures ??= []).Add(e);
            }
        }

        _opened.Clear();

        if (failures is not null)
        {
            throw new AggregateException("one or more optional blob parts could not be closed", failures);
        }
    }
}
