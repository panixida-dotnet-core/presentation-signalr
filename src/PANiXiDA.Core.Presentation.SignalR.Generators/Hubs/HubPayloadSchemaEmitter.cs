using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace PANiXiDA.Core.Presentation.SignalR.Generators.Hubs;

internal sealed class HubPayloadSchemaEmitter
{
    private const string JsonSerialization = "System.Text.Json.Serialization.";
    private const string JsonObjectType = "global::System.Text.Json.Nodes.JsonObject";
    private const string JsonArrayType = "global::System.Text.Json.Nodes.JsonArray";
    private const string SchemaType = "global::PANiXiDA.Core.Presentation.SignalR.Documentation.HubPayloadSchema";
    private const int JsonIgnoreAlways = 1;

    private static readonly HashSet<string> Dictionaries = new(StringComparer.Ordinal)
    {
        "System.Collections.Generic.Dictionary<TKey, TValue>",
        "System.Collections.Generic.IDictionary<TKey, TValue>",
        "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>"
    };

    private static readonly HashSet<string> Collections = new(StringComparer.Ordinal)
    {
        "System.Collections.Generic.List<T>",
        "System.Collections.Generic.IList<T>",
        "System.Collections.Generic.IReadOnlyList<T>",
        "System.Collections.Generic.ICollection<T>",
        "System.Collections.Generic.IReadOnlyCollection<T>",
        "System.Collections.Generic.IEnumerable<T>",
        "System.Collections.Generic.HashSet<T>",
        "System.Collections.Generic.ISet<T>",
        "System.Collections.Generic.IReadOnlySet<T>"
    };

    private static readonly Dictionary<SpecialType, string> ScalarTypes = new()
    {
        [SpecialType.System_Boolean] = "boolean",
        [SpecialType.System_String] = "string",
        [SpecialType.System_Char] = "string",
        [SpecialType.System_Byte] = "integer",
        [SpecialType.System_SByte] = "integer",
        [SpecialType.System_Int16] = "integer",
        [SpecialType.System_UInt16] = "integer",
        [SpecialType.System_Int32] = "integer",
        [SpecialType.System_UInt32] = "integer",
        [SpecialType.System_Int64] = "integer",
        [SpecialType.System_UInt64] = "integer",
        [SpecialType.System_Single] = "number",
        [SpecialType.System_Double] = "number",
        [SpecialType.System_Decimal] = "number"
    };

    private static readonly Dictionary<string, string> StringFormats = new(StringComparer.Ordinal)
    {
        ["System.Guid"] = "uuid",
        ["System.DateTime"] = "date-time",
        ["System.DateTimeOffset"] = "date-time",
        ["System.DateOnly"] = "date",
        ["System.TimeOnly"] = "time",
        ["System.Uri"] = "uri"
    };

    private static readonly HashSet<string> CustomContractAttributes = new(StringComparer.Ordinal)
    {
        JsonSerialization + "JsonConverterAttribute",
        JsonSerialization + "JsonPolymorphicAttribute",
        JsonSerialization + "JsonDerivedTypeAttribute",
        JsonSerialization + "JsonExtensionDataAttribute",
        JsonSerialization + "JsonNumberHandlingAttribute",
        JsonSerialization + "JsonUnmappedMemberHandlingAttribute",
        JsonSerialization + "JsonObjectCreationHandlingAttribute",
        "System.Diagnostics.CodeAnalysis.AllowNullAttribute",
        "System.Diagnostics.CodeAnalysis.DisallowNullAttribute",
        "System.Diagnostics.CodeAnalysis.MaybeNullAttribute",
        "System.Diagnostics.CodeAnalysis.NotNullAttribute"
    };

    private readonly HashSet<ITypeSymbol> _types = new(SymbolEqualityComparer.Default);
    private readonly HashSet<INamedTypeSymbol> _objects = new(SymbolEqualityComparer.Default);

