using System.Threading.Channels;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using PANiXiDA.Core.Presentation.SignalR.DependencyInjection;

namespace PANiXiDA.Core.Presentation.SignalR.IntegrationTests;

public sealed class RedisBackplaneTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    [Fact(DisplayName = "Redis delivers typed group notifications between independent hosts and multiple connections")]
    public async Task Redis_ShouldDeliverAcrossHosts()
    {
        var prefix = $"signalr-tests:{Guid.NewGuid():N}";
        await using var first = await CreateApplicationAsync(prefix);
        await using var second = await CreateApplicationAsync(prefix);
        await using var firstConnection = CreateConnection(first);
        await using var secondConnection = CreateConnection(first);
        var firstMessages = Channel.CreateUnbounded<string>();
        var secondMessages = Channel.CreateUnbounded<string>();
        firstConnection.On<string>("Received", message => firstMessages.Writer.TryWrite(message));
        secondConnection.On<string>("Received", message => secondMessages.Writer.TryWrite(message));
        await firstConnection.StartAsync(TestContext.Current.CancellationToken);
        await secondConnection.StartAsync(TestContext.Current.CancellationToken);
        await firstConnection.InvokeAsync("Join", "players", TestContext.Current.CancellationToken);
        await secondConnection.InvokeAsync("Join", "players", TestContext.Current.CancellationToken);
        var publisher = second.Services.GetRequiredService<IHubContext<StreamingHub, IStreamingClient>>();

        await publisher.Clients.Group("players").Received("snapshot");

        (await ReadMessageAsync(firstMessages)).ShouldBe("snapshot");
        (await ReadMessageAsync(secondMessages)).ShouldBe("snapshot");
    }

    [Fact(DisplayName = "Redis channel prefixes isolate applications sharing one server and hub type")]
    public async Task Redis_ShouldIsolatePrefixes()
    {
        await using var first = await CreateApplicationAsync($"signalr-tests:{Guid.NewGuid():N}");
        await using var second = await CreateApplicationAsync($"signalr-tests:{Guid.NewGuid():N}");
        await using var firstConnection = CreateConnection(first);
        await using var secondConnection = CreateConnection(second);
        var firstMessages = Channel.CreateUnbounded<string>();
        var secondMessages = Channel.CreateUnbounded<string>();
        firstConnection.On<string>("Received", message => firstMessages.Writer.TryWrite(message));
        secondConnection.On<string>("Received", message => secondMessages.Writer.TryWrite(message));
        await firstConnection.StartAsync(TestContext.Current.CancellationToken);
        await secondConnection.StartAsync(TestContext.Current.CancellationToken);
        var firstPublisher = first.Services.GetRequiredService<IHubContext<StreamingHub, IStreamingClient>>();
        var secondPublisher = second.Services.GetRequiredService<IHubContext<StreamingHub, IStreamingClient>>();

        await firstPublisher.Clients.All.Received("first");
        await secondPublisher.Clients.All.Received("second");

        (await ReadMessageAsync(firstMessages)).ShouldBe("first");
        (await ReadMessageAsync(secondMessages)).ShouldBe("second");
        await Should.ThrowAsync<TimeoutException>(() => firstMessages.Reader.ReadAsync(TestContext.Current.CancellationToken)
            .AsTask().WaitAsync(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken));
        await Should.ThrowAsync<TimeoutException>(() => secondMessages.Reader.ReadAsync(TestContext.Current.CancellationToken)
            .AsTask().WaitAsync(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken));
    }

    private async Task<WebApplication> CreateApplicationAsync(string prefix)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Redis"] = redis.ConnectionString,
            ["SignalR:Redis:ChannelPrefix"] = prefix
        });
        builder.Services.AddSignalR(builder.Configuration);
        var app = builder.Build();
        app.MapHub<StreamingHub>("/hubs/events");
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static HubConnection CreateConnection(WebApplication app)
    {
        return new HubConnectionBuilder().WithUrl($"{app.Urls.Single()}/hubs/events").Build();
    }

    private static async Task<string> ReadMessageAsync(Channel<string> messages)
    {
        return await messages.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    public interface IStreamingClient
    {
        Task Received(string message);
    }

    public sealed class StreamingHub : Hub<IStreamingClient>
    {
        public Task Join(string group) => Groups.AddToGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
    }
}
