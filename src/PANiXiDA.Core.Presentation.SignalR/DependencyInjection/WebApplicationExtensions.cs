using Microsoft.AspNetCore.Builder;

using PANiXiDA.Core.Presentation.SignalR.Hubs;

namespace PANiXiDA.Core.Presentation.SignalR.DependencyInjection;

/// <summary>Configures SignalR middleware for a web application.</summary>
public static class WebApplicationExtensions
{
    /// <summary>Maps application hubs and forwards query access tokens only on their exact transport paths.</summary>
    /// <param name="app">The web application.</param>
    /// <param name="configureHubs">The callback mapping hubs and configuring their authorization, metadata and connection options.</param>
    /// <returns>The original web application for further middleware and endpoint configuration.</returns>
    /// <exception cref="ArgumentException">No hubs are mapped or a hub path is invalid or duplicated.</exception>
    /// <remarks>Call before authentication, or before UseHttp from PANiXiDA.Core.Presentation.Http. Matching ignores case and trailing slashes; child paths including negotiate are excluded. Normal Bearer validation still runs.</remarks>
    public static WebApplication UseSignalR(this WebApplication app, Action<HubMapBuilder> configureHubs)
    {
        var mapping = new HubMapBuilder(app);
        configureHubs(mapping);
        if (mapping.Paths.Count == 0)
        {
            throw new ArgumentException("At least one hub must be mapped.", nameof(configureHubs));
        }

        var paths = new HashSet<string>(mapping.Paths, StringComparer.OrdinalIgnoreCase);

        app.Use(async (context, next) =>
        {
            var request = context.Request;
            if (paths.Contains(request.Path.ToString().TrimEnd('/'))
                && !request.Headers.ContainsKey("Authorization")
                && request.Query.TryGetValue("access_token", out var token)
                && token.Count == 1 && !string.IsNullOrWhiteSpace(token[0])
                && !token[0]!.Any(char.IsControl))
            {
                request.Headers.Authorization = $"Bearer {token[0]}";
            }

            await next(context);
        });

        return app;
    }

}
