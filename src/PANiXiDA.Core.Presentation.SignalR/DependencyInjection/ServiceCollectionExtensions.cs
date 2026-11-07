using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using PANiXiDA.Core.Presentation.SignalR.Configurations;

using StackExchange.Redis;

namespace PANiXiDA.Core.Presentation.SignalR.DependencyInjection;

/// <summary>Registers reusable SignalR presentation services and middleware.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers SignalR and enables Redis when the SignalR:Redis section exists.</summary>
    /// <param name="services">The application service collection.</param>
    /// <param name="configuration">Configuration containing optional SignalR:Redis and ConnectionStrings sections.</param>
    /// <returns>The standard SignalR builder for further hub and protocol configuration.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>Redis settings are validated at startup. Authentication, authorization, hub routes and application services remain host-owned.</remarks>
    public static ISignalRServerBuilder AddSignalR(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var builder = services.AddSignalR();
        var section = configuration.GetSection(RedisBackplaneOptions.SectionName);
        if (!section.Exists())
        {
            return builder;
        }

        services.AddSingleton<IValidateOptions<RedisBackplaneOptions>>(new RedisBackplaneOptionsValidator(configuration));
        services.AddOptions<RedisBackplaneOptions>().Bind(section).ValidateOnStart();
        builder.AddStackExchangeRedis();
        services.AddOptions<RedisOptions>()
            .Configure<IOptions<RedisBackplaneOptions>>((redis, configured) =>
            {
                var options = configured.Value;
                redis.Configuration = ConfigurationOptions.Parse(configuration.GetConnectionString(options.ConnectionStringName)!);
                redis.Configuration.ChannelPrefix = RedisChannel.Literal(options.ChannelPrefix);
            });

        return builder;
    }

    /// <summary>Copies a single nonblank access_token query value into a missing Authorization header on exact hub paths.</summary>
    /// <param name="app">The application pipeline builder.</param>
    /// <param name="hubPaths">Literal absolute hub paths, without PathBase, query strings or route parameters.</param>
    /// <returns>The original application builder.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">No paths are supplied or a path is invalid.</exception>
    /// <remarks>Call before authentication, or before UseHttp from PANiXiDA.Core.Presentation.Http. Matching ignores case and trailing slashes; child paths including negotiate are excluded. Normal Bearer validation still runs.</remarks>
    public static IApplicationBuilder UseSignalR(this IApplicationBuilder app, params string[] hubPaths)
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
