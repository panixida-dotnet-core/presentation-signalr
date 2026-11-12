using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using Asp.Versioning;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

using PANiXiDA.Core.ApiDocumentation;
using PANiXiDA.Core.ApiDocumentation.DependencyInjection;
using PANiXiDA.Core.Presentation.SignalR.DependencyInjection;
using PANiXiDA.Core.Presentation.SignalR.Hubs;

namespace PANiXiDA.Core.Presentation.SignalR.UnitTests.Documentation;

public sealed class GeneratedPayloadSchemaTests
{
    [Fact(DisplayName = "Generated payload schemas describe JSON property names, nullability and nested types")]
    public async Task GeneratesSchemasFromHubSignatures()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSignalR(builder.Configuration).AddJsonProtocol(options =>
            options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
        await using var app = builder.Build();
        app.UseSignalR(hubs => GeneratedSchemaHub.Map(hubs));

        var schema = GenerateResultSchema(app, "generated");

        var properties = schema["properties"]!.AsObject();
        properties.Select(property => property.Key).ShouldBe(
            ["game_id", "queued_at", "name", "scores", "metadata", "blob", "link", "status", "labels", "aliases", "is_ready"], ignoreOrder: true);
        properties["game_id"]!["type"]!.AsArray().Select(item => item!.GetValue<string>()).ShouldBe(["string", "null"]);
        properties["game_id"]!["format"]!.GetValue<string>().ShouldBe("uuid");
        properties["queued_at"]!["format"]!.GetValue<string>().ShouldBe("date-time");
        properties["scores"]!["items"]!["type"]!.AsArray().Select(item => item!.GetValue<string>()).ShouldBe(["integer", "null"]);
        properties["metadata"]!["additionalProperties"]!["properties"]!["ratio"]!["type"]!.GetValue<string>().ShouldBe("number");
        properties["metadata"]!["additionalProperties"]!["type"]!.AsArray().Select(item => item!.GetValue<string>())
            .ShouldBe(["object", "null"]);
        properties["labels"]!["items"]!["type"]!.AsArray().Select(item => item!.GetValue<string>())
            .ShouldBe(["string", "null"]);
        properties["aliases"]!["items"]!["type"]!.AsArray().Select(item => item!.GetValue<string>())
            .ShouldBe(["string", "null"]);
        properties["blob"]!["format"]!.GetValue<string>().ShouldBe("byte");
        properties["link"]!["format"]!.GetValue<string>().ShouldBe("uri");
        properties["status"]!["type"]!.GetValue<string>().ShouldBe("integer");
        properties["is_ready"]!["type"]!.GetValue<string>().ShouldBe("boolean");
        schema["required"]!.AsArray().Select(item => item!.GetValue<string>())
            .ShouldBe(properties.Select(property => property.Key), ignoreOrder: true);
    }

    [Fact(DisplayName = "Closed generic DTOs preserve nested shapes and the selected JSON constructor's required members")]
    public async Task GeneratesSchemasForConstructedTypesAndRequiredMembers()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSignalR(builder.Configuration);
        await using var app = builder.Build();
        app.UseSignalR(hubs => ConstructedSchemaHub.Map(hubs));

        var document = GenerateDocument(app, "constructed");
        var request = document["components"]!["messages"]!.AsObject().Single(message =>
            !message.Value!["name"]!.GetValue<string>().EndsWith(".result", StringComparison.Ordinal)).Value!;
        var input = request["payload"]!["schema"]!["properties"]!["arguments"]!["prefixItems"]![0]!;
        var output = GetResultSchema(document);

        var inputPayload = input["properties"]!["value"]!["properties"]!["value"]!;
        inputPayload["properties"]!["name"]!["type"]!.GetValue<string>().ShouldBe("string");
        inputPayload["properties"]!["readOnly"].ShouldBeNull();
        inputPayload["properties"]!["writeOnly"].ShouldNotBeNull();
        inputPayload["required"]!.AsArray().Select(item => item!.GetValue<string>())
            .ShouldBe(["sequence", "count", "note", "token"], ignoreOrder: true);

