using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

using StackExchange.Redis;

namespace PANiXiDA.Core.Presentation.SignalR.Configuration;

internal sealed class RedisBackplaneOptionsValidator(IConfiguration configuration)
    : IValidateOptions<RedisBackplaneOptions>
{
    public ValidateOptionsResult Validate(string? name, RedisBackplaneOptions options)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.ChannelPrefix))
        {
            failures.Add("'SignalR:Redis:ChannelPrefix' must be configured.");
        }

        if (string.IsNullOrWhiteSpace(options.ConnectionStringName))
        {
            failures.Add("'SignalR:Redis:ConnectionStringName' must be configured.");
        }
        else
        {
            ValidateConnectionString(configuration.GetConnectionString(options.ConnectionStringName), failures);
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateConnectionString(string? connectionString, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            failures.Add("The configured SignalR Redis connection string must not be empty.");
            return;
        }

        try
        {
            if (ConfigurationOptions.Parse(connectionString).EndPoints.Count == 0)
            {
                failures.Add("The SignalR Redis connection string must specify an endpoint.");
            }
        }
        catch (ArgumentException)
        {
            failures.Add("The SignalR Redis connection string is invalid.");
        }
    }
}
