using Microsoft.AspNetCore.Builder;

namespace PANiXiDA.Core.Presentation.SignalR.DependencyInjection;

/// <summary>Configures access token forwarding for SignalR transport requests.</summary>
public static class ApplicationBuilderExtensions
{
    /// <summary>Copies a single nonblank access_token query value into a missing Authorization header on exact hub paths.</summary>
    /// <param name="app">The application pipeline builder.</param>
    /// <param name="hubPaths">Literal absolute hub paths, without PathBase, query strings or route parameters.</param>
    /// <returns>The original application builder.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">No paths are supplied or a path is invalid.</exception>
    /// <remarks>Call before authentication, or before UseHttp from PANiXiDA.Core.Presentation.Http. Matching ignores case and trailing slashes; child paths including negotiate are excluded. Normal Bearer validation still runs.</remarks>
    public static IApplicationBuilder UseSignalRAccessTokens(this IApplicationBuilder app, params string[] hubPaths)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(hubPaths);
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

        return app.Use(async (context, next) =>
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
    }

    private static bool IsValidHubPath(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) && path.StartsWith('/')
            && !path.StartsWith("//", StringComparison.Ordinal) && path.TrimEnd('/').Length > 0
            && !path.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))
            && path.IndexOfAny(['?', '#', '{', '}', '*', '\\']) < 0;
    }
}
