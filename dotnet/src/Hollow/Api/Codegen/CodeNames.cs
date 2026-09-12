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
using System.Text;

namespace Hollow.Api.Codegen;

/// <summary>
/// Turns Hollow names into C# ones.
/// </summary>
/// <remarks>
/// A Hollow type name comes from whatever wrote the dataset, which may have been Java, so it can be
/// anything from <c>Movie</c> to <c>my-type</c> to <c>class</c>. The rules here are the port's
/// conventions applied consistently: PascalCase members, <c>I</c> on an interface, and an escape for
/// anything C# would reject.
/// </remarks>
internal static class CodeNames
{
    /// <summary>Members every record wrapper already has, which a field may not shadow.</summary>
    private static readonly HashSet<string> ReservedMembers = new(StringComparer.Ordinal)
    {
        "Ordinal", "Delegate", "TypeDataAccess", "Schema", "Api", "Equals", "GetHashCode", "ToString",
        "Count", "Keys", "Values", "Contains", "ContainsKey", "ContainsValue", "IndexOf", "LastIndexOf",
        "GetEnumerator", "InstantiateElement", "EqualsElement", "InstantiateKey", "InstantiateValue",
        "EqualsKey", "EqualsValue", "TypeApi",
    };

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class",
        "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event",
        "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto", "if",
        "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new",
        "null", "object", "operator", "out", "override", "params", "private", "protected", "public",
        "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static",
        "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong",
        "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
    };

    /// <summary>The class name for a record wrapper of <paramref name="typeName"/>.</summary>
    internal static string RecordType(string typeName) => Pascal(typeName);

    /// <summary>The class name for the type API of <paramref name="typeName"/>.</summary>
    internal static string TypeApi(string typeName) => Pascal(typeName) + "TypeApi";

    /// <summary>The interface name for the delegate of <paramref name="typeName"/>.</summary>
    internal static string DelegateInterface(string typeName) => "I" + Pascal(typeName) + "Delegate";

    /// <summary>The class name for the lookup delegate of <paramref name="typeName"/>.</summary>
    internal static string LookupDelegate(string typeName) => Pascal(typeName) + "LookupDelegate";

    /// <summary>The class name for the cached delegate of <paramref name="typeName"/>.</summary>
    internal static string CachedDelegate(string typeName) => Pascal(typeName) + "CachedDelegate";

    /// <summary>The class name for the factory of <paramref name="typeName"/>.</summary>
    internal static string Factory(string typeName) => Pascal(typeName) + "Factory";

    /// <summary>The class name for the unique-key index over <paramref name="typeName"/>.</summary>
    internal static string UniqueKeyIndex(string typeName) => Pascal(typeName) + "UniqueKeyIndex";

    /// <summary>
    /// The record name for the primary key of <paramref name="typeName"/>.
    /// </summary>
    /// <remarks>
    /// A key is one value even when it is spelled across several fields, so the generated lookups take
    /// it as one. Java passes the fields positionally as <c>Object...</c>, which compiles whatever is
    /// handed to it in whatever order.
    /// </remarks>
    internal static string PrimaryKeyRecord(string typeName) => Pascal(typeName) + "PrimaryKey";

    /// <summary>The method name for the key lookup of <paramref name="typeName"/> on the API.</summary>
    internal static string ApiKeyLookup(string typeName) => "Find" + Pascal(typeName);

    /// <summary>
    /// The class name for paths that have arrived at <paramref name="typeName"/>.
    /// </summary>
    internal static string PathType(string typeName) => Pascal(typeName) + "Path";

    /// <summary>The class name of the container holding every root a path can start at.</summary>
    /// <remarks>
    /// A trailing <c>Api</c> is dropped first, so a <c>CatalogueApi</c> client offers
    /// <c>CataloguePaths</c> rather than <c>CatalogueApiPaths</c>.
    /// </remarks>
    internal static string PathRoots(string apiClassName)
    {
        string name = Pascal(apiClassName);

        return (name.EndsWith("Api", StringComparison.Ordinal) ? name.Substring(0, name.Length - 3) : name)
            + "Paths";
    }

    /// <summary>
    /// The property name for <paramref name="fieldName"/> on a wrapper of <paramref name="typeName"/>.
    /// </summary>
    /// <remarks>
    /// A field whose PascalCase name collides with the wrapper's own members, or with the class name
    /// itself (which C# forbids), is suffixed rather than renamed, so the correspondence to the schema
    /// stays obvious.
    /// </remarks>
    internal static string Property(string typeName, string fieldName)
    {
        string name = Pascal(fieldName);

        return ReservedMembers.Contains(name) || name == RecordType(typeName) ? name + "Field" : name;
    }

    /// <summary>The name of the generated accessor for <paramref name="typeName"/> on the API.</summary>
    internal static string ApiAccessor(string typeName) => "Get" + Pascal(typeName);

    /// <summary>
    /// <paramref name="name"/> as a C# identifier, escaped if it would otherwise be a keyword.
    /// </summary>
    internal static string Identifier(string name)
    {
        string identifier = Sanitise(name);

        return Keywords.Contains(identifier) ? "@" + identifier : identifier;
    }

    /// <summary>A camelCase parameter name for <paramref name="name"/>.</summary>
    internal static string Parameter(string name)
    {
        string camel = Camel(name);

        return Keywords.Contains(camel) ? "@" + camel : camel;
    }

    /// <summary>
    /// <paramref name="name"/> in camelCase, unescaped.
    /// </summary>
    /// <remarks>
    /// For building a longer identifier around it — <c>_decimalProvider</c> is a perfectly good name
    /// even though <c>decimal</c> on its own would need escaping, and <c>_@decimalProvider</c> is not
    /// an identifier at all.
    /// </remarks>
    internal static string Camel(string name)
    {
        string pascal = Pascal(name);

        return char.ToLowerInvariant(pascal[0]) + pascal.Substring(1);
    }

    /// <summary>The C# type a value field reads as, nullable where the field can be null.</summary>
    internal static string ValueTypeOf(ModelFieldType fieldType) =>
        fieldType switch
        {
            ModelFieldType.Int => "int",
            ModelFieldType.Long => "long",
            ModelFieldType.Float => "float",
            ModelFieldType.Double => "double",
            ModelFieldType.Boolean => "bool?",
            ModelFieldType.Decimal => "decimal?",
            ModelFieldType.String => "string?",
            ModelFieldType.Bytes => "byte[]?",
            _ => throw new ArgumentOutOfRangeException(
                nameof(fieldType), fieldType, "not a value field type"),
        };

    /// <summary>
    /// <paramref name="name"/> in PascalCase, with anything C# would reject removed.
    /// </summary>
    /// <remarks>
    /// A name that is already PascalCase is left alone rather than re-cased, so <c>ListOfActor</c> does
    /// not become <c>Listofactor</c>. Separators — <c>_</c>, <c>-</c>, <c>.</c>, a space — start a new
    /// word.
    /// </remarks>
    internal static string Pascal(string name)
    {
        string sanitised = Sanitise(name);
        StringBuilder pascal = new(sanitised.Length);
        bool capitaliseNext = true;

        foreach (char character in sanitised)
        {
            if (character == '_')
            {
                capitaliseNext = true;
                continue;
            }

            pascal.Append(
                capitaliseNext ? char.ToUpper(character, CultureInfo.InvariantCulture) : character);
            capitaliseNext = false;
        }

        return pascal.Length == 0 ? "_" : pascal.ToString();
    }

    /// <summary>
    /// <paramref name="name"/> with every character C# cannot have in an identifier turned into an
    /// underscore, and a leading digit prefixed.
    /// </summary>
    private static string Sanitise(string name)
    {
        // Written out rather than ArgumentException.ThrowIfNullOrEmpty, which is .NET 7 and up: this
        // file is compiled into the netstandard2.0 source generator as well.
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("a name to turn into an identifier cannot be empty", nameof(name));
        }

        StringBuilder sanitised = new(name.Length);

        foreach (char character in name)
        {
            sanitised.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
        }

        if (char.IsDigit(sanitised[0]))
        {
            sanitised.Insert(0, '_');
        }

        return sanitised.ToString();
    }
}
