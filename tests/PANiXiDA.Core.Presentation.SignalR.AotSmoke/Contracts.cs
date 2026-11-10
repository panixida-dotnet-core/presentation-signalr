using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using PANiXiDA.Core.ApiDocumentation;
using PANiXiDA.Core.Presentation.SignalR.Hubs;

namespace PANiXiDA.Core.Presentation.SignalR.AotSmoke;

/// <summary>A documented AOT smoke hub.</summary>
[ApiContract("smoke", "1.0")]
public sealed class SmokeHub : Hub, IHubEndpoint
{
    /// <summary>Maps the smoke hub.</summary>
    /// <param name="hubs">The hub builder.</param>
    /// <returns>The endpoint conventions.</returns>
    public static HubEndpointConventionBuilder Map(HubMapBuilder hubs) => hubs.MapHub<SmokeHub>("/hub");
    /// <summary>Returns the supplied payload.</summary>
    /// <param name="payload">The payload.</param>
    /// <returns>The same payload.</returns>
    public SmokePayload Echo(SmokePayload payload) => payload;
}

/// <summary>A documented payload.</summary>
/// <param name="Id">The payload identifier.</param>
public sealed record SmokePayload(Guid Id);

[JsonSerializable(typeof(SmokePayload))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class SmokeJsonContext : JsonSerializerContext;
