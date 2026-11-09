using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using PANiXiDA.Core.Presentation.SignalR.DependencyInjection;

namespace PANiXiDA.Core.Presentation.SignalR.UnitTests.Configurations;

public sealed class HubOptionsTests
{
    [Theory(DisplayName = "Detailed errors are enabled only in Development with and without Redis")]
    [InlineData("Development", false, true)]
    [InlineData("Development", true, true)]
    [InlineData("Production", false, false)]
    [InlineData("Production", true, false)]
    [InlineData("Staging", false, false)]
    [InlineData("Staging", true, false)]
    [InlineData("Testing", false, false)]
    [InlineData("Testing", true, false)]
    public async Task AddSignalR_ShouldConfigureDetailedErrors(string environmentName, bool useRedis, bool expected)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = environmentName });
        if (useRedis)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SignalR:Redis:ChannelPrefix"] = "tests",
                ["ConnectionStrings:Redis"] = "localhost:6379"
            });
        }
        builder.Services.Configure<HubOptions>(options => options.EnableDetailedErrors = true);

        builder.Services.AddSignalR(builder.Configuration);
        await using var app = builder.Build();

        app.Services.GetRequiredService<IOptions<HubOptions>>().Value.EnableDetailedErrors.ShouldBe(expected);
    }

    [Fact(DisplayName = "Detailed errors stay disabled without a registered host environment")]
    public void AddSignalR_ShouldDisableDetailedErrorsWithoutEnvironment()
    {
        var services = new ServiceCollection();
        services.AddSignalR(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<HubOptions>>().Value.EnableDetailedErrors.ShouldBe(false);
    }

    [Fact(DisplayName = "A host can explicitly disable Development detailed errors after Core registration")]
    public async Task AddSignalR_ShouldAllowHostToDisableDetailedErrors()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Services.AddSignalR(builder.Configuration);
        builder.Services.Configure<HubOptions>(options => options.EnableDetailedErrors = false);
        await using var app = builder.Build();

        app.Services.GetRequiredService<IOptions<HubOptions>>().Value.EnableDetailedErrors.ShouldBe(false);
    }
}
