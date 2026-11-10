using System.Text;
using System.Xml.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace PANiXiDA.Core.Presentation.SignalR.Generators.Hubs;

internal static class HubDocumentationEmitter
{
    private const string ContractName = "PANiXiDA.Core.Presentation.SignalR.Hubs.IDocumentedHubEndpoint";
    private const string Metadata = "global::PANiXiDA.Core.Presentation.SignalR.Documentation.";
    private static readonly DiagnosticDescriptor UnsupportedMethod = new(
        "PANSIGNALRSG003", "Documented hub methods must have unique targets and closed payload types",
        "Hub method '{0}' has a duplicate target, generic parameters or unsupported payload types",
        "HubRegistration", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor InvalidOperationName = new(
        "PANSIGNALRSG004", "Operation display names must not be empty",
        "Hub method '{0}' declares an empty EndpointName",
        "HubRegistration", DiagnosticSeverity.Error, true);

    internal static bool HasContract(INamedTypeSymbol hub) => hub.AllInterfaces.Any(type => type.ToDisplayString() == ContractName);

    internal static string Build(IEnumerable<INamedTypeSymbol> hubs, SourceProductionContext context)
    {
        var result = new StringBuilder();
        foreach (var hub in hubs)
        {
            AppendRegistration(result, hub);
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in HubMethods(hub))
            {
                if (AttributeValue(entry.Method, "Microsoft.AspNetCore.Routing.EndpointNameAttribute") is { } name && string.IsNullOrWhiteSpace(name))
                {
                    context.ReportDiagnostic(Diagnostic.Create(InvalidOperationName, entry.Method.Locations[0], entry.Method.ToDisplayString()));
                    continue;
                }

                var target = Target(entry.Method);
                if (!targets.Add((entry.Client ? "client:" : "hub:") + target) || !IsSupported(entry.Method, entry.Client))
                {
                    context.ReportDiagnostic(Diagnostic.Create(UnsupportedMethod, entry.Method.Locations[0], entry.Method.ToDisplayString()));
                    continue;
                }

                AppendMethod(result, entry.Method, target, entry.Client);
            }

            result.AppendLine("            });");
        }

        return result.ToString();
    }

    private static void AppendRegistration(StringBuilder result, INamedTypeSymbol hub)
    {
        result.Append("        ").Append(Metadata).Append("HubDocumentationRegistry.RegisterHub<")
            .Append(TypeName(hub)).AppendLine(">(")
            .Append("            new ").Append(Metadata).AppendLine("HubMethodDescription[] {");
    }

    private static IEnumerable<(IMethodSymbol Method, bool Client)> HubMethods(INamedTypeSymbol hub)
    {
        var methods = Methods(hub).Select(method => (Method: method, Client: false)).ToList();
        for (var current = hub.BaseType; current is not null; current = current.BaseType)
        {
            if (current.OriginalDefinition.ToDisplayString() != "Microsoft.AspNetCore.SignalR.Hub<T>")
            {
                continue;
            }

            if (current.TypeArguments[0] is INamedTypeSymbol { TypeKind: TypeKind.Interface } client)
            {
                methods.AddRange(Methods(client).Select(method => (Method: method, Client: true)));
            }

            break;
        }

        return methods;
    }

    private static string Target(IMethodSymbol method) => MethodAttributes(method).FirstOrDefault(attribute =>
        AttributeName(attribute) == "Microsoft.AspNetCore.SignalR.HubMethodNameAttribute")
        ?.ConstructorArguments[0].Value as string ?? method.Name;

    private static bool IsSupported(IMethodSymbol method, bool client)
    {
        if (method.IsGenericMethod || method.ReturnsByRef || method.ReturnsByRefReadonly || UnsupportedPayload(method.ReturnType) ||
            method.Parameters.Any(parameter => parameter.RefKind != RefKind.None || UnsupportedPayload(parameter.Type)))
        {
            return false;
        }

        Unwrap(method.ReturnType, out var returnStream);
        return !client || !returnStream && !method.Parameters.Any(parameter =>
        {
            StreamItem(parameter.Type, out var stream);
            return stream;
        });
    }

