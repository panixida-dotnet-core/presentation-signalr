using System.ComponentModel;

namespace PANiXiDA.Core.Presentation.SignalR.Documentation;

/// <summary>Describes a hub method or typed client callback. Intended for generated code.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class HubMethodDescription
{
    /// <summary>Creates a method description.</summary>
    /// <param name="name">The SignalR target name.</param>
    /// <param name="parameters">Serialized arguments.</param>
    /// <param name="resultType">The result or stream item type, or null for a void result.</param>
    /// <param name="isClientMethod">Whether the server invokes this method on clients.</param>
    /// <param name="isStream">Whether the result is streamed.</param>
    /// <param name="summary">The XML summary extracted at compile time.</param>
    /// <param name="requiresAuthorization">Whether the method declares authorization.</param>
    public HubMethodDescription(string name, IReadOnlyList<HubParameterDescription> parameters,
        Type? resultType = null, bool isClientMethod = false, bool isStream = false,
        string? summary = null, bool requiresAuthorization = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.Any(parameter => parameter is null))
        {
            throw new ArgumentException("Parameters must not contain null.", nameof(parameters));
        }

        Name = name;
        Parameters = Array.AsReadOnly(parameters.ToArray());
        ResultType = resultType;
        IsClientMethod = isClientMethod;
        IsStream = isStream;
        Summary = summary;
        RequiresAuthorization = requiresAuthorization;
    }

    /// <summary>Gets the SignalR target name.</summary>
    public string Name { get; }
    /// <summary>Gets the serialized arguments.</summary>
    public IReadOnlyList<HubParameterDescription> Parameters { get; }
    /// <summary>Gets the optional return or stream item type.</summary>
    public Type? ResultType { get; }
    /// <summary>Gets whether this is a client callback.</summary>
    public bool IsClientMethod { get; }
    /// <summary>Gets whether the result is streamed.</summary>
    public bool IsStream { get; }
    /// <summary>Gets the source summary.</summary>
    public string? Summary { get; }
    /// <summary>Gets whether method authorization is required.</summary>
    public bool RequiresAuthorization { get; }
}
