using System.Collections.Immutable;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace PANiXiDA.Core.Presentation.SignalR.Generators.Hubs;

/// <summary>Generates direct hub mapping calls for each consuming assembly.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class HubRegistrationGenerator : IIncrementalGenerator
{
    private const string ContractName = "PANiXiDA.Core.Presentation.SignalR.Hubs.IHubEndpoint";
    private const string RegistryName = "PANiXiDA.Core.Presentation.SignalR.Hubs.HubRegistry";
    private const string HubName = "Microsoft.AspNetCore.SignalR.Hub";
    private const string Category = "HubRegistration";

    private static readonly DiagnosticDescriptor UnsupportedType = new(
        "PANSIGNALRSG001",
        "Hub registration requires accessible closed types",
        "Hub '{0}' must be a non-generic type accessible from generated code; private, protected and file-local types are not supported",
        Category,
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor NotAHub = new(
        "PANSIGNALRSG002",
        "Hub registration requires a SignalR hub",
        "Type '{0}' implements IHubEndpoint but does not derive from Microsoft.AspNetCore.SignalR.Hub",
        Category,
        DiagnosticSeverity.Error,
        true);

    /// <summary>Registers semantic discovery of concrete hub mapping contracts.</summary>
    /// <param name="context">The incremental generator initialization context.</param>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var types = context.SyntaxProvider.CreateSyntaxProvider(
            static (node, _) => node is TypeDeclarationSyntax { BaseList: not null },
            static (syntax, token) => (INamedTypeSymbol)syntax.SemanticModel.GetDeclaredSymbol(syntax.Node, token)!)
            .Where(static type => !type.IsAbstract)
            .Collect();

        context.RegisterSourceOutput(types.Combine(context.CompilationProvider),
            static (output, input) => Generate(output, input.Left, input.Right));
    }

    private static void Generate(
        SourceProductionContext context,
        ImmutableArray<INamedTypeSymbol> candidates,
        Compilation compilation)
    {
        var contract = compilation.GetTypeByMetadataName(ContractName);
        if (contract is null)
        {
            return;
        }

        var hubs = new List<INamedTypeSymbol>();
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var type in candidates.OrderBy(GetRuntimeName, StringComparer.Ordinal))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(type) || !type.AllInterfaces.Any(item => SymbolEqualityComparer.Default.Equals(item, contract)))
            {
                continue;
            }

            if (!IsHub(type))
            {
                context.ReportDiagnostic(Diagnostic.Create(NotAHub, type.Locations[0], type.ToDisplayString()));
                continue;
            }

            if (!IsSupportedType(type, compilation))
            {
                context.ReportDiagnostic(Diagnostic.Create(UnsupportedType, type.Locations[0], type.ToDisplayString()));
                continue;
            }

            hubs.Add(type);
        }

        context.AddSource("HubRegistrations.g.cs", SourceText.From(BuildSource(hubs), Encoding.UTF8));
    }

    private static bool IsHub(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == HubName)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSupportedType(INamedTypeSymbol type, Compilation compilation)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (current.Arity > 0 || current.IsFileLocal)
            {
                return false;
            }
        }

        return compilation.IsSymbolAccessibleWithin(type, compilation.Assembly);
    }

    private static string GetRuntimeName(INamedTypeSymbol type)
    {
        if (type.ContainingType is not null)
        {
            return GetRuntimeName(type.ContainingType) + "+" + type.MetadataName;
        }

        return type.ContainingNamespace.IsGlobalNamespace
            ? type.MetadataName
            : type.ContainingNamespace.ToDisplayString() + "." + type.MetadataName;
    }

    private static string BuildSource(List<INamedTypeSymbol> hubs)
    {
        var mappings = new StringBuilder();
        foreach (var hub in hubs)
        {
            mappings.Append("        global::").Append(RegistryName).Append(".MapHub<")
                .Append(hub.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                .AppendLine(">(hubs);");
        }

        return $$"""
            // <auto-generated />
            #nullable enable
            #pragma warning disable CA2255

            [global::System.CodeDom.Compiler.GeneratedCode("PANiXiDA.Core.Presentation.SignalR.Generators", "1.0")]
            file static class GeneratedHubRegistrations
            {
                [global::System.Runtime.CompilerServices.ModuleInitializer]
                internal static void Initialize()
                {
                    global::{{RegistryName}}.RegisterAssembly(
                        typeof(GeneratedHubRegistrations).Assembly,
                        MapHubs);
                }

                private static void MapHubs(global::PANiXiDA.Core.Presentation.SignalR.Hubs.HubMapBuilder hubs)
                {
            {{mappings}}
                }
            }
            """;
    }
}