    private static bool UnsupportedPayload(ITypeSymbol type) => type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer ||
        type is INamedTypeSymbol { IsRefLikeType: true };
    private static void AppendMethod(StringBuilder result, IMethodSymbol method, string target, bool client)
    {
        var returnType = Unwrap(method.ReturnType, out var resultStream);
        var parameters = method.Parameters.Where(parameter => ShouldSerialize(parameter, resultStream, client));
        result.Append("            new ").Append(Metadata).Append("HubMethodDescription(")
            .Append(Literal(target)).Append(", new ").Append(Metadata).AppendLine("HubParameterDescription[] {");
        foreach (var parameter in parameters)
        {
            AppendParameter(result, parameter);
        }

        result.Append("            }, ").Append(returnType is null ? "null" : "typeof(" + TypeName(returnType) + ")")
            .Append(", isClientMethod: ").Append(client ? "true" : "false")
            .Append(", isStream: ").Append(resultStream ? "true" : "false")
            .Append(", summary: ").Append(Literal(Summary(method)))
            .Append(", requiresAuthorization: ").Append(HasMetadata(method, "Microsoft.AspNetCore.Authorization.IAuthorizeData") ? "true" : "false").AppendLine(") {")
            .Append("                MemberName = ").Append(Literal(method.Name)).AppendLine(",")
            .Append("                Documentation = new global::PANiXiDA.Core.ApiDocumentation.ApiOperationMetadata(")
            .Append(Literal(AttributeValue(method, "Microsoft.AspNetCore.Routing.EndpointNameAttribute") ?? method.Name)).Append(", ")
            .Append(Literal(AttributeValue(method, "Microsoft.AspNetCore.Http.EndpointSummaryAttribute") ?? Summary(method))).AppendLine(") },");
    }

    private static void AppendParameter(StringBuilder result, IParameterSymbol parameter)
    {
        var payload = StreamItem(parameter.Type, out var stream);
        result.Append("                new ").Append(Metadata).Append("HubParameterDescription(")
            .Append(Literal(parameter.Name)).Append(", typeof(").Append(TypeName(payload))
            .Append("), ").Append(stream ? "true" : "false")
            .Append(", allowImplicitServices: ").Append(parameter.GetAttributes().Length == 0 ? "true" : "false").AppendLine("),");
    }

    private static bool ShouldSerialize(IParameterSymbol parameter, bool resultStream, bool client)
    {
        if (parameter.Type.ToDisplayString() == "System.Threading.CancellationToken" &&
            (!client && resultStream || client && parameter.Ordinal == ((IMethodSymbol)parameter.ContainingSymbol).Parameters.Length - 1))
        {
            return false;
        }

        return client || !parameter.GetAttributes().Any(IsServiceAttribute);
    }

    private static bool IsServiceAttribute(AttributeData attribute)
    {
        var type = attribute.AttributeConstructor?.ContainingType;
        return type is not null && (type.AllInterfaces.Any(item =>
            item.ToDisplayString() == "Microsoft.AspNetCore.Http.Metadata.IFromServiceMetadata") ||
            type.ToDisplayString() == "Microsoft.Extensions.DependencyInjection.FromKeyedServicesAttribute");
    }

    private static IEnumerable<AttributeData> MethodAttributes(IMethodSymbol method)
    {
        for (var current = method; current is not null; current = current.OverriddenMethod)
        {
            foreach (var attribute in current.GetAttributes())
            {
                yield return attribute;
            }
        }
    }

    private static string AttributeName(AttributeData attribute) => attribute.AttributeConstructor?.ContainingType.ToDisplayString() ?? string.Empty;

    private static string? AttributeValue(IMethodSymbol method, string attributeName)
    {
        var attribute = method.GetAttributes().FirstOrDefault(item => AttributeName(item) == attributeName);
        return attribute?.ConstructorArguments.FirstOrDefault().Value as string;
    }

