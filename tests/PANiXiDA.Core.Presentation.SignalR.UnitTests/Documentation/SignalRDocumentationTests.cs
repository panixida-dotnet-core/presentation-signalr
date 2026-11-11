using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Channels;

using Asp.Versioning;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using PANiXiDA.Core.ApiDocumentation;
using PANiXiDA.Core.ApiDocumentation.DependencyInjection;
using PANiXiDA.Core.Presentation.SignalR.DependencyInjection;
using PANiXiDA.Core.Presentation.SignalR.Documentation;
using PANiXiDA.Core.Presentation.SignalR.Hubs;

namespace PANiXiDA.Core.Presentation.SignalR.UnitTests.Documentation;

public sealed class SignalRDocumentationTests
{
    [Fact(DisplayName = "Generated hub documentation follows mapped paths versions targets callbacks and streams over Kestrel")]
    public async Task GeneratesMappedContracts()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSignalR(builder.Configuration);
        builder.Services.AddAuthorization();
        builder.Services.AddApiDocumentationSchemas(DocumentationJsonContext.Default);
        builder.Services.AddSingleton<DocumentationDependency>();
        await using var app = builder.Build();
        app.MapGet("/plain-before", static () => "ready");
        app.UseSignalR(hubs =>
        {
            DocumentationHub.Map(hubs);
            hubs.MapHub<DocumentationV2Hub>("/v2/state");
        });
        var source = app.Services.GetServices<IApiDocumentSource>().OfType<IAsyncApiDocumentSource>().Single();
        Task Handler(HttpContext context) => context.Response.WriteAsync(
            source.Generate("matchmaking-signalr-v1", context.RequestAborted).ToJsonString(), context.RequestAborted);
        app.MapGet("/contract", (RequestDelegate)Handler);
        await app.StartAsync(TestContext.Current.CancellationToken);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        var document = JsonNode.Parse(await client.GetStringAsync("/contract", TestContext.Current.CancellationToken))!;
        source.GetDocuments().Select(document => document.Name).Order().ShouldBe(["matchmaking-signalr-v1", "matchmaking-signalr-v2"]);
        document["info"]!["version"]!.GetValue<string>().ShouldBe("1.0");
        document["channels"]!.AsObject().Count.ShouldBe(1);
        document["channels"]!["/v1/state"].ShouldNotBeNull();
        document["channels"]!["/v2/state"].ShouldBeNull();
        var operations = document["operations"]!.AsObject();
        foreach (var operation in operations.Select(operation => operation.Value!))
        {
            operation["channel"]!["$ref"]!.GetValue<string>().ShouldBe("#/channels/~1v1~1state");
            if (operation["reply"] is { } completion)
            {
                JsonNode.DeepEquals(operation["channel"], completion["channel"]).ShouldBeTrue();
            }
        }

