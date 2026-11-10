using System.Text.Json.Nodes;

using Asp.Versioning;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using PANiXiDA.Core.ApiDocumentation;
using PANiXiDA.Core.ApiDocumentation.DependencyInjection;
using PANiXiDA.Core.Presentation.SignalR.DependencyInjection;
using PANiXiDA.Core.Presentation.SignalR.Documentation;
using PANiXiDA.Core.Presentation.SignalR.Hubs;

namespace PANiXiDA.Core.Presentation.SignalR.UnitTests.Documentation;

public sealed class OperationMetadataTests
{
    [Fact(DisplayName = "Hub contracts merge Operations attributes XML summaries and automatic method names without changing wire targets")]
    public async Task ResolvesContractAndAttributeMetadata()
    {
        await using var app = CreateApplication();
        app.UseSignalR(hubs => hubs.MapHub<MetadataHub>("/metadata"));

        var document = Generate(app);

        document["info"]!["title"]!.GetValue<string>().ShouldBe("Metadata SignalR v1.0");
        var read = Operation(document, "/metadata", "wire-read");
        read["title"]!.GetValue<string>().ShouldBe("ContractRead");
        read["summary"]!.GetValue<string>().ShouldBe("Attribute summary");
        read["bindings"]!["x-signalr"]!["target"]!.GetValue<string>().ShouldBe("wire-read");
        Message(document, read)["title"]!.GetValue<string>().ShouldBe("ContractRead request");
        Message(document, read, reply: true)["title"]!.GetValue<string>().ShouldBe("ContractRead response");
        Operation(document, "/metadata", nameof(MetadataHub.OnlyContract))["summary"]!.GetValue<string>().ShouldBe("Contract summary");
        Operation(document, "/metadata", nameof(MetadataHub.ClearSummary))["summary"]!.GetValue<string>().ShouldBeEmpty();
        Operation(document, "/metadata", nameof(MetadataHub.OnlyAttributes))["title"]!.GetValue<string>().ShouldBe("AttributeOnly");
        Operation(document, "/metadata", nameof(MetadataHub.OnlySummary))["title"]!.GetValue<string>().ShouldBe(nameof(MetadataHub.OnlySummary));
        Operation(document, "/metadata", nameof(MetadataHub.OnlySummary))["summary"]!.GetValue<string>().ShouldBe("Summary only");
        Operation(document, "/metadata", nameof(MetadataHub.XmlOnly))["summary"]!.GetValue<string>().ShouldBe("XML fallback");
        Operation(document, "/metadata", nameof(MetadataHub.Defaults))["title"]!.GetValue<string>().ShouldBe(nameof(MetadataHub.Defaults));
        Operation(document, "/metadata", nameof(MetadataHub.Defaults))["summary"].ShouldBeNull();
        Operation(document, "/metadata", nameof(IMetadataClient.Changed), true)["title"]!.GetValue<string>().ShouldBe("ContractNotification");
        Message(document, Operation(document, "/metadata", nameof(IMetadataClient.Changed), true))["title"]!
            .GetValue<string>().ShouldBe("ContractNotification notification");
        var confirmation = Operation(document, "/metadata", nameof(IMetadataClient.Confirm), true);
        Message(document, confirmation)["title"]!.GetValue<string>().ShouldBe("Confirm request");
        Message(document, confirmation, reply: true)["title"]!.GetValue<string>().ShouldBe("Confirm response");
        var dispose = Operation(document, "/metadata", nameof(IMetadataClient.Dispose), true);
        dispose["title"]!.GetValue<string>().ShouldBe("Dispose resources");
        Message(document, dispose)["title"]!.GetValue<string>().ShouldBe("Dispose resources notification");
    }

