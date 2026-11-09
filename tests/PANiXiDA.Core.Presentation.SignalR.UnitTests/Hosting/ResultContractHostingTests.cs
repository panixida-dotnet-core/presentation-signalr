using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using PANiXiDA.Core.Presentation.SignalR.DependencyInjection;
using PANiXiDA.Core.Presentation.SignalR.Helpers;
using PANiXiDA.Core.ResultPattern;

namespace PANiXiDA.Core.Presentation.SignalR.UnitTests.Hosting;

public sealed class ResultContractHostingTests
{
    [Fact(DisplayName = "Kestrel hub calls return plain values and void completions using only source-generated JSON metadata")]
    public async Task Hosting_ShouldReturnPlainValues()
    {
        await using var app = await CreateApplicationAsync();
        await using var connection = CreateConnection(app);
        var cancellationToken = TestContext.Current.CancellationToken;

        await connection.StartAsync(cancellationToken);
        await connection.InvokeAsync("Complete", false, cancellationToken);
        var zero = await connection.InvokeAsync<int>("Number", false, 0, cancellationToken);
        var boolean = await connection.InvokeAsync<bool>("Boolean", false, cancellationToken);
        var nullable = await connection.InvokeAsync<string?>("Nullable", cancellationToken);

        zero.ShouldBe(0);
        boolean.ShouldBeFalse();
        nullable.ShouldBeNull();
    }

    [Fact(DisplayName = "Kestrel failures use standard client HubExceptions and keep the connection usable")]
    public async Task Hosting_ShouldUseStandardHubErrors()
    {
        await using var app = await CreateApplicationAsync();
        await using var connection = CreateConnection(app);
        var cancellationToken = TestContext.Current.CancellationToken;
        await connection.StartAsync(cancellationToken);

        var validation = await Should.ThrowAsync<HubException>(() => connection.InvokeAsync("Complete", true, cancellationToken));
        var conflict = await Should.ThrowAsync<HubException>(() => connection.InvokeAsync<int>("Number", true, 42, cancellationToken));
        var unexpected = await Should.ThrowAsync<HubException>(() => connection.InvokeAsync("Unexpected", cancellationToken));

        validation.Message.ShouldContain("Name is required");
        validation.Message.ShouldNotContain("Internal secret");
        conflict.Message.ShouldContain("Conflict");
        unexpected.Message.ShouldContain("An unexpected error occurred.");
        unexpected.Message.ShouldNotContain("Internal secret");
        (await connection.InvokeAsync<int>("Number", false, 42, cancellationToken)).ShouldBe(42);
        connection.State.ShouldBe(HubConnectionState.Connected);
    }

    [Theory(DisplayName = "SignalR rejects invalid JSON contracts before invoking a hub and keeps the connection usable")]
    [InlineData("""{"count":1,"requiredNote":null}""")]
    [InlineData("""{"name":"Melee","requiredNote":null}""")]
    [InlineData("""{"name":"Melee","count":1}""")]
    [InlineData("""{"name":null,"count":1,"requiredNote":null}""")]
    [InlineData("""{"name":"Melee","count":1,"requiredNote":null,"description":null}""")]
    [InlineData("""{"name":"Melee","count":"1","requiredNote":null}""")]
    public async Task Hosting_ShouldRejectInvalidContracts(string payload)
    {
        await using var app = await CreateApplicationAsync();
        await using var connection = CreateConnection(app);
        var cancellationToken = TestContext.Current.CancellationToken;
        var counter = app.Services.GetRequiredService<InvocationCounter>();
        using var invalid = JsonDocument.Parse(payload);
        using var valid = JsonDocument.Parse("""{"name":"Melee","count":1,"requiredNote":null}""");
        await connection.StartAsync(cancellationToken);

        await Should.ThrowAsync<HubException>(() => connection.InvokeAsync<ContractResponse>("Submit", invalid.RootElement, cancellationToken));

        counter.Calls.ShouldBe(0);
        var response = await connection.InvokeAsync<ContractResponse>("Submit", valid.RootElement, cancellationToken);
        response.Name.ShouldBe("Melee");
        counter.Calls.ShouldBe(1);
        connection.State.ShouldBe(HubConnectionState.Connected);
    }

