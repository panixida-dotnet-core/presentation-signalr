using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PANiXiDA.Core.Presentation.SignalR.DependencyInjection;

namespace PANiXiDA.Core.Presentation.SignalR.UnitTests.Hosting;

public sealed class SignalRHostingTests
{
    [Theory(DisplayName = "Query token forwarding preserves host authentication and excludes negotiation and unrelated routes")]
    [InlineData("/hubs/events?access_token=valid", false, null, HttpStatusCode.BadRequest)]
    [InlineData("/hubs/events?access_token=invalid", false, null, HttpStatusCode.Unauthorized)]
    [InlineData("/hubs/events?access_token=valid", false, "Bearer invalid", HttpStatusCode.Unauthorized)]
    [InlineData("/hubs/events/negotiate?negotiateVersion=1&access_token=valid", true, null, HttpStatusCode.Unauthorized)]
    [InlineData("/hubs/events/negotiate?negotiateVersion=1", true, "Bearer valid", HttpStatusCode.OK)]
    [InlineData("/api/private?access_token=valid", false, null, HttpStatusCode.Unauthorized)]
    [InlineData("/api/private", false, "Bearer valid", HttpStatusCode.OK)]
    public async Task Hosting_ShouldUseNormalAuthentication(string path, bool post, string? authorization, HttpStatusCode expected)
    {
        await using var app = await CreateApplicationAsync();
        var server = app.GetTestServer();
        using var client = server.CreateClient();
        using var request = new HttpRequestMessage(post ? HttpMethod.Post : HttpMethod.Get, path);
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(expected);
    }

    [Theory(DisplayName = "SignalR clients negotiate and invoke hubs with header or browser-style query tokens")]
    [InlineData(HttpTransportType.LongPolling)]
    [InlineData(HttpTransportType.WebSockets)]
    public async Task Hosting_ShouldSupportSignalRClient(HttpTransportType transport)
    {
        await using var app = await CreateApplicationAsync();
        var server = app.GetTestServer();
        await using var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/events", options =>
            {
                options.Transports = transport;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>("valid");
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var uri = new UriBuilder(context.Uri);
                    uri.Query = $"{uri.Query.TrimStart('?')}&access_token=valid";
                    return await server.CreateWebSocketClient().ConnectAsync(uri.Uri, cancellationToken);
                };
            }).Build();

        await connection.StartAsync(TestContext.Current.CancellationToken);
        var user = await connection.InvokeAsync<string>("GetUser", TestContext.Current.CancellationToken);

        user.ShouldBe("test-user");
    }

    private static async Task<WebApplication> CreateApplicationAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("TestBearer").AddScheme<AuthenticationSchemeOptions, TestBearerHandler>("TestBearer", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSignalR(new ConfigurationBuilder().Build());
        var app = builder.Build();
        app.UseSignalRAccessTokens("/hubs/events");
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHub<TestHub>("/hubs/events").RequireAuthorization();
        app.MapGet("/api/private", () => "authorized").RequireAuthorization();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    public sealed class TestHub : Hub
    {
        public string GetUser() => Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)!;
    }

    private sealed class TestBearerHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers.Authorization != "Bearer valid")
            {
                return Task.FromResult(AuthenticateResult.Fail("Invalid token."));
            }

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "test-user")], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
