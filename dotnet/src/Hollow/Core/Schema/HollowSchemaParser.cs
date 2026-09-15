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

namespace Hollow.Core.Schema;

/// <summary>
/// Reads schemas back from the text form a schema's <c>ToString()</c> writes.
/// </summary>
/// <remarks>
/// <para>
/// A dataset's schemas normally arrive inside the blob. This is for the times they arrive as text
/// instead: a data model checked into a repository, a schema pasted into a bug report, or a test that
/// would rather say what it means than build schemas field by field.
/// </para>
/// <para>
/// The grammar is small enough to state:
/// </para>
/// <code>
/// TypeName @PrimaryKey(path, path) { int fieldName; OtherType reference; }
/// TypeName List&lt;ElementType&gt;;
/// TypeName Set&lt;ElementType&gt; @HashKey(path);
/// TypeName Map&lt;KeyType,ValueType&gt; @HashKey(path);
/// </code>
/// <para>
/// A field type that is not one of the eight built-in names is a reference to the type it names, which
/// is why a misspelled <c>string</c> becomes a reference to a type called <c>strnig</c> rather than an
/// error. That is Java's behaviour and the format gives no way to tell the two apart.
/// </para>
/// <para>
/// Named <c>HollowSchemaParser</c> in Java, with <c>parseSchema</c> and
/// <c>parseCollectionOfSchemas</c>; here they are <see cref="Parse"/> and
/// <see cref="ParseCollection(string)"/>, and a failure is a <see cref="FormatException"/> rather than
/// Java's <c>IOException</c> — nothing here does any I/O beyond reading the text it was handed.
/// </para>
/// </remarks>
public static class HollowSchemaParser
{
    /// <summary>The field type names the schema syntax spells out rather than treating as references.</summary>
    /// <remarks>
    /// <c>decimal</c> is this port's own — see the format extension section of <c>PORTING.md</c>. It has
    /// to be listed here, or a schema written by this port would read back with its decimal fields
    /// turned into references to a type called <c>decimal</c>.
    /// </remarks>
    private static readonly Dictionary<string, FieldType> BuiltInFieldTypes = new(StringComparer.Ordinal)
    {
        ["int"] = FieldType.Int,
        ["long"] = FieldType.Long,
        ["float"] = FieldType.Float,
        ["double"] = FieldType.Double,
        ["boolean"] = FieldType.Boolean,
        ["string"] = FieldType.String,
        ["bytes"] = FieldType.Bytes,
        ["decimal"] = FieldType.Decimal,
    };

    /// <summary>Reads the one schema <paramref name="schema"/> holds.</summary>
    /// <exception cref="FormatException">
    /// The text is not a schema, or holds nothing at all.
    /// </exception>
    public static HollowSchema Parse(string schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        Tokenizer tokenizer = new(schema);

        return ParseSchema(tokenizer)
            ?? throw new FormatException("the text holds no schema");
    }

    /// <summary>Reads every schema <paramref name="schemas"/> holds, in the order they appear.</summary>
    /// <exception cref="FormatException">One of them is not a schema.</exception>
    public static IReadOnlyList<HollowSchema> ParseCollection(string schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);

        Tokenizer tokenizer = new(schemas);
        List<HollowSchema> parsed = [];

        while (ParseSchema(tokenizer) is { } schema)
        {
            parsed.Add(schema);
        }

