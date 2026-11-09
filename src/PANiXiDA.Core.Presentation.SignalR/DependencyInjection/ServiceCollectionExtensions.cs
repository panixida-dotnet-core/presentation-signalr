using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using PANiXiDA.Core.Presentation.SignalR.Configurations;

using StackExchange.Redis;

namespace PANiXiDA.Core.Presentation.SignalR.DependencyInjection;

/// <summary>Registers reusable SignalR presentation services.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers SignalR and enables Redis when the SignalR:Redis section exists.</summary>
    /// <param name="services">The application service collection.</param>
    /// <param name="configuration">Configuration containing optional SignalR:Redis and ConnectionStrings sections.</param>
    /// <returns>The standard SignalR builder for further hub and protocol configuration.</returns>
    /// <remarks>Redis settings are validated at startup. Authentication, authorization, hub routes and application services remain host-owned.</remarks>
    public static ISignalRServerBuilder AddSignalR(
        this IServiceCollection services,
        IConfiguration configuration)
    {
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
}
