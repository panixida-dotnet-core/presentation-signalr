using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace PANiXiDA.Core.Presentation.SignalR.Generators.Hubs;

internal sealed class HubPayloadSchemaEmitter(bool isOutput)
{
    private const string JsonSerialization = "System.Text.Json.Serialization.";
    private const string JsonObjectType = "global::System.Text.Json.Nodes.JsonObject";
    private const string JsonArrayType = "global::System.Text.Json.Nodes.JsonArray";
    private const string JsonString = "string";
    private const string JsonInteger = "integer";
    private const string SchemaType = "global::PANiXiDA.Core.Presentation.SignalR.Documentation.HubPayloadSchema";
    private const int JsonIgnoreNever = 0;
    private const int JsonIgnoreAlways = 1;
    private const int JsonIgnoreWhenWritingDefault = 2;
    private const int JsonIgnoreWhenWritingNull = 3;
    private const int JsonIgnoreWhenWriting = 4;
    private const int JsonIgnoreWhenReading = 5;
    private const int MaxObjectDepth = 64;

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
        [SpecialType.System_String] = JsonString,
        [SpecialType.System_Char] = JsonString,
        [SpecialType.System_Byte] = JsonInteger,
        [SpecialType.System_SByte] = JsonInteger,
        [SpecialType.System_Int16] = JsonInteger,
        [SpecialType.System_UInt16] = JsonInteger,
        [SpecialType.System_Int32] = JsonInteger,
        [SpecialType.System_UInt32] = JsonInteger,
        [SpecialType.System_Int64] = JsonInteger,
        [SpecialType.System_UInt64] = JsonInteger,
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

    internal static string? Emit(ITypeSymbol type, bool isOutput)
    {
        var emitter = new HubPayloadSchemaEmitter(isOutput);
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

    private string? Build(ITypeSymbol type, bool enforceReferenceNullability = false)
    {
        _types.Add(type);
        var nullable = type.IsReferenceType && (!enforceReferenceNullability || type.NullableAnnotation != NullableAnnotation.NotAnnotated);
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
            _objects.Count >= MaxObjectDepth || !_objects.Add(type))
        {
            return null;
        }

        var result = BuildObject(type, nullable);
        _objects.Remove(type);
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
        var constructor = GetJsonConstructor(type);
        var constructorParameters = constructor?.Parameters.Where(parameter => !parameter.IsOptional) ?? [];
        var setsRequiredMembers = constructor is not null &&
            HasAttribute(constructor, "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute");
        foreach (var property in members)
        {
            var ignoreCondition = GetIgnoreCondition(property);
            if (ignoreCondition == (isOutput ? JsonIgnoreWhenWriting : JsonIgnoreWhenReading))
            {
                continue;
            }

            var enforceReferenceNullability = property.OriginalDefinition.Type.TypeKind != TypeKind.TypeParameter;
            if (HasCustomContract(property) || Build(property.Type, enforceReferenceNullability) is not { } schema)
            {
                return null;
            }

            var name = PropertyName(property);
            properties.Add($"{{ {name}, {schema} }}");
            if (IsRequired(property, ignoreCondition, setsRequiredMembers, constructorParameters))
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

    private bool IsRequired(IPropertySymbol property, int ignoreCondition, bool setsRequiredMembers,
        IEnumerable<IParameterSymbol> constructorParameters)
    {
        if (isOutput && ignoreCondition is JsonIgnoreWhenWritingDefault or JsonIgnoreWhenWritingNull)
        {
            return false;
        }

        return (property.IsRequired && !setsRequiredMembers) ||
            HasAttribute(property, JsonSerialization + "JsonRequiredAttribute") ||
            constructorParameters.Any(parameter => StringComparer.OrdinalIgnoreCase.Equals(parameter.Name, property.Name) &&
                SymbolEqualityComparer.Default.Equals(parameter.Type, property.Type));
    }

    private static List<IPropertySymbol>? GetProperties(INamedTypeSymbol type)
    {
        var properties = new List<IPropertySymbol>();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var current = type; current is not null; current = current.BaseType)
        {
            var members = current.GetMembers();
            if (HasCustomContract(current) || members.Any(member => HasAttribute(member, JsonSerialization + "JsonIncludeAttribute")) ||
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
            if (GetIgnoreCondition(property) != JsonIgnoreAlways)
            {
                properties.Add(property);
            }
        }

        return true;
    }

    private static bool IsPublicInstanceProperty(IPropertySymbol property) =>
        !property.IsStatic && !property.IsIndexer && (property.GetMethod?.DeclaredAccessibility == Accessibility.Public ||
            property.SetMethod?.DeclaredAccessibility == Accessibility.Public);

    private static IMethodSymbol? GetJsonConstructor(INamedTypeSymbol type)
    {
        var constructor = type.InstanceConstructors.FirstOrDefault(candidate =>
            HasAttribute(candidate, JsonSerialization + "JsonConstructorAttribute"));
        if (constructor is null && !type.IsValueType)
        {
            var publicConstructors = type.InstanceConstructors.Where(candidate =>
                candidate.DeclaredAccessibility == Accessibility.Public).ToArray();
            constructor = publicConstructors.Length == 1
                ? publicConstructors[0]
                : publicConstructors.FirstOrDefault(candidate => candidate.Parameters.IsEmpty);
        }

        return constructor;
    }

    private static (string Type, string? Format)? Scalar(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Enum)
        {
            return (JsonInteger, null);
        }

        if (type is IArrayTypeSymbol { Rank: 1, ElementType.SpecialType: SpecialType.System_Byte })
        {
            return (JsonString, "byte");
        }

        if (ScalarTypes.TryGetValue(type.SpecialType, out var scalar))
        {
            return (scalar, null);
        }

        return StringFormats.TryGetValue(type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(), out var format)
            ? (JsonString, format)
            : null;
    }

    private static bool HasCustomContract(ISymbol symbol)
    {
        foreach (var attributeType in symbol.GetAttributes().Select(attribute => attribute.AttributeClass))
        {
            for (var type = attributeType; type is not null; type = type.BaseType)
            {
                if (CustomContractAttributes.Contains(type.ToDisplayString()))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static int GetIgnoreCondition(IPropertySymbol property)
    {
        var attribute = GetAttribute(property, JsonSerialization + "JsonIgnoreAttribute");
        return attribute is null
            ? JsonIgnoreNever
            : (int?)attribute.NamedArguments.FirstOrDefault().Value.Value ?? JsonIgnoreAlways;
    }

    private static string PropertyName(IPropertySymbol property)
    {
        return GetAttribute(property, JsonSerialization + "JsonPropertyNameAttribute")?.ConstructorArguments[0].Value is string name
            ? Literal(name)
            : $"naming?.ConvertName({Literal(property.Name)}) ?? {Literal(property.Name)}";
    }

    private static bool HasAttribute(ISymbol symbol, string name) => GetAttribute(symbol, name) is not null;

    private static AttributeData? GetAttribute(ISymbol symbol, string name) => symbol.GetAttributes()
        .FirstOrDefault(attribute => attribute.AttributeConstructor?.ContainingType.ToDisplayString() == name);

    private static string TypeValue(string type, bool nullable) => nullable
        ? $"new {JsonArrayType}({Literal(type)}, \"null\")"
        : Literal(type);

    private static string Object(params (string Name, string Value)[] properties) =>
        $"new {JsonObjectType} {{ {string.Join(", ", properties.Select(property => $"[{Literal(property.Name)}] = {property.Value}"))} }}";

    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, true);
}