    [Theory(DisplayName = "SignalR accepts explicit nullable constructor values and omitted optional parameters")]
    [InlineData("""{"name":"Melee","count":0,"requiredNote":null}""")]
    [InlineData("""{"name":"Melee","count":0,"requiredNote":"note","description":"description"}""")]
    public async Task Hosting_ShouldAcceptValidContracts(string payload)
    {
        await using var app = await CreateApplicationAsync();
        await using var connection = CreateConnection(app);
        using var json = JsonDocument.Parse(payload);
        var cancellationToken = TestContext.Current.CancellationToken;
        await connection.StartAsync(cancellationToken);

        var response = await connection.InvokeAsync<ContractResponse>("Submit", json.RootElement, cancellationToken);

        response.Name.ShouldBe("Melee");
        app.Services.GetRequiredService<InvocationCounter>().Calls.ShouldBe(1);
    }

    internal static void UseGeneratedJson(JsonSerializerOptions options)
    {
        options.TypeInfoResolverChain.Clear();
        options.TypeInfoResolverChain.Add(ContractJsonContext.Default);
    }

    private static async Task<WebApplication> CreateApplicationAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<InvocationCounter>();
        builder.Services.AddSignalR(new ConfigurationBuilder().Build())
            .AddJsonProtocol(options => UseGeneratedJson(options.PayloadSerializerOptions));
        var app = builder.Build();
        app.UseSignalR(hubs => hubs.MapHub<ContractHub>("/contracts"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static HubConnection CreateConnection(WebApplication app)
    {
        return new HubConnectionBuilder().WithUrl($"{app.Urls.Single()}/contracts", options =>
        {
            options.Transports = HttpTransportType.WebSockets;
            options.SkipNegotiation = true;
            options.WebSocketConfiguration = socket => socket.Proxy = null;
        }).AddJsonProtocol(options => UseGeneratedJson(options.PayloadSerializerOptions)).Build();
    }

    public sealed class ContractHub(InvocationCounter counter) : Hub
    {
        public void Complete(bool fail)
        {
            Context.ConnectionAborted.ThrowIfCancellationRequested();
            var result = fail
                ? Result.Failure([
                    Error.Validation("Name is required").WithField("Name"),
                    Error.NotFound("Missing"),
                    Error.Unexpected("Internal secret").WithMetadata("secret", new object())])
                : Result.Success();
            result.ThrowIfFailure();
        }

        public int Number(bool fail, int value)
        {
            Context.ConnectionAborted.ThrowIfCancellationRequested();
            var result = fail ? Result.Failure<int>(Error.Conflict("Conflict")) : Result.Success(value);
            return result.GetValueOrThrow();
        }

        public bool Boolean(bool value)
        {
            Context.ConnectionAborted.ThrowIfCancellationRequested();
            return Result.Success(value).GetValueOrThrow();
        }

        public string? Nullable()
        {
            Context.ConnectionAborted.ThrowIfCancellationRequested();
            return Result.Success<string?>(null).GetValueOrThrow();
        }

        public void Unexpected()
        {
            Context.ConnectionAborted.ThrowIfCancellationRequested();
            Result.Failure(Error.Unexpected("Internal secret").WithMetadata("secret", new object())).ThrowIfFailure();
        }

        public ContractResponse Submit(ContractRequest request)
        {
            Context.ConnectionAborted.ThrowIfCancellationRequested();
            counter.Calls++;
            return Result.Success(new ContractResponse(request.Name)).GetValueOrThrow();
        }
    }

    public sealed class InvocationCounter
    {
        public int Calls { get; set; }
    }
}

public sealed record ContractRequest(string Name, int Count, string? RequiredNote, string Description = "optional");
public sealed record ContractResponse(string Name);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(ContractResponse))]
[JsonSerializable(typeof(ContractRequest))]
[JsonSerializable(typeof(JsonElement))]
internal partial class ContractJsonContext : JsonSerializerContext;
