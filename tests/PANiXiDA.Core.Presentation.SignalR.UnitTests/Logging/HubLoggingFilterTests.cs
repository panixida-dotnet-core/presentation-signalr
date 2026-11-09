using System.Collections.Concurrent;
using System.Security.Claims;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using PANiXiDA.Core.Presentation.SignalR.DependencyInjection;
using PANiXiDA.Core.Presentation.SignalR.Logging;

namespace PANiXiDA.Core.Presentation.SignalR.UnitTests.Logging;

public sealed class HubLoggingFilterTests
{
    [Theory(DisplayName = "Hub filters preserve results and snapshot operation scopes without logging payloads")]
    [InlineData("Echo")]
    [InlineData("OnConnectedAsync")]
    [InlineData("OnDisconnectedAsync")]
    public async Task Filter_ShouldLogSuccessfulOperations(string method)
    {
        using var provider = new RecordingLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var filter = new HubLoggingFilter(factory.CreateLogger<HubLoggingFilter>());
        var context = new TestCallerContext();
        const string secret = "private-payload";

        var result = await ExecuteAsync(filter, context, method, () =>
        {
            factory.CreateLogger("HubProbe").LogInformation("Inside hub");
            return ValueTask.FromResult<object?>(secret);
        });

        result.ShouldBe(method == "Echo" ? secret : null);
        var entries = provider.Records.ToArray();
        entries.Length.ShouldBe(2);
        foreach (var entry in entries)
        {
            entry.Attributes["rpc.system"].ShouldBe("signalr");
            entry.Attributes["rpc.service"].ShouldBe(typeof(LoggingHub).FullName);
            entry.Attributes["rpc.method"].ShouldBe(method);
            entry.Attributes["signalr.connection.id"].ShouldBe("connection-1");
            entry.Attributes["enduser.id"].ShouldBe("user-1");
            entry.Message.ShouldNotContain(secret);
            entry.Attributes.Values.ShouldNotContain(secret);
            entry.Exception.ShouldBeNull();
        }
        var completion = entries.Single(entry => entry.Message == "SignalR operation finished");
        completion.Level.ShouldBe(LogLevel.Information);
        completion.Attributes["signalr.operation.outcome"].ShouldBe("succeeded");
        ((double)completion.Attributes["signalr.operation.duration_ms"]!).ShouldBeGreaterThanOrEqualTo(0);
        entries[0].Attributes.ShouldNotContainKey("signalr.operation.outcome");
    }

    [Theory(DisplayName = "Hub filters preserve original exceptions and distinguish connection cancellation from failures")]
    [InlineData("Echo", false, false, "failed")]
    [InlineData("Echo", true, false, "failed")]
    [InlineData("Echo", true, true, "cancelled")]
    [InlineData("OnConnectedAsync", false, false, "failed")]
    [InlineData("OnConnectedAsync", true, true, "cancelled")]
    [InlineData("OnDisconnectedAsync", false, false, "failed")]
    [InlineData("OnDisconnectedAsync", true, true, "cancelled")]
    public async Task Filter_ShouldPreserveExceptions(string method, bool cancellation, bool aborted, string outcome)
    {
        using var provider = new RecordingLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var filter = new HubLoggingFilter(factory.CreateLogger<HubLoggingFilter>());
        var context = new TestCallerContext(aborted);
        Exception failure = cancellation ? new OperationCanceledException(context.ConnectionAborted) : new InvalidOperationException("Expected failure");

        Exception? actual = null;
        try
        {
            await ExecuteAsync(filter, context, method, () => ValueTask.FromException<object?>(failure));
        }
        catch (Exception exception)
        {
            actual = exception;
        }

        actual.ShouldBeSameAs(failure);
        var completion = provider.Records.ShouldHaveSingleItem();
        completion.Attributes["signalr.operation.outcome"].ShouldBe(outcome);
        completion.Exception.ShouldBeNull();
        completion.Level.ShouldBe(LogLevel.Information);
    }

    [Fact(DisplayName = "Disconnect failures are passed unchanged to the callback and included in the completion outcome")]
    public async Task Filter_ShouldPreserveDisconnectFailure()
    {
        using var provider = new RecordingLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var filter = new HubLoggingFilter(factory.CreateLogger<HubLoggingFilter>());
        var failure = new IOException("Transport closed");

        await ExecuteAsync(filter, new TestCallerContext(), "OnDisconnectedAsync", () => ValueTask.FromResult<object?>(null), failure);

        provider.Records.ShouldHaveSingleItem().Attributes["signalr.operation.outcome"].ShouldBe("failed");
    }

