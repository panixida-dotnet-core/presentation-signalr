using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using PANiXiDA.Core.Presentation.SignalR.DependencyInjection;
using PANiXiDA.Core.Presentation.SignalR.Helpers;
using PANiXiDA.Core.Presentation.SignalR.Results;
using PANiXiDA.Core.Presentation.SignalR.UnitTests.Hosting;
using PANiXiDA.Core.ResultPattern;

namespace PANiXiDA.Core.Presentation.SignalR.UnitTests.Configurations;

public sealed class JsonConfigurationTests
{
    [Theory(DisplayName = "SignalR registration applies strict JSON settings with and without Redis")]
    [InlineData(false)]
    [InlineData(true)]
    public void AddSignalR_ShouldConfigureStrictContracts(bool useRedis)
    {
        var services = new ServiceCollection();
        var settings = new Dictionary<string, string?>();
        if (useRedis)
        {
            settings["SignalR:Redis:ChannelPrefix"] = "tests";
            settings["ConnectionStrings:Redis"] = "localhost:6379";
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        services.Configure<JsonHubProtocolOptions>(options =>
        {
            ResultContractHostingTests.UseGeneratedJson(options.PayloadSerializerOptions);
            options.PayloadSerializerOptions.DefaultBufferSize = 4096;
        });

        services.AddSignalR(configuration);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value.PayloadSerializerOptions;

        options.NumberHandling.ShouldBe(JsonNumberHandling.Strict);
        options.RespectRequiredConstructorParameters.ShouldBeTrue();
        options.RespectNullableAnnotations.ShouldBeTrue();
        options.DefaultBufferSize.ShouldBe(4096);
        options.TypeInfoResolverChain.ShouldHaveSingleItem().ShouldBeSameAs(ContractJsonContext.Default);
    }

    [Fact(DisplayName = "A host can customize JSON protocol settings after Core registration")]
    public void AddSignalR_ShouldPreserveHostCustomization()
    {
        var services = new ServiceCollection();
        services.AddSignalR(new ConfigurationBuilder().Build()).AddJsonProtocol(options =>
            options.PayloadSerializerOptions.NumberHandling = JsonNumberHandling.AllowReadingFromString);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value.PayloadSerializerOptions;

        options.NumberHandling.ShouldBe(JsonNumberHandling.AllowReadingFromString);
        options.RespectNullableAnnotations.ShouldBeTrue();
    }

    [Fact(DisplayName = "Source-generated result JSON has a stable structure and does not expose Result internals or metadata")]
    public void Serialize_ShouldWriteSafeErrors()
    {
        var options = CreateOptions();
        var response = Result.Failure<ContractResponse>([
            Error.Validation("Invalid").WithField("Name"),
            Error.Unexpected("Internal secret").WithMetadata("secret", new object())]).ToSignalRResult();

        var json = JsonSerializer.Serialize(response, options.GetTypeInfo(typeof(SignalRResult<ContractResponse>)));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        root.EnumerateObject().Select(property => property.Name).ShouldBe(["isSuccess", "value", "errors"]);
        root.GetProperty("isSuccess").GetBoolean().ShouldBeFalse();
        root.GetProperty("value").ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("errors")[0].GetProperty("field").GetString().ShouldBe("Name");
        json.ShouldNotContain("Internal secret");
        json.ShouldNotContain("metadata", Case.Insensitive);
        json.ShouldNotContain("firstError", Case.Insensitive);
        json.ShouldNotContain("valueOrDefault", Case.Insensitive);
    }

    [Fact(DisplayName = "Strict source-generated JSON rejects null non-nullable response properties")]
    public void Serialize_ShouldRejectInvalidResponse()
    {
        var options = CreateOptions();
        var response = Result.Success(new ContractResponse(null!)).ToSignalRResult();

        Should.Throw<JsonException>(() => JsonSerializer.Serialize(response, options.GetTypeInfo(typeof(SignalRResult<ContractResponse>))));
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var services = new ServiceCollection();
        services.AddSignalR(new ConfigurationBuilder().Build()).AddJsonProtocol(options =>
            ResultContractHostingTests.UseGeneratedJson(options.PayloadSerializerOptions));
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value.PayloadSerializerOptions;
    }
}
