using Microsoft.AspNetCore.Builder;

namespace PANiXiDA.Core.Presentation.SignalR.Hubs;

/// <summary>Configures documentation for a particular mapped hub endpoint.</summary>
public static class HubEndpointConventionBuilderExtensions
{
    /// <summary>Describes bearer authentication in AsyncAPI for a hub secured by a bearer policy.</summary>
    /// <param name="builder">The mapped hub conventions.</param>
    /// <returns>The original convention builder.</returns>
    /// <remarks>Configure the authentication handler and authorization policy separately.</remarks>
    public static HubEndpointConventionBuilder WithBearerDocumentation(this HubEndpointConventionBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Add(endpoint => endpoint.Metadata.Add(BearerDocumentation.Instance));
        return builder;
    }

    /// <summary>Overrides a hub method's display name or summary without changing its SignalR target.</summary>
    /// <param name="builder">The mapped hub conventions.</param>
    /// <param name="methodName">The CLR method name, normally supplied through <c>nameof</c>.</param>
    /// <param name="name">An optional nonempty display name.</param>
    /// <param name="summary">An optional summary. An empty string clears the description.</param>
    /// <returns>The original convention builder.</returns>
    public static HubEndpointConventionBuilder WithOperation(this HubEndpointConventionBuilder builder,
        string methodName, string? name = null, string? summary = null)
    {
        return AddOperation(builder, methodName, name, summary, false);
    }

    /// <summary>Overrides a typed client callback's documentation for this mapped hub.</summary>
    /// <param name="builder">The mapped hub conventions.</param>
    /// <param name="methodName">The callback's CLR method name.</param>
    /// <param name="name">An optional nonempty display name.</param>
    /// <param name="summary">An optional summary. An empty string clears the description.</param>
    /// <returns>The original convention builder.</returns>
    public static HubEndpointConventionBuilder WithClientOperation(this HubEndpointConventionBuilder builder,
        string methodName, string? name = null, string? summary = null)
    {
        return AddOperation(builder, methodName, name, summary, true);
    }

    private static HubEndpointConventionBuilder AddOperation(HubEndpointConventionBuilder builder,
        string methodName, string? name, string? summary, bool client)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        if (name is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
        }

        if (name is null && summary is null)
        {
            throw new ArgumentException("Specify an operation name or summary.");
        }

        builder.Add(endpoint => endpoint.Metadata.Add(new HubOperationOverride(methodName, name, summary, client)));
        return builder;
    }
}

internal sealed record HubOperationOverride(string MemberName, string? Name, string? Summary, bool IsClientMethod);

internal sealed class BearerDocumentation
{
    internal static BearerDocumentation Instance { get; } = new();

    private BearerDocumentation()
    {
    }
}
