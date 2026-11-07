namespace PANiXiDA.Core.Presentation.SignalR.Configuration;

/// <summary>Configures the optional Redis backplane shared by the application's hubs.</summary>
public sealed class RedisBackplaneOptions
{
    /// <summary>Gets the configuration section that enables the Redis backplane when present.</summary>
    public const string SectionName = "SignalR:Redis";

    /// <summary>Gets or sets the name of the Redis connection string in ConnectionStrings.</summary>
    public string ConnectionStringName { get; set; } = "Redis";

    /// <summary>Gets or sets a required channel prefix unique to the application and environment.</summary>
    public string ChannelPrefix { get; set; } = string.Empty;
}
