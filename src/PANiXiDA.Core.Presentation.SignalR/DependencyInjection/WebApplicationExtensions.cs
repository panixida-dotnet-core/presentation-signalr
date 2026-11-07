using Microsoft.AspNetCore.Builder;

namespace PANiXiDA.Core.Presentation.SignalR.DependencyInjection;

/// <summary>Configures SignalR middleware for a web application.</summary>
public static class WebApplicationExtensions
{
    /// <summary>Copies a single nonblank access_token query value into a missing Authorization header on exact hub paths.</summary>
    /// <param name="app">The web application.</param>
    /// <param name="hubPaths">Literal absolute hub paths, without PathBase, query strings or route parameters.</param>
    /// <returns>The original web application for further middleware and endpoint configuration.</returns>
    /// <exception cref="ArgumentException">No paths are supplied or a path is invalid.</exception>
    /// <remarks>Call before authentication, or before UseHttp from PANiXiDA.Core.Presentation.Http. Matching ignores case and trailing slashes; child paths including negotiate are excluded. Normal Bearer validation still runs.</remarks>
    public static WebApplication UseSignalR(this WebApplication app, params string[] hubPaths)
    {
        if (hubPaths.Length == 0)
        {
            throw new ArgumentException("At least one hub path must be provided.", nameof(hubPaths));
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in hubPaths)
        {
            if (!IsValidHubPath(path))
            {
                throw new ArgumentException("Hub paths must be literal absolute paths without whitespace, query strings or route parameters.", nameof(hubPaths));
            }

            paths.Add(path.TrimEnd('/'));
        }

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

    private static bool IsValidHubPath(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) && path.StartsWith('/')
            && !path.StartsWith("//", StringComparison.Ordinal) && path.TrimEnd('/').Length > 0
            && !path.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))
            && path.IndexOfAny(['?', '#', '{', '}', '*', '\\']) < 0;
    }
}
