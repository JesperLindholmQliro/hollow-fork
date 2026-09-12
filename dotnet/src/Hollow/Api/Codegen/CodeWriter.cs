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

using System.Text;

namespace Hollow.Api.Codegen;

/// <summary>
/// Builds a source file, keeping track of indentation.
/// </summary>
/// <remarks>
/// Java's generators concatenate strings and indent by hand, which is why its emitted code is
/// inconsistently formatted. This costs nothing and means the output does not have to be run through a
/// formatter to be readable.
/// </remarks>
internal sealed class CodeWriter
{
    private readonly StringBuilder _text = new();

    private int _indent;
    private bool _atLineStart = true;

    /// <summary>Writes a line at the current indentation, or a blank line when empty.</summary>
    internal CodeWriter Line(string text = "")
    {
        if (text.Length == 0)
        {
            _text.Append('\n');
            _atLineStart = true;

            return this;
        }

        Indent();
        _text.Append(text).Append('\n');
        _atLineStart = true;

        return this;
    }

    /// <summary>Writes each of <paramref name="lines"/> at the current indentation.</summary>
    internal CodeWriter Lines(IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            Line(line);
        }

        return this;
    }

    /// <summary>Writes a blank line unless one has just been written.</summary>
    internal CodeWriter Blank()
    {
        if (_text.Length > 0 && !_text.ToString().EndsWith("\n\n", StringComparison.Ordinal))
        {
            Line();
        }

        return this;
    }

    /// <summary>Writes an XML doc comment, wrapping the text into <c>&lt;summary&gt;</c>.</summary>
    internal CodeWriter Doc(string summary)
    {
        Line($"/// <summary>{summary}</summary>");

        return this;
    }

    /// <summary>Opens a brace-delimited block, indenting what follows.</summary>
    internal Block Open(string? header = null)
    {
        if (header is not null)
        {
            Line(header);
        }

        Line("{");
        _indent++;

        return new Block(this);
    }

    /// <inheritdoc />
    public override string ToString() => _text.ToString();

    private void Indent()
    {
        if (!_atLineStart)
        {
            return;
        }

        _text.Append(' ', _indent * 4);
        _atLineStart = false;
    }

    private void Close()
    {
        _indent--;
        Line("}");
    }

    /// <summary>Closes the block it was opened for.</summary>
    internal readonly struct Block(CodeWriter writer) : IDisposable
    {
        /// <inheritdoc />
        public void Dispose() => writer.Close();
    }
}
