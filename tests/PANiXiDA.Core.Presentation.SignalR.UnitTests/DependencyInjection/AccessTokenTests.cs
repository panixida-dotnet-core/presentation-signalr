using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using PANiXiDA.Core.Presentation.SignalR.DependencyInjection;

namespace PANiXiDA.Core.Presentation.SignalR.UnitTests.DependencyInjection;

public sealed class AccessTokenTests
{
    [Theory(DisplayName = "Query tokens are forwarded only on exact hub transport paths")]
    [InlineData("/hubs/events", "?access_token=valid", "Bearer valid")]
    [InlineData("/HUBS/EVENTS/", "?access_token=valid", "Bearer valid")]
    [InlineData("/hubs/other", "?access_token=valid", "Bearer valid")]
    [InlineData("/hubs/events/negotiate", "?access_token=valid", "")]
    [InlineData("/hubs/events/child", "?access_token=valid", "")]
    [InlineData("/hubs/events-extra", "?access_token=valid", "")]
    [InlineData("/api/orders", "?access_token=valid", "")]
    [InlineData(null, "?access_token=valid", "")]
    [InlineData("", "?access_token=valid", "")]
    [InlineData("/hubs/events", "", "")]
    [InlineData("/hubs/events", "?access_token=", "")]
    [InlineData("/hubs/events", "?access_token=%20", "")]
    [InlineData("/hubs/events", "?access_token=a&access_token=b", "")]
    [InlineData("/hubs/events", "?access_token=a%0D%0Ab", "")]
    public async Task UseSignalR_ShouldRestrictForwarding(string? path, string query, string expected)
    {
        // Arrange
        using var app = CreateApplication();
        app.UseSignalR(hubs =>
        {
            hubs.MapHub<TokenHub>("/hubs/events");
            hubs.MapHub<TokenHub>("/hubs/other");
        }).ShouldBeSameAs(app);
        var nextCalled = false;
        app.Run(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);

        // Act
        await ((IApplicationBuilder)app).Build()(context);

        // Assert
        context.Request.Headers.Authorization.ToString().ShouldBe(expected);
        nextCalled.ShouldBeTrue();
    }

    [Theory(DisplayName = "Existing Authorization headers always take precedence over query tokens")]
    [InlineData("Bearer original")]
    [InlineData("Basic original")]
    [InlineData("")]
    public async Task UseSignalR_ShouldPreserveAuthorization(string authorization)
    {
        using var app = CreateApplication();
        app.UseSignalR(hubs => hubs.MapHub<TokenHub>("/hubs/events"));
        var context = new DefaultHttpContext();
        context.Request.Path = "/hubs/events";
        context.Request.QueryString = new QueryString("?access_token=other");
        context.Request.Headers.Authorization = authorization;

        await ((IApplicationBuilder)app).Build()(context);

        context.Request.Headers.Authorization.ToString().ShouldBe(authorization);
    }

    [Fact(DisplayName = "Hub paths are copied and matched relative to PathBase")]
    public async Task UseSignalR_ShouldCopyPathsAndSupportPathBase()
    {
        using var app = CreateApplication();
        string[] paths = ["/hubs/events/"];
        app.UseSignalR(hubs => hubs.MapHub<TokenHub>(paths[0]));
        paths[0] = "/api/orders";
        var context = new DefaultHttpContext();
        context.Request.PathBase = "/gateway";
        context.Request.Path = "/hubs/events";
        context.Request.QueryString = new QueryString("?access_token=valid");

        await ((IApplicationBuilder)app).Build()(context);

        context.Request.Headers.Authorization.ToString().ShouldBe("Bearer valid");
    }

    [Theory(DisplayName = "Invalid hub paths fail during pipeline configuration")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("//")]
    [InlineData("hubs/events")]
    [InlineData("/hubs/{id}")]
    [InlineData("/hubs/*")]
    [InlineData("/hubs/events?query=1")]
    [InlineData("/hubs/events#fragment")]
    [InlineData("/hubs/ events")]
    [InlineData("/hubs\\events")]
    public void UseSignalR_ShouldRejectInvalidPaths(string? path)
    {
        using var app = CreateApplication();

        Should.Throw<ArgumentException>(() => app.UseSignalR(hubs => hubs.MapHub<TokenHub>(path!)));
    }

    [Fact(DisplayName = "SignalR mapping requires at least one hub")]
    public void UseSignalR_ShouldRejectEmptyMapping()
    {
        using var app = CreateApplication();

        Should.Throw<ArgumentException>(() => app.UseSignalR(_ => { }));
    }

    [Theory(DisplayName = "Hub mapping rejects duplicate paths ignoring case and trailing slashes")]
    [InlineData("/hubs/events")]
    [InlineData("/HUBS/EVENTS/")]
    public void UseSignalR_ShouldRejectDuplicatePaths(string secondPath)
    {
        using var app = CreateApplication();

        Should.Throw<ArgumentException>(() => app.UseSignalR(hubs =>
        {
            hubs.MapHub<TokenHub>("/hubs/events");
            hubs.MapHub<TokenHub>(secondPath);
        }));
    }

    private static WebApplication CreateApplication()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR(new ConfigurationBuilder().Build());
        return builder.Build();
    }

    public sealed class TokenHub : Hub;
}