    private static IEnumerable<IMethodSymbol> Methods(INamedTypeSymbol type)
    {
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        IEnumerable<INamedTypeSymbol> hierarchy;
        if (type.TypeKind == TypeKind.Interface)
        {
            hierarchy = new[] { type }.Concat(type.AllInterfaces);
        }
        else
        {
            var types = new List<INamedTypeSymbol>();
            for (var current = type; current.ToDisplayString() != "Microsoft.AspNetCore.SignalR.Hub" &&
                current.OriginalDefinition.ToDisplayString() != "Microsoft.AspNetCore.SignalR.Hub<T>"; current = current.BaseType!)
            {
                types.Add(current);
            }

            hierarchy = types;
        }

        foreach (var method in hierarchy.SelectMany(current => current.GetMembers().OfType<IMethodSymbol>())
                     .OrderBy(method => method.Name, StringComparer.Ordinal))
        {
            if (method.MethodKind != MethodKind.Ordinary || method.IsStatic || method.DeclaredAccessibility != Accessibility.Public ||
                type.TypeKind != TypeKind.Interface && IsFrameworkMethod(method))
            {
                continue;
            }

            var signature = method.Name + "(" + string.Join(",", method.Parameters.Select(parameter => parameter.Type.ToDisplayString())) + ")";
            if (signatures.Add(signature))
            {
                yield return method;
            }
        }
    }

    private static bool IsFrameworkMethod(IMethodSymbol method)
    {
        var definition = method;
        while (definition.OverriddenMethod is { } overridden)
        {
            definition = overridden;
        }

        return definition.ContainingType.SpecialType == SpecialType.System_Object ||
            definition.ContainingType.ToDisplayString() == "Microsoft.AspNetCore.SignalR.Hub" ||
            method.Name == "Dispose" && method.Parameters.Length == 0;
    }

    private static ITypeSymbol? Unwrap(ITypeSymbol type, out bool stream)
    {
        if (type is INamedTypeSymbol named && named.IsGenericType &&
            named.OriginalDefinition.ToDisplayString() is "System.Threading.Tasks.Task<TResult>" or "System.Threading.Tasks.ValueTask<TResult>")
        {
            type = named.TypeArguments[0];
        }

        if (type.SpecialType == SpecialType.System_Void || type.ToDisplayString() is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask")
        {
            stream = false;
            return null;
        }

        return ReturnStreamItem(type, out stream);
    }

    private static ITypeSymbol ReturnStreamItem(ITypeSymbol type, out bool stream)
    {
        if (type is INamedTypeSymbol named)
        {
            var enumerable = new[] { named }.Concat(named.AllInterfaces).FirstOrDefault(candidate =>
                candidate.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.IAsyncEnumerable<T>");
            if (enumerable is not null)
            {
                stream = true;
                return enumerable.TypeArguments[0];
            }

            for (var current = named; current is not null; current = current.BaseType)
            {
                if (current.OriginalDefinition.ToDisplayString() == "System.Threading.Channels.ChannelReader<T>")
                {
                    stream = true;
                    return current.TypeArguments[0];
                }
            }
        }

        stream = false;
        return type;
    }

    private static ITypeSymbol StreamItem(ITypeSymbol type, out bool stream)
    {
        stream = type is INamedTypeSymbol named && named.IsGenericType &&
            named.OriginalDefinition.ToDisplayString() is "System.Collections.Generic.IAsyncEnumerable<T>" or "System.Threading.Channels.ChannelReader<T>";
        return stream ? ((INamedTypeSymbol)type).TypeArguments[0] : type;
    }

    private static bool HasMetadata(IMethodSymbol method, string name) => MethodAttributes(method).Any(attribute => attribute.AttributeConstructor?.ContainingType.AllInterfaces.Any(item => item.ToDisplayString() == name) == true);

    private static string? Summary(IMethodSymbol method)
    {
        var xml = method.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        var summary = XDocument.Parse(xml).Root!.Element("summary")?.Value;
        return summary is null ? null : string.Join(" ", summary.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Literal(string? value) => value is null ? "null" : SymbolDisplay.FormatLiteral(value, true);

    private static string TypeName(ITypeSymbol type) => type.TypeKind == TypeKind.Dynamic ? "global::System.Object" : type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
}
