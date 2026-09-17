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

namespace Hollow.Explorer;

/// <summary>
/// A writer that escapes what it is given before passing it on.
/// </summary>
/// <remarks>
/// The record being shown is written straight into the page by whatever stringifier the reader asked
/// for, and a record holds text someone else put there. Escaping on the way through means the page is
/// safe without the record having to be built up in memory first, which for a record holding a large
/// collection is the difference that matters.
/// </remarks>
/// <param name="writer">Where the escaped text goes.</param>
public sealed class HtmlEscapingTextWriter(TextWriter writer) : TextWriter
{
    private readonly TextWriter _writer = writer ?? throw new ArgumentNullException(nameof(writer));

    /// <inheritdoc />
    public override Encoding Encoding => _writer.Encoding;

    /// <inheritdoc />
    public override IFormatProvider FormatProvider => _writer.FormatProvider;

    /// <inheritdoc />
    public override void Write(char value) => Write(new ReadOnlySpan<char>(in value));

    /// <inheritdoc />
    public override void Write(string? value)
    {
        if (value is not null)
        {
            Write(value.AsSpan());
        }
    }

    /// <inheritdoc />
    public override void Write(char[] buffer, int index, int count) =>
        Write(new ReadOnlySpan<char>(buffer, index, count));

    /// <summary>
    /// Writes <paramref name="value"/> with the characters that would be read as markup replaced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This escapes by hand rather than through <see cref="System.Text.Encodings.Web.HtmlEncoder"/>
    /// because the record is written inside a <c>&lt;pre&gt;</c>, and every encoder that class offers
    /// escapes newlines: they are control characters, which no set of allowed Unicode ranges can let
    /// through. A record laid out over twenty lines would arrive as one line of <c>&amp;#xA;</c>.
    /// </para>
    /// <para>
    /// What is left is what Java's <c>escapeHtml4</c> does to the same text. It is enough because the
    /// destination is element content and nothing else: with <c>&amp;</c> and <c>&lt;</c> replaced
    /// there is no way out of it, and the other three are replaced because it costs nothing to.
    /// </para>
    /// </remarks>
    public override void Write(ReadOnlySpan<char> value)
    {
        int written = 0;

        for (int i = 0; i < value.Length; i++)
        {
            string? replacement = value[i] switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&#39;",
                _ => null,
            };

            if (replacement is null)
            {
                continue;
            }

            _writer.Write(value[written..i]);
            _writer.Write(replacement);
            written = i + 1;
        }

        _writer.Write(value[written..]);
    }

    /// <inheritdoc />
    public override void Flush() => _writer.Flush();

    /// <summary>
    /// Leaves the wrapped writer alone, because it is the page's and outlives this one.
    /// </summary>
    /// <remarks>
    /// The base call is still made. <see cref="TextWriter"/> holds nothing that needs releasing today,
    /// but skipping the chain is the kind of omission that becomes a leak the moment it does.
    /// </remarks>
    protected override void Dispose(bool disposing) => base.Dispose(disposing);
}
