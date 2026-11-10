using PANiXiDA.Core.ApiDocumentation;
using System.ComponentModel;
using System.Runtime.CompilerServices;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace PANiXiDA.Core.Presentation.SignalR.Documentation;

/// <summary>Registers statically generated documentation without reflection discovery.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class HubDocumentationRegistry
{
    private static readonly ConditionalWeakTable<Type, HubDescription> Registrations = [];

    /// <summary>Registers a documented hub. Intended for generated module initializers.</summary>
    /// <typeparam name="THub">The documented hub.</typeparam>
    /// <param name="document">The module and version descriptor.</param>
    /// <param name="methods">The generated method descriptions.</param>
    public static void RegisterHub<THub>(ApiDocumentDescriptor document, params HubMethodDescription[] methods)
        where THub : Hub
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(methods);
        if (document.Format != ApiDocumentFormat.AsyncApi || methods.Any(method => method is null))
        {
            throw new ArgumentException("An AsyncAPI descriptor and non-null methods are required.");
        }

        Registrations.Add(typeof(THub), new HubDescription(document, Array.AsReadOnly(methods.ToArray()),
            services => services.GetRequiredService<IOptions<HubOptions<THub>>>().Value));
    }

    internal static HubDescription? Find(Type hubType) => Registrations.TryGetValue(hubType, out var value) ? value : null;
}

internal sealed record HubDescription(ApiDocumentDescriptor Document, IReadOnlyList<HubMethodDescription> Methods,
    Func<IServiceProvider, HubOptions> Options);

internal sealed class MappedHubDocumentation
{
    internal List<(Type HubType, string Path, HubDescription Description)> Hubs { get; } = [];

    internal void Add(Type hubType, string path)
    {
        if (HubDocumentationRegistry.Find(hubType) is { } description)
        {
            Hubs.Add((hubType, path, description));
        }
    }
}
