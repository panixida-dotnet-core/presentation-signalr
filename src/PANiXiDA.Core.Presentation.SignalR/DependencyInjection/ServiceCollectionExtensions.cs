using PANiXiDA.Core.ApiDocumentation.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using Nerdbank.MessagePack;

using PANiXiDA.Core.Presentation.SignalR.Configurations;
using PANiXiDA.Core.Presentation.SignalR.Documentation;
using PANiXiDA.Core.Presentation.SignalR.Hubs;
using PANiXiDA.Core.Presentation.SignalR.Logging;

using PolyType;

using StackExchange.Redis;

namespace PANiXiDA.Core.Presentation.SignalR.DependencyInjection;

/// <summary>Registers reusable SignalR presentation services.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers SignalR with JSON and MessagePack and enables optional Redis.</summary>
    /// <param name="services">The application service collection.</param>
    /// <param name="configuration">Configuration containing optional SignalR:Redis and ConnectionStrings sections.</param>
    /// <returns>The standard SignalR builder for further hub and protocol configuration.</returns>
    /// <remarks>Redis settings are validated at startup. Authentication, authorization, hub routes and application services remain host-owned.</remarks>
    public static ISignalRServerBuilder AddSignalR(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        return services.AddSignalR(configuration, MessagePackTypes.GeneratedTypeShapeProvider);
    }

    /// <summary>Registers SignalR with JSON, source-generated MessagePack contracts and optional Redis.</summary>
    /// <param name="services">The application service collection.</param>
    /// <param name="configuration">Configuration containing optional SignalR:Redis and ConnectionStrings sections.</param>
    /// <param name="messagePackTypes">Source-generated shapes covering every MessagePack argument, result and notification payload.</param>
    /// <param name="messagePackSerializer">Optional application serializer overriding Core's Microsoft-client-compatible defaults.</param>
    /// <returns>The standard SignalR builder for further hub and JSON configuration.</returns>
    /// <remarks>The existing overload supplies shapes for common scalar types. Application DTOs require their own generated provider.</remarks>
    /// <exception cref="ArgumentNullException">The type shape provider is null.</exception>
    public static ISignalRServerBuilder AddSignalR(
        this IServiceCollection services,
        IConfiguration configuration,
        ITypeShapeProvider messagePackTypes,
        MessagePackSerializer? messagePackSerializer = null)
    {
        ArgumentNullException.ThrowIfNull(messagePackTypes);
        services.TryAddSingleton<MappedHubDocumentation>();
        services.AddApiDocumentSource<SignalRDocumentSource>();
        var builder = services.AddSignalR().AddJsonConfiguration()
            .AddMessagePackConfiguration(messagePackTypes, messagePackSerializer);
        services.AddSingleton<HubExceptionFilter>();
        services.AddSingleton<HubLoggingFilter>();
        services.AddOptions<HubOptions>().Configure<IServiceProvider>((options, provider) =>
        {
            options.EnableDetailedErrors = provider.GetService<IHostEnvironment>()?.IsDevelopment() == true;
            options.AddFilter<HubExceptionFilter>();
            options.AddFilter<HubLoggingFilter>();
        });
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
