using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PANiXiDA.Core.Presentation.SignalR.DependencyInjection;

using StackExchange.Redis;

namespace PANiXiDA.Core.Presentation.SignalR.UnitTests.DependencyInjection;

public sealed class ServiceCollectionExtensionsTests
{
    [Fact(DisplayName = "SignalR uses the in-memory lifetime manager when Redis is not enabled")]
    public void AddSignalR_ShouldSupportSingleHost()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Redis"] = "localhost:6379"
        }).Build();

        var builder = services.AddSignalR(configuration);
        builder.AddHubOptions<Hub>(options => options.MaximumReceiveMessageSize = 4096);
        using var provider = services.BuildServiceProvider();

        builder.Services.ShouldBeSameAs(services);
        provider.GetRequiredService<HubLifetimeManager<Hub>>().ShouldBeOfType<DefaultHubLifetimeManager<Hub>>();
        provider.GetRequiredService<IOptions<HubOptions<Hub>>>().Value.MaximumReceiveMessageSize.ShouldBe(4096);
    }

    [Theory(DisplayName = "Redis configuration uses the selected connection and a literal channel prefix")]
    [InlineData("Redis")]
    [InlineData("Streaming")]
    public void AddSignalR_ShouldConfigureRedis(string connectionName)
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration();
        configuration["SignalR:Redis:ConnectionStringName"] = connectionName;
        configuration[$"ConnectionStrings:{connectionName}"] = "localhost:6380,ssl=true,abortConnect=false";

        services.AddSignalR(configuration);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RedisOptions>>().Value;

        options.Configuration.EndPoints.ShouldHaveSingleItem().ToString().ShouldBe("Unspecified/localhost:6380");
        options.Configuration.Ssl.ShouldBeTrue();
        options.Configuration.AbortOnConnectFail.ShouldBeFalse();
        options.Configuration.ChannelPrefix.ShouldBe(RedisChannel.Literal("test:streaming"));
        services.ShouldContain(descriptor => descriptor.ServiceType == typeof(HubLifetimeManager<>));
    }

    [Theory(DisplayName = "Invalid Redis settings prevent application startup without exposing the connection string")]
    [InlineData("SignalR:Redis:ChannelPrefix", "")]
    [InlineData("SignalR:Redis:ChannelPrefix", " ")]
    [InlineData("SignalR:Redis:ConnectionStringName", "")]
    [InlineData("SignalR:Redis:ConnectionStringName", "Missing")]
    [InlineData("ConnectionStrings:Redis", "")]
    [InlineData("ConnectionStrings:Redis", "password=secret")]
    [InlineData("ConnectionStrings:Redis", "localhost,password=secret,invalidSetting=true")]
    public async Task AddSignalR_ShouldValidateRedisAtStartup(string key, string value)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var configuration = CreateConfiguration();
        configuration[key] = value;
        builder.Services.AddSignalR(configuration);
        await using var app = builder.Build();

        var exception = await Should.ThrowAsync<OptionsValidationException>(
            () => app.StartAsync(TestContext.Current.CancellationToken));

        exception.Message.ShouldNotContain("secret");
    }

    [Fact(DisplayName = "SignalR registration validates null arguments")]
    public void AddSignalR_ShouldValidateArguments()
    {
        Should.Throw<ArgumentNullException>(() => ServiceCollectionExtensions.AddSignalR(null!, new ConfigurationBuilder().Build()));
        Should.Throw<ArgumentNullException>(() => new ServiceCollection().AddSignalR((IConfiguration)null!));
    }

    private static IConfigurationRoot CreateConfiguration()
    {
        return new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SignalR:Redis:ChannelPrefix"] = "test:streaming",
            ["ConnectionStrings:Redis"] = "localhost:6379"
        }).Build();
    }
}
