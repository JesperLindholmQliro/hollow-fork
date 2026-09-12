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

using Hollow.Api.Codegen;
using Microsoft.CodeAnalysis;

namespace Hollow.SourceGenerator;

/// <summary>
/// A type reached from somewhere in the model, with what that reference declared about it.
/// </summary>
/// <param name="Type">The referenced type.</param>
/// <param name="NameOverride">The Hollow type name the referencing member declared, if any.</param>
/// <param name="DeclaredHashKey">The hash key the referencing member declared, if any.</param>
internal sealed record Reference(
    ITypeSymbol Type, string? NameOverride, IReadOnlyList<string>? DeclaredHashKey);

/// <summary>
/// Derives a data model from Roslyn's view of the declared types.
/// </summary>
/// <remarks>
/// <para>
/// This is the one piece of code generation a source generator cannot borrow. The text emitter asks
/// <c>HollowObjectMapper</c> to derive the schemas, because it runs with the model's types loaded; a
/// generator runs inside the compiler and sees symbols, not types, so the same rules are applied
/// again here.
/// </para>
/// <para>
/// The two have to agree exactly — a client generated one way reads a blob written by a producer
/// mapping the same types the other. <c>SourceGeneratorTests</c> asserts that they do, over a model
/// exercising every rule below, which is the check that keeps them from drifting.
/// </para>
/// </remarks>
internal static class SymbolModel
{
    private const string AttributeNamespace = "Hollow.Core.Write.ObjectMapper";

    /// <summary>
    /// The schemas for <paramref name="roots"/> and everything reachable from them.
    /// </summary>
    /// <exception cref="ModelException">The model declares something that cannot be mapped.</exception>
    internal static IReadOnlyList<ModelSchema> Describe(IEnumerable<INamedTypeSymbol> roots)
    {
        Dictionary<string, ModelSchema> schemas = new(StringComparer.Ordinal);
        Queue<Reference> pending = new();

        foreach (INamedTypeSymbol root in roots)
        {
            pending.Enqueue(new Reference(root, null, null));
        }

        while (pending.Count > 0)
        {
            Reference reference = pending.Dequeue();
            string typeName = ResolveTypeName(reference.Type, reference.NameOverride);

            if (schemas.ContainsKey(typeName))
            {
                continue;
            }

            // Reserved before the referenced types are walked, so a model that points back at itself
            // terminates.
            schemas[typeName] = Describe(reference, typeName, pending);
        }

        return [.. schemas.Values];
    }

    private static ModelSchema Describe(Reference reference, string typeName, Queue<Reference> pending)
    {
        ITypeSymbol type = reference.Type;

        if (TryGetDictionaryTypes(type, out ITypeSymbol? keyType, out ITypeSymbol? valueType))
        {
            pending.Enqueue(new Reference(keyType!, null, null));
            pending.Enqueue(new Reference(valueType!, null, null));

            return new ModelMapSchema(
                typeName,
                ResolveTypeName(keyType!, null),
                ResolveTypeName(valueType!, null),
                reference.DeclaredHashKey ?? DefaultHashKeyFor(keyType!));
        }

        if (TryGetSetElementType(type, out ITypeSymbol? setElement))
        {
            pending.Enqueue(new Reference(setElement!, null, null));

            return new ModelSetSchema(
                typeName,
                ResolveTypeName(setElement!, null),
                reference.DeclaredHashKey ?? DefaultHashKeyFor(setElement!));
        }

        if (TryGetListElementType(type, out ITypeSymbol? listElement))
        {
            pending.Enqueue(new Reference(listElement!, null, null));

            return new ModelListSchema(typeName, ResolveTypeName(listElement!, null));
        }

        return DescribeObject(type, typeName, pending);
    }

    private static ModelObjectSchema DescribeObject(
        ITypeSymbol type, string typeName, Queue<Reference> pending)
    {
        IReadOnlyList<string>? primaryKey = PrimaryKeyOf(type);

        if (IsScalarWrapper(type))
        {
            // A scalar stored by reference becomes a one-field wrapper type, matching Java's
            // Integer/Long/String and friends.
            return new ModelObjectSchema(
                typeName, [new ModelField("value", ScalarFieldType(type))], primaryKey);
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            // An enum becomes a type carrying its member name, so the data stays readable if the enum's
            // numeric values are ever renumbered.
            return new ModelObjectSchema(
                typeName, [new ModelField("_name", ModelFieldType.String)], primaryKey);
        }

        List<ModelField> fields = [];

        foreach (ISymbol member in MappedMembers(type))
        {
            ITypeSymbol memberType = MemberType(member);

            if (IsInlinedScalar(member, memberType))
            {
                fields.Add(new ModelField(member.Name, ScalarFieldType(memberType)));
                continue;
            }

            ITypeSymbol underlying = NullableUnderlying(memberType);
            string? nameOverride = SingleStringArgument(member, "HollowTypeNameAttribute");

            fields.Add(new ModelField(
                member.Name, ModelFieldType.Reference, ResolveTypeName(underlying, nameOverride)));
            pending.Enqueue(new Reference(underlying, nameOverride, HashKeyOn(member)));
        }

        if (fields.Count == 0)
        {
            throw new ModelException(
                $"{type.ToDisplayString()} has no public instance properties or fields to map; a Hollow "
                + "record needs at least one field",
                type);
        }

        return new ModelObjectSchema(typeName, fields, primaryKey);
    }

