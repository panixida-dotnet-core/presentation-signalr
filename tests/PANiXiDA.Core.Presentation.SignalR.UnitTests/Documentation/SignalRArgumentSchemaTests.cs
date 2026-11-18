using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Asp.Versioning;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

using PANiXiDA.Core.ApiDocumentation;
using PANiXiDA.Core.ApiDocumentation.DependencyInjection;
using PANiXiDA.Core.Presentation.SignalR.DependencyInjection;
using PANiXiDA.Core.Presentation.SignalR.Hubs;

namespace PANiXiDA.Core.Presentation.SignalR.UnitTests.Documentation;

public sealed class SignalRArgumentSchemaTests
{
    [Theory(DisplayName = "Single DTO arguments expose a named model with generated or registered JSON metadata")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NamesSingleArgumentModels(bool registerContext)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSignalR(builder.Configuration);
        builder.Services.AddAuthorization();
        if (registerContext)
        {
            builder.Services.AddApiDocumentationSchemas(DocumentationJsonContext.Default);
        }

        await using var app = builder.Build();
        app.UseSignalR(hubs => DocumentationHub.Map(hubs));

        var document = Generate(app, "matchmaking-signalr-v1");
        var arguments = Arguments(document, nameof(IDocumentationClient.Changed));

        arguments["minItems"]!.GetValue<int>().ShouldBe(1);
        arguments["maxItems"]!.GetValue<int>().ShouldBe(1);
        arguments["prefixItems"].ShouldBeNull();
        arguments["items"].ShouldNotBeNull();
        var reference = arguments["items"]!["$ref"]!.GetValue<string>();
        reference.ShouldEndWith("/properties/arguments/$defs/DocumentationPayload");
        var model = Resolve(document, reference);
        model.ShouldBeSameAs(arguments["$defs"]![nameof(DocumentationPayload)]);
        model["properties"]!["id"]!["format"]!.GetValue<string>().ShouldBe("uuid");
        model["properties"]!["state"].ShouldNotBeNull();
    }

    [Theory(DisplayName = "Argument schemas preserve exact counts and positional types")]
    [InlineData("Watch", false, 0)]
    [InlineData("read-state", false, 1)]
    [InlineData("read-state", true, 2)]
    public async Task PreservesArgumentCounts(string target, bool disableImplicitServices, int count)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSignalR(builder.Configuration).AddHubOptions<DocumentationHub>(options =>
            options.DisableImplicitFromServicesParameters = disableImplicitServices);
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<DocumentationDependency>();
        await using var app = builder.Build();
        app.UseSignalR(hubs => DocumentationHub.Map(hubs));

        var document = Generate(app, "matchmaking-signalr-v1");
        var arguments = Arguments(document, target);

        arguments["minItems"]!.GetValue<int>().ShouldBe(count);
        arguments["maxItems"]!.GetValue<int>().ShouldBe(count);
        if (count == 1)
        {
            arguments["prefixItems"].ShouldBeNull();
            var model = Resolve(document, arguments["items"]!["$ref"]!.GetValue<string>());
            model["format"]!.GetValue<string>().ShouldBe("uuid");
        }
        else
        {
            arguments["items"].ShouldBeNull();
            arguments["$defs"].ShouldBeNull();
            if (count == 0)
            {
                arguments["prefixItems"].ShouldBeNull();
            }
            else
            {
                var positions = arguments["prefixItems"]!.AsArray();
                positions.Count.ShouldBe(count);
                positions[0]!["format"]!.GetValue<string>().ShouldBe("uuid");
                positions[1]!["properties"].ShouldNotBeNull();
            }
        }
    }

    [Fact(DisplayName = "Recursive argument references resolve inside the named model in the completed AsyncAPI document")]
    public async Task RebasesRecursiveArgumentReferences()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSignalR(builder.Configuration);
        builder.Services.AddApiDocumentationSchemas(ArgumentSchemaJsonContext.Default);
        await using var app = builder.Build();
        app.UseSignalR(hubs => RecursiveArgumentHub.Map(hubs));

        var document = Generate(app, "argument-schema-signalr-v1");
        var arguments = Arguments(document, nameof(RecursiveArgumentHub.Echo));

        var reference = arguments["items"].ShouldNotBeNull()["$ref"]!.GetValue<string>();
        var model = Resolve(document, reference);
        var nested = model["properties"]!["next"]!;
        var recursiveReference = nested["properties"]!["next"]!["$ref"]!.GetValue<string>();
        recursiveReference.ShouldBe(reference + "/properties/next");
        Resolve(document, recursiveReference).ShouldBeSameAs(nested);
    }

    [Fact(DisplayName = "Array arguments retain their named array contract and recursive element references")]
    public async Task NamesArrayArguments()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSignalR(builder.Configuration);
        builder.Services.AddApiDocumentationSchemas(ArgumentSchemaJsonContext.Default);
        await using var app = builder.Build();
        app.UseSignalR(hubs => RecursiveArgumentHub.Map(hubs));

        var document = Generate(app, "argument-schema-signalr-v1");
        var arguments = Arguments(document, nameof(RecursiveArgumentHub.EchoArray));

        var reference = arguments["items"]!["$ref"]!.GetValue<string>();
        reference.ShouldEndWith("/properties/arguments/$defs/RecursiveArgumentArray");
        var model = Resolve(document, reference);
        var element = model["items"]!;
        element["properties"]!["name"].ShouldNotBeNull();
        var nested = element["properties"]!["next"]!;
        var recursiveReference = nested["properties"]!["next"]!["$ref"]!.GetValue<string>();
        Resolve(document, recursiveReference).ShouldBeSameAs(nested);
    }

    private static JsonObject Generate(WebApplication app, string name) => app.Services
        .GetServices<IApiDocumentSource>().OfType<IAsyncApiDocumentSource>().Single()
        .Generate(name, TestContext.Current.CancellationToken);

    private static JsonObject Arguments(JsonObject document, string target) => document["components"]!["messages"]!
        .AsObject().Select(message => message.Value!["payload"]!["schema"]!)
        .Single(schema => schema["properties"]?["target"]?["const"]?.GetValue<string>() == target)
        ["properties"]!["arguments"]!.AsObject();

    private static JsonNode Resolve(JsonObject document, string reference)
    {
        reference.ShouldStartWith("#/");
        JsonNode node = document;
        foreach (var segment in reference[2..].Split('/'))
        {
            var key = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            node = node[key] ?? throw new InvalidOperationException($"Unresolved schema reference '{reference}'.");
        }

        return node;
    }
}

public sealed class RecursiveArgumentHub : Hub, IDocumentedHubEndpoint
{
    public static string Name => "argument-schema";
    public static string Summary => "Argument schemas";
    public static ApiVersion ApiVersion => new(1, 0);
    public static HubEndpointConventionBuilder Map(HubMapBuilder hubs) => hubs.MapHub<RecursiveArgumentHub>("/recursive");
    public RecursiveArgument Echo(RecursiveArgument value)
    {
        Context.ConnectionAborted.ThrowIfCancellationRequested();
        return value;
    }

    public RecursiveArgument[] EchoArray(RecursiveArgument[] values)
    {
        Context.ConnectionAborted.ThrowIfCancellationRequested();
        return values;
    }
}

public sealed record RecursiveArgument(string Name, RecursiveArgument? Next);

[JsonSerializable(typeof(RecursiveArgument))]
[JsonSerializable(typeof(RecursiveArgument[]))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class ArgumentSchemaJsonContext : JsonSerializerContext;
