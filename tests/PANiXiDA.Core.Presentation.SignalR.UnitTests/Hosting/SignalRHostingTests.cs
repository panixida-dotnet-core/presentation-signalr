using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PANiXiDA.Core.Presentation.SignalR.DependencyInjection;
using PANiXiDA.Core.Presentation.SignalR.Hubs;

namespace PANiXiDA.Core.Presentation.SignalR.UnitTests.Hosting;

public sealed class SignalRHostingTests
{
    [Theory(DisplayName = "Query token forwarding preserves host authentication and excludes negotiation and unrelated routes")]
    [InlineData("/hubs/events?access_token=valid", false, null, HttpStatusCode.BadRequest)]
    [InlineData("/hubs/other?access_token=valid", false, null, HttpStatusCode.BadRequest)]
    [InlineData("/hubs/events?access_token=invalid", false, null, HttpStatusCode.Unauthorized)]
    [InlineData("/hubs/events?access_token=valid", false, "Bearer invalid", HttpStatusCode.Unauthorized)]
    [InlineData("/hubs/events/negotiate?negotiateVersion=1&access_token=valid", true, null, HttpStatusCode.Unauthorized)]
    [InlineData("/hubs/events/negotiate?negotiateVersion=1", true, "Bearer valid", HttpStatusCode.OK)]
    [InlineData("/api/private?access_token=valid", false, null, HttpStatusCode.Unauthorized)]
    [InlineData("/api/private", false, "Bearer valid", HttpStatusCode.OK)]
    public async Task Hosting_ShouldUseNormalAuthentication(string path, bool post, string? authorization, HttpStatusCode expected)
    {
        await using var app = await CreateApplicationAsync();
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri(app.Urls.Single())
        };
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
        await using var connection = new HubConnectionBuilder()
            .WithUrl($"{app.Urls.Single()}/hubs/events", options =>
            {
                options.Transports = transport;
                options.HttpMessageHandlerFactory = handler =>
                {
                    if (handler is HttpClientHandler clientHandler)
                    {
                        clientHandler.UseProxy = false;
                    }

                    return handler;
                };
                options.AccessTokenProvider = () => Task.FromResult<string?>("valid");
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var uri = new UriBuilder(context.Uri);
                    uri.Query = $"{uri.Query.TrimStart('?')}&access_token=valid";
                    var socket = new ClientWebSocket();
                    socket.Options.Proxy = null;
                    try
                    {
                        await socket.ConnectAsync(uri.Uri, cancellationToken);
                        return socket;
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                };
            }).Build();

        await connection.StartAsync(TestContext.Current.CancellationToken);
        var user = await connection.InvokeAsync<string>("GetUser", TestContext.Current.CancellationToken);

        user.ShouldBe("test-user");
    }

    [Fact(DisplayName = "Hub mapping applies standard connection options to negotiation")]
    public async Task Hosting_ShouldApplyConnectionOptions()
    {
        await using var app = await CreateApplicationAsync(hubs =>
            hubs.MapHub<TestHub>("/hubs/events", options => options.Transports = HttpTransportType.LongPolling)
                .RequireAuthorization());
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri(app.Urls.Single())
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/hubs/events/negotiate?negotiateVersion=1");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer valid");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.RootElement.GetProperty("availableTransports").EnumerateArray()
            .Select(transport => transport.GetProperty("transport").GetString()).ShouldBe(["LongPolling"]);
    }

    [Fact(DisplayName = "Automatic mapping does not activate hubs with constructor dependencies")]
    public async Task Hosting_ShouldDeferHubActivation()
    {
        await using var app = await CreateApplicationAsync();

        app.Services.GetRequiredService<ActivationCounter>().Count.ShouldBe(0);
    }

    private static async Task<WebApplication> CreateApplicationAsync(Action<HubMapBuilder>? configureHubs = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("TestBearer").AddScheme<AuthenticationSchemeOptions, TestBearerHandler>("TestBearer", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<ActivationCounter>();
        builder.Services.AddSignalR(new ConfigurationBuilder().Build());
        var app = builder.Build();
        if (configureHubs is null)
        {
            app.UseSignalR(typeof(SignalRHostingTests).Assembly);
        }
        else
        {
            app.UseSignalR(configureHubs);
        }
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/api/private", () => "authorized").RequireAuthorization();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    public sealed class TestHub : Hub, IHubEndpoint
    {
        public TestHub(ActivationCounter counter)
        {
            counter.Count++;
        }

        public static HubEndpointConventionBuilder Map(HubMapBuilder hubs) =>
            hubs.MapHub<TestHub>("/hubs/events").RequireAuthorization();

        public string GetUser() => Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)!;
    }

    public sealed class OtherHub : Hub, IHubEndpoint
    {
        public static HubEndpointConventionBuilder Map(HubMapBuilder hubs) =>
            hubs.MapHub<OtherHub>("/hubs/other").RequireAuthorization();
    }

    public sealed class ActivationCounter
    {
        public int Count { get; set; }
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