    [Fact(DisplayName = "Multiple methods and hub types share one channel per mapped path and remain isolated across modules")]
    public async Task GroupsMultipleHubsAndMethods()
    {
        await using var app = CreateApplication();
        app.UseSignalR(hubs =>
        {
            hubs.MapHub<MetadataHub>("/metadata");
            hubs.MapHub<SecondaryMetadataHub>("/secondary");
            hubs.MapHub<DocumentationV2Hub>("/other");
        });
        var source = app.Services.GetServices<IApiDocumentSource>().OfType<IAsyncApiDocumentSource>().Single();

        var document = source.Generate("metadata-signalr-v1", TestContext.Current.CancellationToken);

        source.GetDocuments().Select(item => item.Name).Order().ShouldBe(["matchmaking-signalr-v2", "metadata-signalr-v1"]);
        document["channels"]!.AsObject().Select(channel => channel.Key).Order().ShouldBe(["/metadata", "/secondary"]);
        document["operations"]!.AsObject().Count.ShouldBe(11);
        document["operations"]!.AsObject().Where(operation => operation.Key.EndsWith("@/metadata", StringComparison.Ordinal))
            .Select(operation => operation.Value!["bindings"]!["x-signalr"]!["target"]!.GetValue<string>()).Order()
            .ShouldBe(new[] { "Changed", "ClearSummary", "Confirm", "Defaults", "Dispose", "OnlyAttributes", "OnlyContract", "OnlySummary", "wire-read", "XmlOnly" }.Order());
        foreach (var entry in document["operations"]!.AsObject())
        {
            var operation = entry.Value!;
            var request = Message(document, operation);
            request["name"]!.GetValue<string>().ShouldBe(entry.Key);
            request["payload"]!["schema"]!["properties"]!["target"]!["const"]!.GetValue<string>()
                .ShouldBe(operation["bindings"]!["x-signalr"]!["target"]!.GetValue<string>());
            if (operation["reply"] is { } reply)
            {
                JsonNode.DeepEquals(operation["channel"], reply["channel"]).ShouldBeTrue();
                Message(document, operation, reply: true)["name"]!.GetValue<string>().ShouldBe(entry.Key + ".result");
            }
        }

        var other = source.Generate("matchmaking-signalr-v2", TestContext.Current.CancellationToken);
        other["channels"]!.AsObject().Select(channel => channel.Key).ShouldBe(["/other"]);
        document["operations"]!.AsObject().Any(operation => operation.Key.EndsWith("@/other", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Fact(DisplayName = "Changing display names only changes operation and message titles while preserving the complete transport contract")]
    public async Task RenamesOnlyDocumentationTitles()
    {
        await using var original = CreateApplication();
        original.UseSignalR(hubs => hubs.MapHub<MetadataHub>("/metadata"));
        var before = Generate(original);
        await using var renamed = CreateApplication();
        renamed.UseSignalR(hubs => hubs.MapHub<MetadataHub>("/metadata")
            .WithOperation(nameof(MetadataHub.Read), name: "Read current state"));

        var after = Generate(renamed);

        var operation = Operation(after, "/metadata", "wire-read");
        Message(after, operation)["title"]!.GetValue<string>().ShouldBe("Read current state request");
        Message(after, operation, reply: true)["title"]!.GetValue<string>().ShouldBe("Read current state response");
        foreach (var document in new[] { before, after })
        {
            foreach (var item in document["operations"]!.AsObject().Concat(document["components"]!["messages"]!.AsObject()))
            {
                item.Value!.AsObject().Remove("title");
            }
        }

        JsonNode.DeepEquals(before, after).ShouldBeTrue();
    }

    [Fact(DisplayName = "Map overrides are ordered per field and isolated to each mapped path including typed callbacks")]
    public async Task IsolatesOrderedMapOverrides()
    {
        await using var app = CreateApplication();
        app.UseSignalR(hubs =>
        {
            hubs.MapHub<MetadataHub>("/contract");
            hubs.MapHub<MetadataHub>("/custom")
                .WithOperation(nameof(MetadataHub.Read), summary: "Builder summary")
                .WithOperation(nameof(MetadataHub.Read), name: "BuilderRead")
                .WithOperation(nameof(MetadataHub.Read), name: "FinalRead")
                .WithClientOperation(nameof(IMetadataClient.Changed), name: "BuilderNotification")
                .WithClientOperation(nameof(IMetadataClient.Dispose), name: "BuilderDispose");
            hubs.MapHub<MetadataHub>("/clear").WithOperation(nameof(MetadataHub.Read), summary: "");
        });
        var beforeStart = Generate(app);
        await app.StartAsync(TestContext.Current.CancellationToken);

        var document = Generate(app);
        document.ToJsonString().ShouldBe(beforeStart.ToJsonString());

        Operation(document, "/contract", "wire-read")["title"]!.GetValue<string>().ShouldBe("ContractRead");
        var custom = Operation(document, "/custom", "wire-read");
        custom["title"]!.GetValue<string>().ShouldBe("FinalRead");
        custom["summary"]!.GetValue<string>().ShouldBe("Builder summary");
        Operation(document, "/clear", "wire-read")["summary"]!.GetValue<string>().ShouldBeEmpty();
        var callback = Operation(document, "/custom", nameof(IMetadataClient.Changed), true);
        callback["title"]!.GetValue<string>().ShouldBe("BuilderNotification");
        callback["summary"]!.GetValue<string>().ShouldBe("Contract callback summary");
        callback["action"]!.GetValue<string>().ShouldBe("send");
        Operation(document, "/custom", nameof(IMetadataClient.Dispose), true)["title"]!.GetValue<string>().ShouldBe("BuilderDispose");
    }

    [Theory(DisplayName = "Unknown method or callback overrides fail explicitly")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsUnknownOverrides(bool client)
    {
        await using var app = CreateApplication();
        app.UseSignalR(hubs =>
        {
            var endpoint = hubs.MapHub<MetadataHub>("/invalid");
            if (client)
            {
                endpoint.WithClientOperation("Missing", name: "Missing");
            }
            else
            {
                endpoint.WithOperation("Missing", name: "Missing");
            }
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        Should.Throw<InvalidOperationException>(() => Generate(app)).Message.ShouldContain("Missing");
    }

    [Fact(DisplayName = "Operation builders and generated member names validate public arguments")]
    public async Task ValidatesBuilderArguments()
    {
        await using var app = CreateApplication();
        app.UseSignalR(hubs =>
        {
            var endpoint = hubs.MapHub<MetadataHub>("/guards");
            Should.Throw<ArgumentNullException>(() => ((HubEndpointConventionBuilder)null!).WithOperation("Read", name: "Read"));
            Should.Throw<ArgumentException>(() => endpoint.WithOperation("", name: "Read"));
            Should.Throw<ArgumentException>(() => endpoint.WithOperation("Read", name: " "));
            Should.Throw<ArgumentException>(() => endpoint.WithOperation("Read"));
        });
        Should.Throw<ArgumentException>(() => new HubMethodDescription("Read", []) { MemberName = "" });
    }

    [Fact(DisplayName = "Static hub contracts reject invalid metadata and snapshot operation dictionaries")]
    public void ValidatesAndSnapshotsStaticContracts()
    {
        HubMethodDescription[] methods =
        [
            new("wire-read", []) { MemberName = "Read" },
            new("Changed", [], isClientMethod: true)
        ];
        ContractGuardHub.Name = "";
        Should.Throw<ArgumentException>(() => HubDocumentationRegistry.RegisterHub<ContractGuardHub>(methods));
        ContractGuardHub.Name = "guard";
        ContractGuardHub.Summary = "";
        Should.Throw<ArgumentException>(() => HubDocumentationRegistry.RegisterHub<ContractGuardHub>(methods));
        ContractGuardHub.Summary = "Guard";
        ContractGuardHub.ApiVersion = null!;
        Should.Throw<ArgumentNullException>(() => HubDocumentationRegistry.RegisterHub<ContractGuardHub>(methods));
        ContractGuardHub.ApiVersion = ApiVersion.Neutral;
        Should.Throw<ArgumentException>(() => HubDocumentationRegistry.RegisterHub<ContractGuardHub>(methods));
        ContractGuardHub.ApiVersion = new ApiVersion(1, 0);
        ContractGuardHub.Operations = null!;
        Should.Throw<ArgumentNullException>(() => HubDocumentationRegistry.RegisterHub<ContractGuardHub>(methods));
        ContractGuardHub.Operations = new Dictionary<string, ApiOperationMetadata> { ["Missing"] = new("Missing") };
        Should.Throw<ArgumentException>(() => HubDocumentationRegistry.RegisterHub<ContractGuardHub>(methods));
        ContractGuardHub.Operations = new Dictionary<string, ApiOperationMetadata> { ["Read"] = null! };
        Should.Throw<ArgumentException>(() => HubDocumentationRegistry.RegisterHub<ContractGuardHub>(methods));
        var operations = new Dictionary<string, ApiOperationMetadata>
        {
            ["Read"] = new("Original"),
            ["client.Changed"] = new("Original callback")
        };
        ContractGuardHub.Operations = operations;

        HubDocumentationRegistry.RegisterHub<ContractGuardHub>(methods);
        operations["Read"] = new ApiOperationMetadata("Changed");
        operations["client.Changed"] = new ApiOperationMetadata("Changed callback");

        HubDocumentationRegistry.Find(typeof(ContractGuardHub))!.Operations["Read"].Name.ShouldBe("Original");
        HubDocumentationRegistry.Find(typeof(ContractGuardHub))!.Operations["client.Changed"].Name.ShouldBe("Original callback");
        HubDocumentationRegistry.RegisterHub<LegacyGuardHub>(new ApiDocumentDescriptor(
            "legacy", "Legacy", "legacy", "1.0", ApiDocumentFormat.AsyncApi));
        HubDocumentationRegistry.Find(typeof(LegacyGuardHub))!.Operations.ShouldBeEmpty();
    }

    private static WebApplication CreateApplication()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSignalR(builder.Configuration);
        builder.Services.AddApiDocumentationSchemas(DocumentationJsonContext.Default);
        return builder.Build();
    }

    private static JsonObject Generate(WebApplication app) => app.Services.GetServices<IApiDocumentSource>()
        .OfType<IAsyncApiDocumentSource>().Single().Generate("metadata-signalr-v1", TestContext.Current.CancellationToken);

    private static JsonNode Operation(JsonObject document, string path, string target, bool client = false) =>
        document["operations"]!.AsObject().Single(operation =>
            operation.Key.EndsWith("@" + path, StringComparison.Ordinal) &&
            operation.Value!["bindings"]!["x-signalr"]!["target"]!.GetValue<string>() == target &&
            operation.Value["action"]!.GetValue<string>() == (client ? "send" : "receive")).Value!;

    private static JsonNode Message(JsonObject document, JsonNode operation, bool reply = false)
    {
        var reference = (reply ? operation["reply"] : operation)!["messages"]![0]!["$ref"]!.GetValue<string>();
        return document["components"]!["messages"]![reference.Split('/')[^1]]!;
    }

    public abstract class LegacyGuardHub : Hub;
}

public sealed class MetadataHub : Hub<IMetadataClient>, IDocumentedHubEndpoint
{
    public static string Name => "metadata";

    public static string Summary => "Metadata";

    public static ApiVersion ApiVersion => new(1, 0);

    public static IReadOnlyDictionary<string, ApiOperationMetadata> Operations { get; } = new Dictionary<string, ApiOperationMetadata>
    {
        [nameof(Read)] = new("ContractRead"),
        [nameof(OnlyContract)] = new("ContractOnly", "Contract summary"),
        [nameof(ClearSummary)] = new("Cleared", ""),
        ["client." + nameof(IMetadataClient.Changed)] = new("ContractNotification", "Contract callback summary"),
        ["client." + nameof(IMetadataClient.Dispose)] = new("Dispose resources")
    };

    public static HubEndpointConventionBuilder Map(HubMapBuilder hubs) => hubs.MapHub<MetadataHub>("/metadata");

    /// <summary>XML read summary</summary>
    [HubMethodName("wire-read")]
    [EndpointName("AttributeRead")]
    [EndpointSummary("Attribute summary")]
    public string Read(Guid id) => id.ToString();

    public void OnlyContract() { }

    [EndpointSummary("Must be cleared")]
    public void ClearSummary() { }

    [EndpointName("AttributeOnly")]
    [EndpointSummary("Attribute only summary")]
    public void OnlyAttributes() { }

    [EndpointSummary("Summary only")]
    public void OnlySummary() { }

    /// <summary>XML fallback</summary>
    public void XmlOnly() { }

    public void Defaults() { }
}

public sealed class SecondaryMetadataHub : Hub, IDocumentedHubEndpoint
{
    public static string Name => "metadata";

    public static string Summary => "Metadata";

    public static ApiVersion ApiVersion => new(1, 0);

    public static HubEndpointConventionBuilder Map(HubMapBuilder hubs) => hubs.MapHub<SecondaryMetadataHub>("/secondary");

    public string GetState() => "Searching";
}

public interface IMetadataClient
{
    [EndpointName("AttributeNotification")]
    [EndpointSummary("Attribute callback summary")]
    Task Changed(string value);

    Task<string> Confirm();

    Task Dispose();
}

public abstract class ContractGuardHub : Hub, IDocumentedHubEndpoint
{
    public static string Name { get; set; } = "guard";

    public static string Summary { get; set; } = "Guard";

    public static ApiVersion ApiVersion { get; set; } = new(1, 0);

    public static IReadOnlyDictionary<string, ApiOperationMetadata> Operations { get; set; } = new Dictionary<string, ApiOperationMetadata>();

    public static HubEndpointConventionBuilder Map(HubMapBuilder hubs) => hubs.MapHub<ContractGuardHub>("/guard");
}
