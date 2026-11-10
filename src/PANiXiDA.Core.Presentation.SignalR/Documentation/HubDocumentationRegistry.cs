using PANiXiDA.Core.ApiDocumentation;
using System.ComponentModel;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;

using Asp.Versioning;

using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;

using PANiXiDA.Core.Presentation.SignalR.Hubs;

namespace PANiXiDA.Core.Presentation.SignalR.Documentation;

/// <summary>Registers statically generated documentation without reflection discovery.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class HubDocumentationRegistry
{
    private static readonly ConditionalWeakTable<Type, HubDescription> Registrations = [];

    /// <summary>Registers a hub using its static documentation contract and generated method metadata.</summary>
    /// <typeparam name="THub">The documented hub contract.</typeparam>
    /// <param name="methods">The generated method descriptions.</param>
    public static void RegisterHub<THub>(params HubMethodDescription[] methods)
        where THub : Hub, IDocumentedHubEndpoint
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(THub.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(THub.Summary);
        var version = THub.ApiVersion;
        ArgumentNullException.ThrowIfNull(version);
        if (version == ApiVersion.Neutral)
        {
            throw new ArgumentException("Documented hubs require a concrete API version.", nameof(THub.ApiVersion));
        }

        var text = version.ToString();
        var document = new ApiDocumentDescriptor(ApiDocumentNames.Create(THub.Name, "signalr", text),
            $"{THub.Summary} SignalR v{text}", THub.Name, text, ApiDocumentFormat.AsyncApi, deprecated: THub.IsDeprecated);
        RegisterCore<THub>(document, THub.Operations, methods);
    }

    /// <summary>Registers a documented hub. Intended for generated module initializers.</summary>
    /// <typeparam name="THub">The documented hub.</typeparam>
    /// <param name="document">The module and version descriptor.</param>
    /// <param name="methods">The generated method descriptions.</param>
    public static void RegisterHub<THub>(ApiDocumentDescriptor document, params HubMethodDescription[] methods)
        where THub : Hub
    {
        RegisterCore<THub>(document, FrozenDictionary<string, ApiOperationMetadata>.Empty, methods);
    }

    private static void RegisterCore<THub>(ApiDocumentDescriptor document,
        IReadOnlyDictionary<string, ApiOperationMetadata> operations, HubMethodDescription[] methods)
        where THub : Hub
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(methods);
        ArgumentNullException.ThrowIfNull(operations);
        if (document.Format != ApiDocumentFormat.AsyncApi || methods.Any(method => method is null))
        {
            throw new ArgumentException("An AsyncAPI descriptor and non-null methods are required.");
        }

        foreach (var operation in operations)
        {
            if (operation.Value is null || !methods.Any(method =>
                    operation.Key == (method.IsClientMethod ? "client." : string.Empty) + method.MemberName))
            {
                throw new ArgumentException($"Operation '{operation.Key}' does not identify a generated hub method or typed client callback.", nameof(operations));
            }
        }

        Registrations.Add(typeof(THub), new HubDescription(document, Array.AsReadOnly(methods.ToArray()),
            services => services.GetRequiredService<IOptions<HubOptions<THub>>>().Value,
            operations.ToFrozenDictionary(StringComparer.Ordinal)));
    }

    internal static HubDescription? Find(Type hubType) => Registrations.TryGetValue(hubType, out var value) ? value : null;
}

internal sealed record HubDescription(ApiDocumentDescriptor Document, IReadOnlyList<HubMethodDescription> Methods,
    Func<IServiceProvider, HubOptions> Options, IReadOnlyDictionary<string, ApiOperationMetadata> Operations);

internal sealed class MappedHubDocumentation
{
    internal List<MappedHubDescription> Hubs { get; } = [];
    internal Func<IReadOnlyList<Endpoint>>? Endpoints { get; set; }

    internal void Add(Type hubType, string path, HttpConnectionDispatcherOptions connectionOptions)
    {
        if (HubDocumentationRegistry.Find(hubType) is { } description)
        {
            Hubs.Add(new(hubType, path, description, connectionOptions));
        }
    }
}

internal sealed record MappedHubDescription(Type HubType, string Path, HubDescription Description,
    HttpConnectionDispatcherOptions ConnectionOptions);