    /// <summary>
    /// The public instance properties then fields, in declaration order, skipping anything marked
    /// <c>[HollowTransient]</c>.
    /// </summary>
    /// <remarks>
    /// The same order and the same filter as <c>MappedMember.ForType</c>, since the field positions the
    /// generated API indexes by come out of it. A record's compiler-generated
    /// <c>EqualityContract</c> is protected and so never appears.
    /// </remarks>
    private static List<ISymbol> MappedMembers(ITypeSymbol type)
    {
        List<ISymbol> properties = [];
        List<ISymbol> fields = [];

        // Base first, as reflection reports inherited members before declared ones.
        foreach (ITypeSymbol level in Hierarchy(type))
        {
            properties.AddRange(level.GetMembers().OfType<IPropertySymbol>()
                .Where(property => property.DeclaredAccessibility == Accessibility.Public
                    && !property.IsStatic
                    && property.GetMethod is not null
                    && property.Parameters.Length == 0
                    && !HasAttribute(property, "HollowTransientAttribute")));

            fields.AddRange(level.GetMembers().OfType<IFieldSymbol>()
                .Where(field => field.DeclaredAccessibility == Accessibility.Public
                    && !field.IsStatic
                    && !field.IsImplicitlyDeclared
                    && !HasAttribute(field, "HollowTransientAttribute")));
        }

        properties.AddRange(fields);

        return properties;
    }

    private static List<ITypeSymbol> Hierarchy(ITypeSymbol type)
    {
        List<ITypeSymbol> levels = [];

        for (ITypeSymbol? level = type;
            level is not null && level.SpecialType != SpecialType.System_Object;
            level = level.BaseType)
        {
            levels.Insert(0, level);
        }

        return levels;
    }

    private static ITypeSymbol MemberType(ISymbol member) =>
        member is IPropertySymbol property ? property.Type : ((IFieldSymbol)member).Type;

    // ---- The naming and typing rules, mirrored from HollowObjectMapper ----

    /// <summary>The Hollow type name a CLR type maps to.</summary>
    internal static string ResolveTypeName(ITypeSymbol type, string? nameOverride)
    {
        if (nameOverride is not null)
        {
            return nameOverride;
        }

        if (SingleStringArgument(type, "HollowTypeNameAttribute") is { } attributeName)
        {
            return attributeName;
        }

        if (TryGetDictionaryTypes(type, out ITypeSymbol? keyType, out ITypeSymbol? valueType))
        {
            return $"MapOf{ResolveTypeName(keyType!, null)}To{ResolveTypeName(valueType!, null)}";
        }

        if (TryGetSetElementType(type, out ITypeSymbol? setElement))
        {
            return $"SetOf{ResolveTypeName(setElement!, null)}";
        }

        if (TryGetListElementType(type, out ITypeSymbol? listElement))
        {
            return $"ListOf{ResolveTypeName(listElement!, null)}";
        }

        return DefaultTypeName(type);
    }

    /// <summary>The Hollow type name for a CLR type, before any override.</summary>
    private static string DefaultTypeName(ITypeSymbol type)
    {
        ITypeSymbol underlying = NullableUnderlying(type);

        string? scalarName = underlying.SpecialType switch
        {
            SpecialType.System_String => "String",
            SpecialType.System_Int32 => "Integer",
            SpecialType.System_Int64 => "Long",
            SpecialType.System_Int16 => "Short",
            SpecialType.System_Boolean => "Boolean",
            SpecialType.System_Single => "Float",
            SpecialType.System_Double => "Double",

            // Format extension: no Java Hollow type corresponds to this one. See PORTING.md.
            SpecialType.System_Decimal => "Decimal",

            SpecialType.System_Byte or SpecialType.System_SByte => "Byte",
            SpecialType.System_Char => "Character",
            _ => null,
        };

        if (scalarName is not null)
        {
            return scalarName;
        }

        // A generic type's CLR name carries an arity suffix, which is not a legal Hollow type name;
        // Roslyn's Name has it stripped already.
        return IsByteArray(underlying) ? "Bytes" : underlying.Name;
    }