    [Fact(DisplayName = "Disabled completion logs retain scopes for application warnings and support anonymous connections")]
    public async Task Filter_ShouldRespectLoggingLevels()
    {
        using var provider = new RecordingLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning).AddProvider(provider));
        var filter = new HubLoggingFilter(factory.CreateLogger<HubLoggingFilter>());

        await ExecuteAsync(filter, new TestCallerContext(userId: null), "Echo", () =>
        {
            factory.CreateLogger("HubProbe").LogWarning("Application warning");
            return ValueTask.FromResult<object?>(null);
        });

        var entry = provider.Records.ShouldHaveSingleItem();
        entry.Message.ShouldBe("Application warning");
        entry.Attributes["enduser.id"].ShouldBeNull();
        entry.Attributes["rpc.method"].ShouldBe("Echo");
    }

    [Fact(DisplayName = "Core registration logs real Kestrel hub operations while framework exceptions are logged only once")]
    public async Task AddSignalR_ShouldLogHostedOperations()
    {
        using var provider = new RecordingLoggerProvider();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(provider);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSignalR(new ConfigurationBuilder().Build());
        await using var app = builder.Build();
        app.UseSignalR(hubs => hubs.MapHub<LoggingHub>("/logging"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        await using var connection = new HubConnectionBuilder().WithUrl($"{app.Urls.Single()}/logging", options =>
        {
            options.Transports = HttpTransportType.WebSockets;
            options.SkipNegotiation = true;
            options.WebSocketConfiguration = socket => socket.Proxy = null;
        }).Build();

        await connection.StartAsync(TestContext.Current.CancellationToken);
        (await connection.InvokeAsync<string>("Echo", "private-payload", TestContext.Current.CancellationToken)).ShouldBe("private-payload");
        await Should.ThrowAsync<HubException>(() => connection.InvokeAsync("Fail", TestContext.Current.CancellationToken));
        await connection.StopAsync(TestContext.Current.CancellationToken);
        await provider.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var completions = provider.Records.Where(entry => entry.Message == "SignalR operation finished").ToArray();
        completions.Length.ShouldBe(4);
        completions.Select(entry => entry.Attributes["rpc.method"]).ShouldBe(["OnConnectedAsync", "Echo", "Fail", "OnDisconnectedAsync"]);
        completions.Single(entry => Equals(entry.Attributes["rpc.method"], "Fail")).Attributes["signalr.operation.outcome"].ShouldBe("failed");
        provider.Records.Count(entry => entry.Level >= LogLevel.Error).ShouldBe(1);
        var probe = provider.Records.Single(entry => entry.Message == "Echo called");
        probe.Attributes["rpc.method"].ShouldBe("Echo");
        probe.Attributes["signalr.connection.id"].ShouldNotBeNull();
        probe.Attributes.Values.ShouldNotContain("private-payload");
    }

    private static async Task<object?> ExecuteAsync(
        HubLoggingFilter filter, TestCallerContext context, string method, Func<ValueTask<object?>> next, Exception? disconnectException = null)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var hub = new LoggingHub(NullLogger<LoggingHub>.Instance);
        var lifetime = new HubLifetimeContext(context, services, hub);
        if (method == "OnConnectedAsync")
        {
            await filter.OnConnectedAsync(lifetime, _ => next().AsTask());
            return null;
        }
        if (method == "OnDisconnectedAsync")
        {
            await filter.OnDisconnectedAsync(lifetime, disconnectException, (_, exception) =>
            {
                exception.ShouldBeSameAs(disconnectException);
                return next().AsTask();
            });
            return null;
        }
        var invocation = new HubInvocationContext(context, services, hub, typeof(LoggingHub).GetMethod(nameof(LoggingHub.Echo))!, ["private-payload"]);
        return await filter.InvokeMethodAsync(invocation, _ => next());
    }

    public sealed class LoggingHub(ILogger<LoggingHub> logger) : Hub
    {
        public string Echo(string payload)
        {
            logger.LogInformation("Echo called");
            return payload;
        }

        public void Fail() => throw new InvalidOperationException("Expected hub failure");
    }

    private sealed class TestCallerContext(bool aborted = false, string? userId = "user-1") : HubCallerContext
    {
        public override string ConnectionId => "connection-1";
        public override string? UserIdentifier => userId;
        public override ClaimsPrincipal User { get; } = new();
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => new(aborted);
        public override void Abort() { }
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
        public ConcurrentQueue<LogEntry> Records { get; } = new();
        public TaskCompletionSource Disconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this);
        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
        public void Dispose() { }

        private sealed class RecordingLogger(RecordingLoggerProvider provider) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => provider._scopes.Push(state);
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var attributes = new Dictionary<string, object?>();
                provider._scopes.ForEachScope((scope, values) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                    {
                        foreach (var pair in pairs) { values[pair.Key] = pair.Value; }
                    }
                }, attributes);
                var entry = new LogEntry(logLevel, formatter(state, exception), exception, attributes);
                provider.Records.Enqueue(entry);
                if (entry.Message == "SignalR operation finished" && Equals(attributes["rpc.method"], "OnDisconnectedAsync"))
                {
                    provider.Disconnected.TrySetResult();
                }
            }
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception, Dictionary<string, object?> Attributes);
}
