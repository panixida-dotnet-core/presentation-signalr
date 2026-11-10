using System.Diagnostics.CodeAnalysis;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

using PANiXiDA.Core.Presentation.SignalR.Documentation;

namespace PANiXiDA.Core.Presentation.SignalR.Hubs;

/// <summary>Maps hubs and records their transport paths for access token forwarding.</summary>
public sealed class HubMapBuilder
{
    private readonly IEndpointRouteBuilder _endpoints;
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);

    internal HubMapBuilder(IEndpointRouteBuilder endpoints)
    {
        _endpoints = endpoints;
    }

    internal IReadOnlyCollection<string> Paths => _paths;

    /// <summary>Maps a hub at a unique literal path and enables query token forwarding on that path.</summary>
    /// <typeparam name="THub">The application hub type.</typeparam>
    /// <param name="path">The absolute hub path without PathBase, query strings or route parameters.</param>
    /// <param name="configureOptions">Optional standard SignalR transport and connection settings.</param>
    /// <returns>The standard hub convention builder for authorization and endpoint metadata.</returns>
    /// <exception cref="ArgumentException">The path is invalid or has already been mapped by this builder.</exception>
    public HubEndpointConventionBuilder MapHub<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicMethods)] THub>(
        [StringSyntax("Route")] string path,
        Action<HttpConnectionDispatcherOptions>? configureOptions = null)
        where THub : Hub
    {
        if (!IsValidHubPath(path))
        {
            throw new ArgumentException("Hub paths must be literal absolute paths without whitespace, query strings or route parameters.", nameof(path));
        }

        var normalizedPath = path.TrimEnd('/');
        if (!_paths.Add(normalizedPath))
        {
            throw new ArgumentException("Hub paths must be unique.", nameof(path));
        }

        HttpConnectionDispatcherOptions? connectionOptions = null;
        var mapped = _endpoints.MapHub<THub>(normalizedPath, options =>
        {
            configureOptions?.Invoke(options);
            connectionOptions = options;
        });
        _endpoints.ServiceProvider.GetService<MappedHubDocumentation>()?.Add(typeof(THub), normalizedPath, connectionOptions!);
        return mapped;
    }

    private static bool IsValidHubPath(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) && path.StartsWith('/')
            && !path.StartsWith("//", StringComparison.Ordinal) && path.TrimEnd('/').Length > 0
            && !path.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))
            && path.IndexOfAny(['?', '#', '{', '}', '*', '\\']) < 0;
    }
}
