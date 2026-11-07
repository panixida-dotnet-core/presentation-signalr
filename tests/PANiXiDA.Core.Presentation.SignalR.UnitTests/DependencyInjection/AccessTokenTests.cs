using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

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
    public async Task UseSignalRAccessTokens_ShouldRestrictForwarding(string? path, string query, string expected)
    {
        // Arrange
        using var services = new ServiceCollection().BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        app.UseSignalRAccessTokens("/hubs/events", "/hubs/other").ShouldBeSameAs(app);
        var nextCalled = false;
        app.Run(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);

        // Act
        await app.Build()(context);

        // Assert
        context.Request.Headers.Authorization.ToString().ShouldBe(expected);
        nextCalled.ShouldBeTrue();
    }

    [Theory(DisplayName = "Existing Authorization headers always take precedence over query tokens")]
    [InlineData("Bearer original")]
    [InlineData("Basic original")]
    [InlineData("")]
    public async Task UseSignalRAccessTokens_ShouldPreserveAuthorization(string authorization)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        app.UseSignalRAccessTokens("/hubs/events");
        var context = new DefaultHttpContext();
        context.Request.Path = "/hubs/events";
        context.Request.QueryString = new QueryString("?access_token=other");
        context.Request.Headers.Authorization = authorization;

        await app.Build()(context);

        context.Request.Headers.Authorization.ToString().ShouldBe(authorization);
    }

    [Fact(DisplayName = "Hub paths are copied and matched relative to PathBase")]
    public async Task UseSignalRAccessTokens_ShouldCopyPathsAndSupportPathBase()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        string[] paths = ["/hubs/events/"];
        app.UseSignalRAccessTokens(paths);
        paths[0] = "/api/orders";
        var context = new DefaultHttpContext();
        context.Request.PathBase = "/gateway";
        context.Request.Path = "/hubs/events";
        context.Request.QueryString = new QueryString("?access_token=valid");

        await app.Build()(context);

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
    public void UseSignalRAccessTokens_ShouldRejectInvalidPaths(string? path)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var app = new ApplicationBuilder(services);

        Should.Throw<ArgumentException>(() => app.UseSignalRAccessTokens([path!]));
    }

    [Fact(DisplayName = "Access token middleware rejects an empty hub path list")]
    public void UseSignalRAccessTokens_ShouldRejectEmptyPaths()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var app = new ApplicationBuilder(services);

        Should.Throw<ArgumentException>(() => app.UseSignalRAccessTokens());
    }
}