        var read = operations.Single(operation => operation.Key.Contains("hub.read-state@", StringComparison.Ordinal)).Value!;
        read["summary"]!.GetValue<string>().ShouldBe("Reads current state.");
        read["bindings"]!["x-signalr"]!["authorizationRequired"]!.GetValue<bool>().ShouldBeTrue();
        read["reply"].ShouldNotBeNull();
        var reply = document["components"]!["messages"]!.AsObject().Single(message => message.Value!["name"]!.GetValue<string>().EndsWith("hub.read-state@/v1/state.result", StringComparison.Ordinal)).Value!;
        reply["title"]!.GetValue<string>().ShouldBe("Read response");
        reply["payload"]!["schema"]!["oneOf"]![0]!["properties"]!["result"]!["properties"]!["state"].ShouldNotBeNull();
        reply["payload"]!["schema"]!["oneOf"]![1]!["properties"]!["error"]!["type"]!.GetValue<string>().ShouldBe("string");
        operations.Any(operation => operation.Key.Contains("Hidden", StringComparison.Ordinal)).ShouldBeFalse();
        operations.Any(operation => operation.Key.Contains("OnConnected", StringComparison.Ordinal)).ShouldBeFalse();
        operations.Single(operation => operation.Key.Contains("client.Changed@", StringComparison.Ordinal)).Value!["action"]!.GetValue<string>().ShouldBe("send");
        operations.Single(operation => operation.Key.Contains("hub.Watch@", StringComparison.Ordinal)).Value!["bindings"]!["x-signalr"]!["streamResult"]!.GetValue<bool>().ShouldBeTrue();
        operations.Single(operation => operation.Key.Contains(".upload.updates", StringComparison.Ordinal)).Value!["action"]!.GetValue<string>().ShouldBe("receive");
        var message = document["components"]!["messages"]!.AsObject().Single(message => message.Value!["name"]!.GetValue<string>().EndsWith("hub.read-state@/v1/state", StringComparison.Ordinal)).Value!;
        message["title"]!.GetValue<string>().ShouldBe("Read request");
        var streamed = document["components"]!["messages"]!.AsObject()
            .Single(message => message.Value!["name"]!.GetValue<string>().EndsWith("hub.Watch@/v1/state.result", StringComparison.Ordinal)).Value!;
        streamed["title"]!.GetValue<string>().ShouldBe("Watch stream");
        var uploaded = document["components"]!["messages"]!.AsObject()
            .Single(message => message.Value!["name"]!.GetValue<string>().EndsWith(".upload.updates", StringComparison.Ordinal)).Value!;
        uploaded["title"]!.GetValue<string>().ShouldBe("Upload / updates stream");
        message["payload"]!["schema"]!["properties"]!["arguments"]!["minItems"]!.GetValue<int>().ShouldBe(1);
        var bulk = document["components"]!["messages"]!.AsObject().Single(message => message.Value!["name"]!.GetValue<string>().EndsWith("hub.Bulk@/v1/state", StringComparison.Ordinal)).Value!;
        bulk["payload"]!["schema"]!["properties"]!["arguments"]!["minItems"]!.GetValue<int>().ShouldBe(1);
        var uploadReply = document["components"]!["messages"]!.AsObject().Single(message => message.Value!["name"]!.GetValue<string>().EndsWith("hub.Upload@/v1/state.result", StringComparison.Ordinal)).Value!;
        uploadReply["payload"]!["schema"]!["oneOf"]![0]!["properties"]!["result"]!["type"]!.GetValue<string>().ShouldBe("null");
        operations.Single(operation => operation.Key.Contains("hub.WatchDerived@", StringComparison.Ordinal)).Value!["bindings"]!["x-signalr"]!["streamResult"]!.GetValue<bool>().ShouldBeTrue();
        var v2 = source.Generate("matchmaking-signalr-v2", TestContext.Current.CancellationToken);
        v2["info"]!["x-deprecated"]!.GetValue<bool>().ShouldBeTrue();
        var configured = v2["components"]!["messages"]!.AsObject().Single(message =>
            message.Value!["name"]!.GetValue<string>().EndsWith("hub.Read@/v2/state.result", StringComparison.Ordinal)).Value!;
        configured["payload"]!["schema"]!["oneOf"]![0]!["properties"]!["result"]!["properties"]!["state"].ShouldNotBeNull();
        await using var connection = new HubConnectionBuilder().WithUrl(address + "/v2/state").Build();
        await connection.StartAsync(TestContext.Current.CancellationToken);
        var result = await connection.InvokeAsync<DocumentationPayload>(nameof(DocumentationV2Hub.Read), TestContext.Current.CancellationToken);
        result.State.ShouldBe("ready");
        await connection.StopAsync(TestContext.Current.CancellationToken);
        Should.Throw<ArgumentException>(() => source.Generate("missing"));
        Should.Throw<ArgumentException>(() => source.Generate(""));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        Should.Throw<OperationCanceledException>(() => source.Generate("matchmaking-signalr-v1", cancelled.Token));
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    [Theory(DisplayName = "Fallback authorization applies to hub documentation unless the endpoint allows anonymous access")]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task HonorsFallbackAuthorization(bool fallback, bool anonymous, bool expected)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSignalR(builder.Configuration);
        builder.Services.AddAuthorizationBuilder().SetFallbackPolicy(
            fallback ? new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build() : null);
        builder.Services.AddApiDocumentationSchemas(DocumentationJsonContext.Default);
        await using var app = builder.Build();
        app.UseSignalR(hubs =>
        {
            var endpoint = hubs.MapHub<DocumentationV2Hub>("/fallback");
            if (anonymous)
            {
                endpoint.AllowAnonymous();
            }
        });

