using System.Collections.Frozen;

using Asp.Versioning;

using PANiXiDA.Core.ApiDocumentation;

namespace PANiXiDA.Core.Presentation.SignalR.Hubs;

/// <summary>Provides statically accessible module, version and operation metadata for a generated hub contract.</summary>
public interface IDocumentedHubEndpoint : IHubEndpoint
{
    /// <summary>Gets the stable module name used in document URLs.</summary>
    static abstract string Name { get; }

    /// <summary>Gets the human-readable module title.</summary>
    static abstract string Summary { get; }

    /// <summary>Gets the documented contract version. Hub routes remain application-owned.</summary>
    static abstract ApiVersion ApiVersion { get; }

    /// <summary>Gets optional metadata keyed by CLR method name. Prefix typed client callbacks with <c>client.</c>.</summary>
    static virtual IReadOnlyDictionary<string, ApiOperationMetadata> Operations => FrozenDictionary<string, ApiOperationMetadata>.Empty;

    /// <summary>Gets whether the documented version is deprecated.</summary>
    static virtual bool IsDeprecated => false;
}