        var outputPayload = output["properties"]!["value"]!["properties"]!["value"]!;
        outputPayload["properties"]!["readOnly"].ShouldNotBeNull();
        outputPayload["properties"]!["writeOnly"].ShouldBeNull();
        outputPayload["required"]!.AsArray().Select(item => item!.GetValue<string>())
            .ShouldBe(["sequence", "token"], ignoreOrder: true);
    }

    [Fact(DisplayName = "A custom JSON type resolver requires explicit documentation metadata")]
    public async Task RejectsGeneratedSchemaForModifiedResolver()
    {
        var builder = WebApplication.CreateSlimBuilder();
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(typeInfo =>
        {
            if (typeInfo.Type == typeof(GeneratedSchemaPayload))
            {
                typeInfo.Properties.Single(property => property.Name == "name").Name = "displayName";
            }
        });
        builder.Services.AddSignalR(builder.Configuration).AddJsonProtocol(options =>
            options.PayloadSerializerOptions.TypeInfoResolver = resolver);
        await using var app = builder.Build();
        app.UseSignalR(hubs => GeneratedSchemaHub.Map(hubs));

        var error = Should.Throw<InvalidOperationException>(() => GenerateResultSchema(app, "generated"));

        error.Message.ShouldContain(nameof(GeneratedSchemaPayload));
    }

    [Theory(DisplayName = "A JSON context supplies the contract for a configured converter on a nested DTO member")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UsesExplicitContextForConfiguredConverter(bool registerContext)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSignalR(builder.Configuration).AddJsonProtocol(options =>
            options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter<GeneratedSchemaState>()));
        if (registerContext)
        {
            builder.Services.AddApiDocumentationSchemas(GeneratedSchemaJsonContext.Default);
        }

        await using var app = builder.Build();
        app.UseSignalR(hubs => GeneratedSchemaHub.Map(hubs));

        if (registerContext)
        {
            var schema = GenerateResultSchema(app, "generated");
            schema["properties"]!["status"]!["enum"]!.AsArray().Select(item => item!.GetValue<string>())
                .ShouldBe(["Searching", "Matched"]);
        }
        else
        {
            var error = Should.Throw<InvalidOperationException>(() => GenerateResultSchema(app, "generated"));
            error.Message.ShouldContain(nameof(GeneratedSchemaPayload));
        }
    }

    [Theory(DisplayName = "Types declaring a JSON converter require an explicit documentation context")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UsesExplicitContextForConverterAttribute(bool registerContext)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSignalR(builder.Configuration);
        if (registerContext)
        {
            builder.Services.AddApiDocumentationSchemas(GeneratedSchemaJsonContext.Default);
        }

        await using var app = builder.Build();
        app.UseSignalR(hubs => ConverterSchemaHub.Map(hubs));

        if (registerContext)
        {
            var schema = GenerateResultSchema(app, "converter");
            schema["enum"]!.AsArray().Select(item => item!.GetValue<string>()).ShouldBe(["Ready"]);
        }
        else
        {
            var error = Should.Throw<InvalidOperationException>(() => GenerateResultSchema(app, "converter"));
            error.Message.ShouldContain(nameof(ConverterSchemaState));
        }
    }

    private static JsonNode GenerateResultSchema(WebApplication app, string module) =>
        GetResultSchema(GenerateDocument(app, module));

    private static JsonObject GenerateDocument(WebApplication app, string module)
    {
        var source = app.Services.GetServices<IApiDocumentSource>().OfType<IAsyncApiDocumentSource>().Single();
        return source.Generate(module + "-signalr-v1", TestContext.Current.CancellationToken);
    }

    private static JsonNode GetResultSchema(JsonObject document)
    {
        var reply = document["components"]!["messages"]!.AsObject().Single(message =>
            message.Value!["name"]!.GetValue<string>().EndsWith(".result", StringComparison.Ordinal)).Value!;
        return reply["payload"]!["schema"]!["oneOf"]![0]!["properties"]!["result"]!;
    }
}

#pragma warning disable CA1822 // Hub methods are instance endpoints.
public sealed class GeneratedSchemaHub : Hub, IDocumentedHubEndpoint
{
    public static string Name => "generated";

    public static string Summary => "Generated";

    public static ApiVersion ApiVersion => new(1, 0);

    public static HubEndpointConventionBuilder Map(HubMapBuilder hubs) => hubs.MapHub<GeneratedSchemaHub>("/generated");

    public GeneratedSchemaPayload Echo(GeneratedSchemaPayload payload) => payload;
}

public sealed class ConstructedSchemaHub : Hub, IDocumentedHubEndpoint
{
    public static string Name => "constructed";

    public static string Summary => "Constructed";

    public static ApiVersion ApiVersion => new(1, 0);

    public static HubEndpointConventionBuilder Map(HubMapBuilder hubs) => hubs.MapHub<ConstructedSchemaHub>("/constructed");

    public SchemaBox<SchemaBox<InitializedSchemaPayload>> Echo(SchemaBox<SchemaBox<InitializedSchemaPayload>> payload) => payload;
}

public sealed class ConverterSchemaHub : Hub, IDocumentedHubEndpoint
{
    public static string Name => "converter";

    public static string Summary => "Converter";

    public static ApiVersion ApiVersion => new(1, 0);

    public static HubEndpointConventionBuilder Map(HubMapBuilder hubs) => hubs.MapHub<ConverterSchemaHub>("/converter");

    public ConverterSchemaState Read() => ConverterSchemaState.Ready;
}
#pragma warning restore CA1822

public sealed record GeneratedSchemaPayload(Guid? GameId, DateTimeOffset? QueuedAt,
    [property: JsonPropertyName("name")] string DisplayName, IReadOnlyList<int?> Scores,
    Dictionary<string, GeneratedSchemaNested> Metadata, byte[] Blob, Uri? Link, GeneratedSchemaState Status,
    string[] Labels, List<string> Aliases)
{
    [JsonIgnore]
    public string Ignored => DisplayName;

    [JsonRequired]
    public bool IsReady { get; init; }
}

public sealed record SchemaBox<T>(T Value);

[method: SetsRequiredMembers]
public sealed class InitializedSchemaPayload(int sequence, int count, string? note)
{
    public int Sequence { get; } = sequence;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Count { get; } = count;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; } = note;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenReading)]
    public string? ReadOnly { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public string? WriteOnly { get; init; }

    public required string Name { get; init; } = "initialized";

    [JsonRequired]
    public string? Token { get; init; }
}

public sealed record GeneratedSchemaNested(int Count)
{
    public double Ratio { get; init; }
}

public enum GeneratedSchemaState
{
    Searching,
    Matched
}

[JsonConverter(typeof(JsonStringEnumConverter<ConverterSchemaState>))]
public enum ConverterSchemaState
{
    Ready
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(GeneratedSchemaPayload))]
[JsonSerializable(typeof(ConverterSchemaState))]
internal sealed partial class GeneratedSchemaJsonContext : JsonSerializerContext;
