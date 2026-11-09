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
using PANiXiDA.Core.Presentation.SignalR.Results;
using PANiXiDA.Core.ResultPattern;

namespace PANiXiDA.Core.Presentation.SignalR.UnitTests.Hosting;

public sealed class ResultContractHostingTests
{
    [Fact(DisplayName = "Kestrel hub calls round-trip structured results using only source-generated JSON metadata")]
    public async Task Hosting_ShouldRoundTripResults()
    {
        await using var app = await CreateApplicationAsync();
        await using var connection = CreateConnection(app);
        var cancellationToken = TestContext.Current.CancellationToken;

        await connection.StartAsync(cancellationToken);
        var success = await connection.InvokeAsync<SignalRResult>("Complete", false, cancellationToken);
        var failure = await connection.InvokeAsync<SignalRResult>("Complete", true, cancellationToken);
        var zero = await connection.InvokeAsync<SignalRResult<int>>("Number", false, 0, cancellationToken);
        var failedNumber = await connection.InvokeAsync<SignalRResult<int>>("Number", true, 42, cancellationToken);
        var boolean = await connection.InvokeAsync<SignalRResult<bool>>("Boolean", false, cancellationToken);
        var nullable = await connection.InvokeAsync<SignalRResult<string?>>("Nullable", cancellationToken);

        success.IsSuccess.ShouldBeTrue();
        success.Errors.ShouldBeEmpty();
        failure.IsSuccess.ShouldBeFalse();
        failure.Errors.Select(error => error.Type).ShouldBe(["Validation", "NotFound", "Unexpected"]);
        failure.Errors[0].Field.ShouldBe("Name");
        failure.Errors[2].Message.ShouldBe("An unexpected error occurred.");
        zero.IsSuccess.ShouldBeTrue();
        zero.Value.ShouldBe(0);
        failedNumber.IsSuccess.ShouldBeFalse();
        failedNumber.Value.ShouldBe(0);
        failedNumber.Errors.ShouldHaveSingleItem().Type.ShouldBe("Conflict");
        boolean.IsSuccess.ShouldBeTrue();
        boolean.Value.ShouldBeFalse();
        nullable.IsSuccess.ShouldBeTrue();
        nullable.Value.ShouldBeNull();
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

        await Should.ThrowAsync<HubException>(() => connection.InvokeAsync<SignalRResult<ContractResponse>>("Submit", invalid.RootElement, cancellationToken));

        counter.Calls.ShouldBe(0);
        var response = await connection.InvokeAsync<SignalRResult<ContractResponse>>("Submit", valid.RootElement, cancellationToken);
        response.IsSuccess.ShouldBeTrue();
        response.Value!.Name.ShouldBe("Melee");
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

        var response = await connection.InvokeAsync<SignalRResult<ContractResponse>>("Submit", json.RootElement, cancellationToken);

        response.IsSuccess.ShouldBeTrue();
        response.Value!.Name.ShouldBe("Melee");
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
        public SignalRResult Complete(bool fail)
        {
            Context.ConnectionAborted.ThrowIfCancellationRequested();
            return fail
                ? Result.Failure([
                    Error.Validation("Name is required").WithField("Name"),
                    Error.NotFound("Missing"),
                    Error.Unexpected("Internal secret").WithMetadata("secret", new object())]).ToSignalRResult()
                : Result.Success().ToSignalRResult();
        }

        public SignalRResult<int> Number(bool fail, int value)
        {
            Context.ConnectionAborted.ThrowIfCancellationRequested();
            return fail ? Result.Failure<int>(Error.Conflict("Conflict")).ToSignalRResult() : Result.Success(value).ToSignalRResult();
        }

        public SignalRResult<bool> Boolean(bool value)
        {
            Context.ConnectionAborted.ThrowIfCancellationRequested();
            return Result.Success(value).ToSignalRResult();
        }

        public SignalRResult<string?> Nullable()
        {
            Context.ConnectionAborted.ThrowIfCancellationRequested();
            return Result.Success<string?>(null).ToSignalRResult();
        }

        public SignalRResult<ContractResponse> Submit(ContractRequest request)
        {
            Context.ConnectionAborted.ThrowIfCancellationRequested();
            counter.Calls++;
            return Result.Success(new ContractResponse(request.Name)).ToSignalRResult();
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
[JsonSerializable(typeof(SignalRResult))]
[JsonSerializable(typeof(SignalRResult<int>))]
[JsonSerializable(typeof(SignalRResult<bool>))]
[JsonSerializable(typeof(SignalRResult<string>))]
[JsonSerializable(typeof(SignalRResult<ContractResponse>))]
[JsonSerializable(typeof(ContractRequest))]
[JsonSerializable(typeof(JsonElement))]
internal partial class ContractJsonContext : JsonSerializerContext;