        var source = app.Services.GetServices<IApiDocumentSource>().OfType<IAsyncApiDocumentSource>().Single();
        var document = source.Generate("matchmaking-signalr-v2", TestContext.Current.CancellationToken);
        var operations = document["operations"]!.AsObject().Select(operation => operation.Value!).ToArray();
        operations.ShouldNotBeEmpty();
        operations.ShouldAllBe(operation => operation["bindings"]!["x-signalr"]!["authorizationRequired"]!.GetValue<bool>() == expected);
    }

    [Fact(DisplayName = "Undocumented hubs remain usable without requiring JSON schema metadata")]
    public async Task LeavesUndocumentedHubsUnchanged()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSignalR(builder.Configuration);
        await using var app = builder.Build();
        app.UseSignalR(hubs => hubs.MapHub<PlainHub>("/plain"));
        app.Services.GetRequiredService<ApiDocumentCatalog>().GetDocuments().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Generated metadata validates required arguments and keeps caller arrays immutable")]
    public void ValidatesMetadata()
    {
        Should.Throw<ArgumentException>(() => new HubParameterDescription("", typeof(string)));
        Should.Throw<ArgumentNullException>(() => new HubParameterDescription("value", null!));
        Should.Throw<ArgumentException>(() => new HubMethodDescription("", []));
        Should.Throw<ArgumentNullException>(() => new HubMethodDescription("Read", null!));
        Should.Throw<ArgumentException>(() => new HubMethodDescription("Read", [null!]));
        HubParameterDescription[] parameters = [new("value", typeof(string))];
        var method = new HubMethodDescription("Read", parameters);
        parameters[0] = new HubParameterDescription("changed", typeof(int));
        method.Parameters[0].Name.ShouldBe("value");
        Should.Throw<ArgumentNullException>(() => HubDocumentationRegistry.RegisterHub<PlainHub>(null!));
        var descriptor = new ApiDocumentDescriptor("plain", "Plain", "plain", "1", ApiDocumentFormat.AsyncApi);
        Should.Throw<ArgumentNullException>(() => HubDocumentationRegistry.RegisterHub<PlainHub>(descriptor, null!));
        Should.Throw<ArgumentException>(() => HubDocumentationRegistry.RegisterHub<PlainHub>(descriptor, [null!]));
        Should.Throw<ArgumentException>(() => HubDocumentationRegistry.RegisterHub<PlainHub>(
            new ApiDocumentDescriptor("http", "HTTP", "plain", "1", ApiDocumentFormat.OpenApi)));
    }

    [Theory(DisplayName = "SignalR documents reject conflicting module version title or deprecation metadata")]
    [InlineData("Module", "1.0", "Title", false)]
    [InlineData("module", "1", "Title", false)]
    [InlineData("module", "1.0", "Other", false)]
    [InlineData("module", "1.0", "Title", true)]
    public async Task RejectsConflictingMetadata(string module, string version, string title, bool deprecated)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSignalR(builder.Configuration);
        await using var app = builder.Build();
        var mappings = app.Services.GetRequiredService<MappedHubDocumentation>();
        var first = new ApiDocumentDescriptor("module-signalr-v1", "Title", "module", "1.0", ApiDocumentFormat.AsyncApi);
        var second = new ApiDocumentDescriptor("module-signalr-v1", title, module, version, ApiDocumentFormat.AsyncApi, deprecated: deprecated);
        mappings.Hubs.Add(new(typeof(PlainHub), "/first", new HubDescription(first, [], _ => new HubOptions(), new Dictionary<string, ApiOperationMetadata>()), new()));
        mappings.Hubs.Add(new(typeof(PlainHub), "/second", new HubDescription(second, [], _ => new HubOptions(), new Dictionary<string, ApiOperationMetadata>()), new()));
        Should.Throw<InvalidOperationException>(() => app.Services.GetRequiredService<ApiDocumentCatalog>().GetDocuments());
    }

    [Theory(DisplayName = "SignalR documentation follows hub protocol and implicit service options")]
    [InlineData(null, true)]
    [InlineData("json", false)]
    public async Task HonorsHubOptions(string? protocol, bool disableImplicitServices)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSignalR(builder.Configuration).AddHubOptions<DocumentationHub>(options =>
        {
            options.SupportedProtocols = protocol is null ? null : [protocol];
            options.DisableImplicitFromServicesParameters = disableImplicitServices;
        });
        builder.Services.AddSingleton<DocumentationDependency>();
        builder.Services.AddApiDocumentationSchemas(DocumentationJsonContext.Default);
        await using var app = builder.Build();
        app.UseSignalR(hubs => hubs.MapHub<DocumentationHub>("/options").AllowAnonymous());
        var source = app.Services.GetServices<IApiDocumentSource>().OfType<IAsyncApiDocumentSource>().Single();
        var document = source.Generate("matchmaking-signalr-v1", TestContext.Current.CancellationToken);
        var read = document["operations"]!.AsObject().Single(operation => operation.Key.Contains("hub.read-state@", StringComparison.Ordinal)).Value!;
        read["bindings"]!["x-signalr"]!["protocols"]!.AsArray().Count.ShouldBe(protocol is null ? 2 : 1);
        read["bindings"]!["x-signalr"]!["authorizationRequired"]!.GetValue<bool>().ShouldBeFalse();
        var message = document["components"]!["messages"]!.AsObject().Single(message => message.Value!["name"]!.GetValue<string>().EndsWith("hub.read-state@/options", StringComparison.Ordinal)).Value!;
        message["payload"]!["schema"]!["properties"]!["arguments"]!["minItems"]!.GetValue<int>().ShouldBe(disableImplicitServices ? 2 : 1);
    }

    [Fact(DisplayName = "Documentation works with service providers that do not expose service introspection")]
    public async Task SupportsNonIntrospectingProviders()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSignalR(builder.Configuration);
        await using var app = builder.Build();
        var descriptor = new ApiDocumentDescriptor("module-signalr-v1", "Module", "module", "1.0", ApiDocumentFormat.AsyncApi);
        var mappings = new MappedHubDocumentation();
        mappings.Hubs.Add(new(typeof(PlainHub), "/plain", new HubDescription(descriptor,
            [new HubMethodDescription("Read", [new HubParameterDescription("id", typeof(Guid))])], _ => new HubOptions(), new Dictionary<string, ApiOperationMetadata>()), new()));
        var source = new SignalRDocumentSource(mappings, new ApiSchemaRegistry([DocumentationJsonContext.Default]),
            new NonIntrospectingProvider(app.Services));
        source.Generate(descriptor.Name, TestContext.Current.CancellationToken)["operations"]!.AsObject().Count.ShouldBe(1);
    }

    [Theory(DisplayName = "Documented transports and protocols match actual SignalR negotiation")]
    [InlineData(HttpTransportType.WebSockets, 2)]
    [InlineData(HttpTransportType.ServerSentEvents, 1)]
    [InlineData(HttpTransportType.LongPolling, 2)]
    public async Task HonorsMappedTransports(HttpTransportType transport, int protocolCount)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSignalR(builder.Configuration);
        builder.Services.AddApiDocumentationSchemas(DocumentationJsonContext.Default);
        await using var app = builder.Build();
        app.UseSignalR(hubs => hubs.MapHub<DocumentationV2Hub>("/limited", options => options.Transports = transport));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var source = app.Services.GetServices<IApiDocumentSource>().OfType<IAsyncApiDocumentSource>().Single();
        var document = source.Generate("matchmaking-signalr-v2", TestContext.Current.CancellationToken);
        var binding = document["operations"]!.AsObject().Single().Value!["bindings"]!["x-signalr"]!;
        binding["transports"]!.AsArray().Select(node => node!.GetValue<string>()).ShouldBe([transport.ToString()]);
        binding["protocols"]!.AsArray().Count.ShouldBe(protocolCount);
        binding["transportProtocol"]!.GetValue<string>().ShouldBe(transport == HttpTransportType.WebSockets ? "ws" : "http");
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        using var response = await client.PostAsync("/limited/negotiate?negotiateVersion=1", null, TestContext.Current.CancellationToken);
        var negotiation = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        negotiation["availableTransports"]![0]!["transport"]!.GetValue<string>().ShouldBe(transport.ToString());
        negotiation["availableTransports"]!.AsArray().Count.ShouldBe(1);
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    private sealed class NonIntrospectingProvider(IServiceProvider services) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IServiceProviderIsService) ? null : services.GetService(serviceType);
    }
    public sealed class PlainHub : Hub;
}

