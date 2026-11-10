using System.ComponentModel;

namespace PANiXiDA.Core.Presentation.SignalR.Documentation;

/// <summary>Describes a serialized hub argument. Intended for generated code.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class HubParameterDescription
{
    /// <summary>Creates an argument description.</summary>
    /// <param name="name">The source parameter name.</param>
    /// <param name="type">The payload type, or stream item type.</param>
    /// <param name="isStream">Whether this argument uploads a stream.</param>
    /// <param name="allowImplicitServices">Whether SignalR may resolve this parameter implicitly from DI.</param>
    public HubParameterDescription(string name, Type type, bool isStream = false, bool allowImplicitServices = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(type);
        Name = name;
        Type = type;
        IsStream = isStream;
        AllowImplicitServices = allowImplicitServices;
    }

    /// <summary>Gets the parameter name.</summary>
    public string Name { get; }
    /// <summary>Gets the payload type.</summary>
    public Type Type { get; }
    /// <summary>Gets whether the parameter is a stream.</summary>
    public bool IsStream { get; }
    /// <summary>Gets whether implicit service resolution applies to this parameter.</summary>
    public bool AllowImplicitServices { get; }
}