        return parsed;
    }

    /// <summary>Reads every schema <paramref name="reader"/> holds, in the order they appear.</summary>
    /// <remarks>
    /// The whole text is read before any of it is parsed, where Java tokenises the reader as it goes.
    /// A data model is a few kilobytes and the streaming bought nothing.
    /// </remarks>
    /// <exception cref="FormatException">One of them is not a schema.</exception>
    public static IReadOnlyList<HollowSchema> ParseCollection(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return ParseCollection(reader.ReadToEnd());
    }

    /// <summary>The next schema, or null once the text is spent.</summary>
    private static HollowSchema? ParseSchema(Tokenizer tokenizer)
    {
        // Anything before the type name that is not a word is skipped, which is what lets a file end
        // with a trailing comment or a stray semicolon.
        while (tokenizer.Kind != TokenKind.Word)
        {
            if (tokenizer.Kind == TokenKind.End)
            {
                return null;
            }

            tokenizer.Next();
        }

        string typeName = tokenizer.Word;

        return tokenizer.Next() switch
        {
            TokenKind.Word when tokenizer.Word == "List" => ParseListSchema(typeName, tokenizer),
            TokenKind.Word when tokenizer.Word == "Set" => ParseSetSchema(typeName, tokenizer),
            TokenKind.Word when tokenizer.Word == "Map" => ParseMapSchema(typeName, tokenizer),
            TokenKind.Word => throw Expected(typeName, "'{', 'List', 'Set' or 'Map'", tokenizer),
            _ => ParseObjectSchema(typeName, tokenizer),
        };
    }

    private static HollowObjectSchema ParseObjectSchema(string typeName, Tokenizer tokenizer)
    {
        string[] primaryKey = ParseKeyFieldPaths(tokenizer, "PrimaryKey", typeName);

        Expect(tokenizer, '{', typeName);
        tokenizer.Next();

        List<(string Type, string Name)> fields = [];

        while (!tokenizer.IsSymbol('}'))
        {
            if (tokenizer.Kind != TokenKind.Word)
            {
                throw Expected(typeName, "a field type", tokenizer);
            }

            string fieldType = tokenizer.Word;

            if (tokenizer.Next() != TokenKind.Word)
            {
                throw Expected($"{typeName}.{fieldType}", "a field name", tokenizer);
            }

            string fieldName = tokenizer.Word;
            fields.Add((fieldType, fieldName));

            tokenizer.Next();
            Expect(tokenizer, ';', $"{typeName}.{fieldName}");
            tokenizer.Next();
        }

        // Past the closing brace, so that the next schema starts at its own type name.
        tokenizer.Next();

        HollowObjectSchema schema = new(typeName, fields.Count, primaryKey);

        foreach ((string fieldType, string fieldName) in fields)
        {
            if (BuiltInFieldTypes.TryGetValue(fieldType, out FieldType builtIn))
            {
                schema.AddField(fieldName, builtIn);
            }
            else
            {
                schema.AddField(fieldName, FieldType.Reference, fieldType);
            }
        }

        return schema;
    }

    private static HollowListSchema ParseListSchema(string typeName, Tokenizer tokenizer)
    {
        string elementType = ParseElementType(typeName, tokenizer);

        Expect(tokenizer, ';', typeName);
        tokenizer.Next();

        return new HollowListSchema(typeName, elementType);
    }

    private static HollowSetSchema ParseSetSchema(string typeName, Tokenizer tokenizer)
    {
        string elementType = ParseElementType(typeName, tokenizer);
        string[] hashKey = ParseKeyFieldPaths(tokenizer, "HashKey", typeName);

        Expect(tokenizer, ';', typeName);
        tokenizer.Next();

        return new HollowSetSchema(typeName, elementType, hashKey);
    }

    private static HollowMapSchema ParseMapSchema(string typeName, Tokenizer tokenizer)
    {
        tokenizer.Next();
        Expect(tokenizer, '<', typeName);

        string keyType = ExpectWord(tokenizer, typeName, "a key type");

        tokenizer.Next();
        Expect(tokenizer, ',', typeName);

        string valueType = ExpectWord(tokenizer, typeName, "a value type");

        tokenizer.Next();
        Expect(tokenizer, '>', typeName);
        tokenizer.Next();

        string[] hashKey = ParseKeyFieldPaths(tokenizer, "HashKey", typeName);

        Expect(tokenizer, ';', typeName);
        tokenizer.Next();

        return new HollowMapSchema(typeName, keyType, valueType, hashKey);
    }

    /// <summary>Reads the <c>&lt;ElementType&gt;</c> of a list or a set, leaving the token after it.</summary>
    private static string ParseElementType(string typeName, Tokenizer tokenizer)
    {
        tokenizer.Next();
        Expect(tokenizer, '<', typeName);

        string elementType = ExpectWord(tokenizer, typeName, "an element type");

        tokenizer.Next();
        Expect(tokenizer, '>', typeName);
        tokenizer.Next();

        return elementType;
    }

    /// <summary>
    /// Reads <c>@PrimaryKey(a, b)</c> or <c>@HashKey(a)</c> if one is there, and nothing if not.
    /// </summary>
    private static string[] ParseKeyFieldPaths(Tokenizer tokenizer, string annotation, string typeName)
    {
        if (!tokenizer.IsSymbol('@'))
        {
            return [];
        }

        if (tokenizer.Next() != TokenKind.Word || tokenizer.Word != annotation)
        {
            throw Expected(typeName, $"@{annotation}", tokenizer);
        }

        tokenizer.Next();
        Expect(tokenizer, '(', typeName);

        List<string> fieldPaths = [];
        tokenizer.Next();

        while (!tokenizer.IsSymbol(')'))
        {
            if (tokenizer.Kind != TokenKind.Word)
            {
                throw Expected(typeName, $"a field path inside @{annotation}", tokenizer);
            }

            fieldPaths.Add(tokenizer.Word);

            if (tokenizer.Next() == TokenKind.Symbol && tokenizer.Symbol == ',')
            {
                tokenizer.Next();
            }
            else if (!tokenizer.IsSymbol(')'))
            {
                throw Expected(typeName, $"',' or ')' inside @{annotation}", tokenizer);
            }
        }

        tokenizer.Next();

        return [.. fieldPaths];
    }

    private static void Expect(Tokenizer tokenizer, char symbol, string where)
    {
        if (!tokenizer.IsSymbol(symbol))
        {
            throw Expected(where, $"'{symbol}'", tokenizer);
        }
    }

    private static string ExpectWord(Tokenizer tokenizer, string where, string what)
    {
        if (tokenizer.Next() != TokenKind.Word)
        {
            throw Expected(where, what, tokenizer);
        }

        return tokenizer.Word;
    }

    private static FormatException Expected(string where, string what, Tokenizer tokenizer) =>
        new(string.Create(
            CultureInfo.InvariantCulture,
            $"expected {what} in the declaration of {where}, found {tokenizer.Describe()}"));

    private enum TokenKind
    {
        End,
        Word,
        Symbol,
    }

    /// <summary>
    /// Splits the text into words and single characters, skipping whitespace and comments.
    /// </summary>
    /// <remarks>
    /// Java uses <c>java.io.StreamTokenizer</c>, configured to treat <c>_</c> as a word character and to
    /// understand both comment forms. .NET has nothing like it, and a general one is not wanted here:
    /// this says exactly what the schema syntax accepts, which is words — a letter or an underscore,
    /// then any of letters, digits, underscores and dots, so that a field path is one token — and the
    /// punctuation characters the grammar uses.
    /// </remarks>
    private sealed class Tokenizer
    {
        private readonly string _text;
        private int _at;

        internal Tokenizer(string text)
        {
            _text = text;
            Next();
        }

        internal TokenKind Kind { get; private set; }

        /// <summary>The current word. Only meaningful while <see cref="Kind"/> is a word.</summary>
        internal string Word { get; private set; } = string.Empty;

        /// <summary>The current character. Only meaningful while <see cref="Kind"/> is a symbol.</summary>
        internal char Symbol { get; private set; }

        internal bool IsSymbol(char symbol) => Kind == TokenKind.Symbol && Symbol == symbol;

        internal TokenKind Next()
        {
            SkipWhitespaceAndComments();

            if (_at >= _text.Length)
            {
                return Kind = TokenKind.End;
            }

            char c = _text[_at];

            if (char.IsLetter(c) || c == '_')
            {
                int start = _at;

                while (_at < _text.Length
                    && (char.IsLetterOrDigit(_text[_at]) || _text[_at] == '_' || _text[_at] == '.'))
                {
                    _at++;
                }

                Word = _text[start.._at];

                return Kind = TokenKind.Word;
            }

            _at++;
            Symbol = c;

            return Kind = TokenKind.Symbol;
        }

        internal string Describe() =>
            Kind switch
            {
                TokenKind.Word => $"'{Word}'",
                TokenKind.Symbol => $"'{Symbol}'",
                _ => "the end of the text",
            };

        private void SkipWhitespaceAndComments()
        {
            while (_at < _text.Length)
            {
                if (char.IsWhiteSpace(_text[_at]))
                {
                    _at++;
                }
                else if (_text[_at] == '/' && _at + 1 < _text.Length && _text[_at + 1] == '/')
                {
                    while (_at < _text.Length && _text[_at] != '\n')
                    {
                        _at++;
                    }
                }
                else if (_text[_at] == '/' && _at + 1 < _text.Length && _text[_at + 1] == '*')
                {
                    int end = _text.IndexOf("*/", _at + 2, StringComparison.Ordinal);

                    // An unterminated block comment swallows the rest of the text, as Java's tokenizer
                    // does; the schema it was in the middle of then fails to parse, which is the report
                    // a reader can act on.
                    _at = end < 0 ? _text.Length : end + 2;
                }
                else
                {
                    return;
                }
            }
        }
    }
}
