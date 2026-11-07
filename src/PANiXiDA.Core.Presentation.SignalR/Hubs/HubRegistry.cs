using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;

using Microsoft.AspNetCore.SignalR;

namespace PANiXiDA.Core.Presentation.SignalR.Hubs;

/// <summary>Connects source-generated assembly registrations to hub mapping. Intended for generated code.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class HubRegistry
{
    private static readonly ConditionalWeakTable<Assembly, Action<HubMapBuilder>> Registrations = [];

    /// <summary>Registers the statically generated mapping callback for an assembly.</summary>
    /// <param name="assembly">The assembly containing the generated registration.</param>
    /// <param name="mapHubs">The callback mapping its hubs in deterministic type-name order.</param>
    /// <exception cref="ArgumentException">The assembly already has a registration.</exception>
    public static void RegisterAssembly(Assembly assembly, Action<HubMapBuilder> mapHubs)
    {
        Registrations.Add(assembly, mapHubs);
    }

    /// <summary>Invokes a hub's static mapping contract without reflection or hub activation.</summary>
    /// <typeparam name="THub">The hub declaring the mapping contract.</typeparam>
    /// <param name="hubs">The hub mapping builder.</param>
    public static void MapHub<THub>(HubMapBuilder hubs)
        where THub : Hub, IHubEndpoint
    {
        THub.Map(hubs);
    }

    internal static void MapAssembly(HubMapBuilder hubs, Assembly assembly)
    {
        RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
        if (!Registrations.TryGetValue(assembly, out var mapHubs))
        {
            throw new InvalidOperationException(
                $"Generated hub registration was not found for assembly '{assembly.FullName}'. " +
                "Reference PANiXiDA.Core.Presentation.SignalR with its analyzers in the hub project and rebuild it.");
        }

        mapHubs(hubs);
    }
}