#pragma warning disable CA1822, IDE0060 // Keep instance endpoints and the signatures used by the documentation tests.
public sealed class DocumentationHub : Hub<IDocumentationClient>, IDocumentedHubEndpoint
{
    public static string Name => "matchmaking";

    public static string Summary => "Matchmaking";

    public static ApiVersion ApiVersion => new(1, 0);

    public static HubEndpointConventionBuilder Map(HubMapBuilder hubs) => hubs.MapHub<DocumentationHub>("/v1/state").RequireAuthorization();

    /// <summary>Reads current state.</summary>
    [HubMethodName("read-state")]
    public Task<DocumentationPayload> Read([Microsoft.AspNetCore.Mvc.FromQuery] Guid id, DocumentationDependency dependency) =>
        Task.FromResult(new DocumentationPayload(id, "searching"));

    public ChannelReader<DocumentationPayload> Watch(CancellationToken cancellationToken) => Channel.CreateUnbounded<DocumentationPayload>().Reader;

    public Task Upload(IAsyncEnumerable<DocumentationPayload> updates) => Task.CompletedTask;

    public Task<DerivedDocumentationReader> WatchDerived() => Task.FromResult<DerivedDocumentationReader>(null!);

    public void Bulk(IEnumerable<DocumentationPayload> values) { }

    private string Hidden() => "hidden";
}

public sealed class DocumentationV2Hub : Hub, IDocumentedHubEndpoint
{
    public static string Name => "matchmaking";

    public static string Summary => "Matchmaking";

    public static ApiVersion ApiVersion => new(2, 0);

    public static bool IsDeprecated => true;

    public static HubEndpointConventionBuilder Map(HubMapBuilder hubs) => hubs.MapHub<DocumentationV2Hub>("/v2/state");

    public ConfiguredTaskAwaitable<DocumentationPayload> Read() =>
        Task.FromResult(new DocumentationPayload(Guid.Empty, "ready")).ConfigureAwait(false);
}

#pragma warning restore CA1822, IDE0060

public interface IDocumentationClient
{
    Task Changed(DocumentationPayload state, CancellationToken cancellationToken);
}

public abstract class DerivedDocumentationReader : ChannelReader<DocumentationPayload>;

public sealed class DocumentationDependency;
public sealed record DocumentationPayload(Guid Id, string State);

[JsonSerializable(typeof(DocumentationDependency))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(DocumentationPayload))]
[JsonSerializable(typeof(IEnumerable<DocumentationPayload>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class DocumentationJsonContext : JsonSerializerContext;