    /// <summary>
    /// Whether a CLR type is one Hollow stores directly rather than as a record of its own members.
    /// </summary>
    private static bool IsScalarWrapper(ITypeSymbol type)
    {
        ITypeSymbol underlying = NullableUnderlying(type);

        return IsPrimitiveOrDecimal(underlying)
            || underlying.SpecialType == SpecialType.System_String
            || IsByteArray(underlying)
            || IsCharArray(underlying);
    }

    /// <summary>The schema field type a scalar maps to.</summary>
    private static ModelFieldType ScalarFieldType(ITypeSymbol type)
    {
        ITypeSymbol underlying = NullableUnderlying(type);

        switch (underlying.SpecialType)
        {
            case SpecialType.System_Boolean:
                return ModelFieldType.Boolean;
            case SpecialType.System_Single:
                return ModelFieldType.Float;
            case SpecialType.System_Double:
                return ModelFieldType.Double;
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
                return ModelFieldType.Long;
            case SpecialType.System_String:
            case SpecialType.System_Char:
                return ModelFieldType.String;
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
                return ModelFieldType.Int;
            case SpecialType.System_Decimal:
                // Format extension: Netflix Hollow has no decimal field type. See PORTING.md.
                return ModelFieldType.Decimal;
            default:
                break;
        }

        if (IsByteArray(underlying))
        {
            return ModelFieldType.Bytes;
        }

        if (IsCharArray(underlying))
        {
            return ModelFieldType.String;
        }

        throw new ModelException($"{underlying.ToDisplayString()} is not a Hollow scalar type", underlying);
    }

    /// <summary>
    /// Whether a member's value is stored in the record rather than as a reference to a type of its own.
    /// </summary>
    /// <remarks>
    /// A non-nullable primitive is always inlined: there is no null to represent, and a reference would
    /// cost an indirection for nothing. Everything else follows <c>[HollowInline]</c>, defaulting to a
    /// reference so that repeated values deduplicate.
    /// </remarks>
    private static bool IsInlinedScalar(ISymbol member, ITypeSymbol memberType) =>
        IsScalarWrapper(memberType)
        && (HasAttribute(member, "HollowInlineAttribute")
            || (IsPrimitiveOrDecimal(NullableUnderlying(memberType)) && !IsNullableValueType(memberType)));

    /// <summary>
    /// The hash key Java derives for a set element or map key type that declares none: its primary key
    /// if it has one, otherwise its single field if it maps to exactly one inlined value field.
    /// </summary>
    /// <remarks>
    /// Derived from the type alone rather than from its schema, because the element type's schema may
    /// not exist yet — a model where a type contains a set of itself would not terminate otherwise.
    /// </remarks>
    private static IReadOnlyList<string>? DefaultHashKeyFor(ITypeSymbol elementOrKeyType)
    {
        if (PrimaryKeyOf(elementOrKeyType) is { Count: > 0 } key)
        {
            return key;
        }

        // Only an object type has fields to hash; a collection of collections hashes by ordinal.
        if (TryGetDictionaryTypes(elementOrKeyType, out _, out _)
            || TryGetSetElementType(elementOrKeyType, out _)
            || TryGetListElementType(elementOrKeyType, out _))
        {
            return null;
        }

        if (IsScalarWrapper(elementOrKeyType))
        {
            return ["value"];
        }

        if (elementOrKeyType.TypeKind == TypeKind.Enum)
        {
            return ["_name"];
        }

        List<ISymbol> members = MappedMembers(elementOrKeyType);

        return members.Count == 1 && IsInlinedScalar(members[0], MemberType(members[0]))
            ? [members[0].Name]
            : null;
    }

    /// <summary>
    /// The hash key a member declares for the set or map it references, if it declares one.
    /// </summary>
    private static IReadOnlyList<string>? HashKeyOn(ISymbol member) =>
        StringArrayArgument(member, "HollowHashKeyAttribute");

    private static IReadOnlyList<string>? PrimaryKeyOf(ITypeSymbol type) =>
        StringArrayArgument(type, "HollowPrimaryKeyAttribute") is { Count: > 0 } key ? key : null;

    // ---- Symbol helpers ----

    private static bool IsNullableValueType(ITypeSymbol type) =>
        type is INamedTypeSymbol { IsGenericType: true } named
        && named.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T;