    internal static string? Emit(ITypeSymbol type)
    {
        var emitter = new HubPayloadSchemaEmitter();
        var schema = emitter.Build(type);
        if (schema is null)
        {
            return null;
        }

        var types = emitter._types.Select(type => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => $"typeof({name})");

        return $"new {SchemaType}(static naming => {schema}, {string.Join(", ", types)})";
    }

    private string? Build(ITypeSymbol type)
    {
        _types.Add(type);
        var nullable = type.IsReferenceType && type.NullableAnnotation != NullableAnnotation.NotAnnotated;
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } optional)
        {
            type = optional.TypeArguments[0];
            _types.Add(type);
            nullable = true;
        }

        if (HasCustomContract(type))
        {
            return null;
        }

        if (Scalar(type) is { } scalar)
        {
            return scalar.Format is null
                ? Object(("type", TypeValue(scalar.Type, nullable)))
                : Object(("type", TypeValue(scalar.Type, nullable)), ("format", Literal(scalar.Format)));
        }

        return type switch
        {
            IArrayTypeSymbol { Rank: 1, IsSZArray: true } array => Array(array.ElementType, nullable),
            INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct or TypeKind.Interface } named => BuildNamed(named, nullable),
            _ => null
        };
    }

    private string? BuildNamed(INamedTypeSymbol type, bool nullable)
    {
        var definition = type.OriginalDefinition.ToDisplayString();
        if (Dictionaries.Contains(definition))
        {
            return Dictionary(type, nullable);
        }

        if (Collections.Contains(definition))
        {
            return Array(type.TypeArguments[0], nullable);
        }

        var typeNamespace = type.ContainingNamespace.ToDisplayString();
        if (type.IsAbstract || type.TypeKind == TypeKind.Interface ||
            typeNamespace == "System" || typeNamespace.StartsWith("System.", StringComparison.Ordinal) ||
            type.AllInterfaces.Any(item => item.SpecialType == SpecialType.System_Collections_IEnumerable) ||
            !_objects.Add(type.OriginalDefinition))
        {
            return null;
        }

        var result = BuildObject(type, nullable);
        _objects.Remove(type.OriginalDefinition);
        return result;
    }

    private string? Array(ITypeSymbol itemType, bool nullable)
    {
        return Build(itemType) is { } item
            ? Object(("type", TypeValue("array", nullable)), ("items", item))
            : null;
    }

    private string? Dictionary(INamedTypeSymbol type, bool nullable)
    {
        var key = type.TypeArguments[0];
        if (key.SpecialType != SpecialType.System_String || Build(type.TypeArguments[1]) is not { } value)
        {
            return null;
        }

        _types.Add(key);
        return Object(("type", TypeValue("object", nullable)), ("additionalProperties", value));
    }

    private string? BuildObject(INamedTypeSymbol type, bool nullable)
    {
        var members = GetProperties(type);
        if (members is null)
        {
            return null;
        }

        var properties = new List<string>();
        var required = new List<string>();
        var constructorParameters = RequiredConstructorParameters(type);
        foreach (var property in members)
        {
            if (HasCustomContract(property) || Build(property.Type) is not { } schema)
            {
                return null;
            }

            var name = PropertyName(property);
            properties.Add($"{{ {name}, {schema} }}");
            if (property.IsRequired || HasAttribute(property, "JsonRequiredAttribute") ||
                constructorParameters.Any(parameter => StringComparer.OrdinalIgnoreCase.Equals(parameter.Name, property.Name) &&
                    SymbolEqualityComparer.Default.Equals(parameter.Type, property.Type)))
            {
                required.Add(name);
            }
        }

        var propertiesExpression = $"new {JsonObjectType} {{ {string.Join(", ", properties)} }}";
        return required.Count == 0
            ? Object(("type", TypeValue("object", nullable)), ("properties", propertiesExpression))
            : Object(("type", TypeValue("object", nullable)), ("properties", propertiesExpression),
                ("required", $"new {JsonArrayType}({string.Join(", ", required)})"));
    }

    private static List<IPropertySymbol>? GetProperties(INamedTypeSymbol type)
    {
        var properties = new List<IPropertySymbol>();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var current = type; current is not null; current = current.BaseType)
        {
            var members = current.GetMembers();
            if (HasCustomContract(current) || members.Any(member => HasAttribute(member, "JsonIncludeAttribute")) ||
                !AddProperties(members.OfType<IPropertySymbol>().Where(IsPublicInstanceProperty), properties, seen))
            {
                return null;
            }
        }

        return properties;
    }

    private static bool AddProperties(IEnumerable<IPropertySymbol> members, List<IPropertySymbol> properties,
        Dictionary<string, string> seen)
    {
        foreach (var property in members)
        {
            var name = PropertyName(property);
            if (seen.TryGetValue(property.Name, out var derivedName))
            {
                if (name != derivedName)
                {
                    return false;
                }

                continue;
            }

            seen.Add(property.Name, name);
            if (!IsIgnored(property))
            {
                properties.Add(property);
            }
        }

        return true;
    }

    private static bool IsPublicInstanceProperty(IPropertySymbol property) =>
        !property.IsStatic && !property.IsIndexer && (property.GetMethod?.DeclaredAccessibility == Accessibility.Public ||
            property.SetMethod?.DeclaredAccessibility == Accessibility.Public);

    private static IEnumerable<IParameterSymbol> RequiredConstructorParameters(INamedTypeSymbol type)
    {
        var constructor = type.InstanceConstructors.FirstOrDefault(candidate => HasAttribute(candidate, "JsonConstructorAttribute"));
        if (constructor is null && !type.IsValueType)
        {
            var publicConstructors = type.InstanceConstructors.Where(candidate =>
                candidate.DeclaredAccessibility == Accessibility.Public).ToArray();
            constructor = publicConstructors.Length == 1
                ? publicConstructors[0]
                : publicConstructors.FirstOrDefault(candidate => candidate.Parameters.IsEmpty);
        }

        return constructor?.Parameters.Where(parameter => !parameter.IsOptional) ?? [];
    }

    private static (string Type, string? Format)? Scalar(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Enum)
        {
            return ("integer", null);
        }

        if (type is IArrayTypeSymbol { Rank: 1, ElementType.SpecialType: SpecialType.System_Byte })
        {
            return ("string", "byte");
        }

        if (ScalarTypes.TryGetValue(type.SpecialType, out var scalar))
        {
            return (scalar, null);
        }

        return StringFormats.TryGetValue(type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(), out var format)
            ? ("string", format)
            : null;
    }

    private static bool HasCustomContract(ISymbol symbol)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            for (var type = attribute.AttributeClass; type is not null; type = type.BaseType)
            {
                if (CustomContractAttributes.Contains(type.ToDisplayString()))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsIgnored(IPropertySymbol property)
    {
        var attribute = GetAttribute(property, "JsonIgnoreAttribute");
        return attribute is not null &&
            Equals(attribute.NamedArguments.FirstOrDefault().Value.Value ?? JsonIgnoreAlways, JsonIgnoreAlways);
    }

    private static string PropertyName(IPropertySymbol property)
    {
        return GetAttribute(property, "JsonPropertyNameAttribute")?.ConstructorArguments[0].Value is string name
            ? Literal(name)
            : $"naming?.ConvertName({Literal(property.Name)}) ?? {Literal(property.Name)}";
    }

    private static bool HasAttribute(ISymbol symbol, string name) => GetAttribute(symbol, name) is not null;

    private static AttributeData? GetAttribute(ISymbol symbol, string name) => symbol.GetAttributes()
        .FirstOrDefault(attribute => attribute.AttributeConstructor?.ContainingType.ToDisplayString() == JsonSerialization + name);

    private static string TypeValue(string type, bool nullable) => nullable
        ? $"new {JsonArrayType}({Literal(type)}, \"null\")"
        : Literal(type);

    private static string Object(params (string Name, string Value)[] properties) =>
        $"new {JsonObjectType} {{ {string.Join(", ", properties.Select(property => $"[{Literal(property.Name)}] = {property.Value}"))} }}";

    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, true);
}
