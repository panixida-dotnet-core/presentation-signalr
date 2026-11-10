using System.Text.Json.Serialization;
using Asp.Versioning;
using Microsoft.AspNetCore.SignalR;
using PANiXiDA.Core.ApiDocumentation;
using PANiXiDA.Core.Presentation.SignalR.Hubs;

namespace PANiXiDA.Core.Presentation.SignalR.AotSmoke;

/// <summary>A documented AOT smoke hub.</summary>
public sealed class SmokeHub : Hub, IDocumentedHubEndpoint
{
    /// <summary>Gets the document module name.</summary>
    public static string Name => "smoke";

    /// <summary>Gets the document title.</summary>
    public static string Summary => "Smoke";

    /// <summary>Gets the contract version.</summary>
    public static ApiVersion ApiVersion => new(1, 0);

    /// <summary>Gets operation metadata used by the local AOT check.</summary>
    public static IReadOnlyDictionary<string, ApiOperationMetadata> Operations { get; } =
        new Dictionary<string, ApiOperationMetadata> { [nameof(Echo)] = new("ContractEcho", "Contract summary") };

    /// <summary>Maps the smoke hub.</summary>
    /// <param name="hubs">The hub builder.</param>
    /// <returns>The endpoint conventions.</returns>
    public static HubEndpointConventionBuilder Map(HubMapBuilder hubs) => hubs.MapHub<SmokeHub>("/hub")
        .WithOperation(nameof(Echo), name: "Echo payload");

    /// <summary>Returns the supplied payload.</summary>
    /// <param name="payload">The payload.</param>
    /// <returns>The same payload.</returns>
    [Microsoft.AspNetCore.Routing.EndpointName("AttributeEcho")]
    [Microsoft.AspNetCore.Http.EndpointSummary("Attribute summary")]
    public SmokePayload Echo(SmokePayload payload) => payload;
}

/// <summary>A documented payload.</summary>
/// <param name="Id">The payload identifier.</param>
public sealed record SmokePayload(Guid Id);

[JsonSerializable(typeof(SmokePayload))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class SmokeJsonContext : JsonSerializerContext;