    private static ITypeSymbol NullableUnderlying(ITypeSymbol type) =>
        IsNullableValueType(type) ? ((INamedTypeSymbol)type).TypeArguments[0] : type;

    private static bool IsPrimitiveOrDecimal(ITypeSymbol type) =>
        type.SpecialType is SpecialType.System_Boolean or SpecialType.System_Char
            or SpecialType.System_SByte or SpecialType.System_Byte
            or SpecialType.System_Int16 or SpecialType.System_UInt16
            or SpecialType.System_Int32 or SpecialType.System_UInt32
            or SpecialType.System_Int64 or SpecialType.System_UInt64
            or SpecialType.System_Single or SpecialType.System_Double
            or SpecialType.System_IntPtr or SpecialType.System_UIntPtr
            or SpecialType.System_Decimal;

    private static bool IsByteArray(ITypeSymbol type) =>
        type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte };

    private static bool IsCharArray(ITypeSymbol type) =>
        type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Char };

    private static bool TryGetDictionaryTypes(
        ITypeSymbol type, out ITypeSymbol? keyType, out ITypeSymbol? valueType)
    {
        INamedTypeSymbol? dictionary =
            FindGenericInterface(type, "System.Collections.Generic.IDictionary", 2);

        keyType = dictionary?.TypeArguments[0];
        valueType = dictionary?.TypeArguments[1];

        return dictionary is not null;
    }

    private static bool TryGetSetElementType(ITypeSymbol type, out ITypeSymbol? elementType)
    {
        elementType =
            FindGenericInterface(type, "System.Collections.Generic.ISet", 1)?.TypeArguments[0];

        return elementType is not null;
    }

    private static bool TryGetListElementType(ITypeSymbol type, out ITypeSymbol? elementType)
    {
        // A string is enumerable but is a scalar as far as Hollow is concerned, and so are the two
        // arrays that map to value fields.
        if (type.SpecialType == SpecialType.System_String || IsByteArray(type) || IsCharArray(type))
        {
            elementType = null;

            return false;
        }

        elementType =
            FindGenericInterface(type, "System.Collections.Generic.IEnumerable", 1)?.TypeArguments[0];

        return elementType is not null;
    }

    private static INamedTypeSymbol? FindGenericInterface(ITypeSymbol type, string unboundName, int arity)
    {
        if (type is INamedTypeSymbol { IsGenericType: true } named && Matches(named, unboundName, arity))
        {
            return named;
        }

        foreach (INamedTypeSymbol candidate in type.AllInterfaces)
        {
            if (candidate.IsGenericType && Matches(candidate, unboundName, arity))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool Matches(INamedTypeSymbol type, string unboundName, int arity) =>
        type.Arity == arity
        && type.ContainingNamespace?.ToDisplayString() + "." + type.Name == unboundName;

    private static bool HasAttribute(ISymbol symbol, string attributeName) =>
        symbol.GetAttributes().Any(attribute => IsHollowAttribute(attribute, attributeName));

    private static bool IsHollowAttribute(AttributeData attribute, string attributeName) =>
        attribute.AttributeClass is { } attributeClass
        && attributeClass.Name == attributeName
        && attributeClass.ContainingNamespace?.ToDisplayString() == AttributeNamespace;

    private static string? SingleStringArgument(ISymbol symbol, string attributeName) =>
        symbol.GetAttributes()
            .FirstOrDefault(attribute => IsHollowAttribute(attribute, attributeName))
            ?.ConstructorArguments is { Length: 1 } arguments
            ? arguments[0].Value as string
            : null;

    /// <summary>
    /// The <c>params string[]</c> an attribute was given, or <see langword="null"/> where the attribute
    /// is absent. An empty list means the attribute was applied with no paths.
    /// </summary>
    private static IReadOnlyList<string>? StringArrayArgument(ISymbol symbol, string attributeName)
    {
        AttributeData? attribute = symbol.GetAttributes()
            .FirstOrDefault(candidate => IsHollowAttribute(candidate, attributeName));

        if (attribute is null)
        {
            return null;
        }

        return attribute.ConstructorArguments.Length == 1
            && attribute.ConstructorArguments[0].Kind == TypedConstantKind.Array
                ? [.. attribute.ConstructorArguments[0].Values
                    .Select(value => value.Value as string)
                    .Where(value => value is not null)
                    .Select(value => value!)]
                : [];
    }
}

/// <summary>
/// Something in the declared model cannot be mapped, reported against the symbol that declared it.
/// </summary>
internal sealed class ModelException(string message, ISymbol symbol) : Exception(message)
{
    /// <summary>What the generator was looking at when it gave up.</summary>
    internal ISymbol Symbol { get; } = symbol;
}
