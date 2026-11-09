using Microsoft.AspNetCore.Builder;

namespace PANiXiDA.Core.Presentation.SignalR.Hubs;

/// <summary>Declares the static mapping used to discover an application hub at compile time.</summary>
public interface IHubEndpoint
{
    /// <summary>Maps the hub and configures its authorization, metadata and connection options.</summary>
    /// <param name="hubs">The hub mapping builder.</param>
    /// <returns>The convention builder for the mapped hub.</returns>
    static abstract HubEndpointConventionBuilder Map(HubMapBuilder hubs);
}
