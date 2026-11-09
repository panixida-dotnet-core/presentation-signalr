using System.Buffers;

using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using PANiXiDA.Core.Presentation.SignalR.DependencyInjection;

using PolyType;

namespace PANiXiDA.Core.Presentation.SignalR.UnitTests.Configurations;

public sealed partial class MessagePackConfigurationTests
{
    [Theory(DisplayName = "Core registers JSON and binary MessagePack with and without Redis")]
    [InlineData(false)]
    [InlineData(true)]
    public void AddSignalR_ShouldRegisterBothProtocols(bool useRedis)
    {
        var services = new ServiceCollection();
        var settings = new Dictionary<string, string?>();
        if (useRedis)
        {
            settings["SignalR:Redis:ChannelPrefix"] = "tests";
            settings["ConnectionStrings:Redis"] = "localhost:6379";
        }

        services.AddSignalR(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        using var provider = services.BuildServiceProvider();
        var protocols = provider.GetServices<IHubProtocol>().ToArray();

        protocols.Select(protocol => protocol.Name).Order().ShouldBe(["json", "messagepack"]);
        var protocol = protocols.Single(protocol => protocol.Name == "messagepack");
        protocol.TransferFormat.ShouldBe(Microsoft.AspNetCore.Connections.TransferFormat.Binary);
        ReadResult(protocol, 42).ReadInt32().ShouldBe(42);
    }

    [Theory(DisplayName = "Application shapes and serializer options control MessagePack enum contracts")]
    [InlineData(false)]
    [InlineData(true)]
    public void AddSignalR_ShouldUseApplicationContracts(bool customSerializer)
    {
        var services = new ServiceCollection();
        var serializer = customSerializer ? new Nerdbank.MessagePack.MessagePackSerializer { SerializeEnumValuesByName = false } : null;
        services.AddSignalR(new ConfigurationBuilder().Build(), ProtocolTypes.GeneratedTypeShapeProvider, serializer);
        using var provider = services.BuildServiceProvider();
        var protocol = provider.GetServices<IHubProtocol>().Single(protocol => protocol.Name == "messagepack");

        var reader = ReadResult(protocol, ProtocolState.Ready);

        if (customSerializer) { reader.ReadInt32().ShouldBe(1); }
        else { reader.ReadString().ShouldBe("Ready"); }
    }

    [Fact(DisplayName = "Missing MessagePack shapes fail without falling back to reflection")]
    public void AddSignalR_ShouldRejectUnknownContracts()
    {
        var services = new ServiceCollection();
        services.AddSignalR(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        var protocol = provider.GetServices<IHubProtocol>().Single(protocol => protocol.Name == "messagepack");

        Should.Throw<InvalidDataException>(() => protocol.GetMessageBytes(CompletionMessage.WithResult("1", new UnknownContract("Melee"))));
    }

    [Fact(DisplayName = "MessagePack registration rejects a null application type provider")]
    public void AddSignalR_ShouldRejectNullProvider()
    {
        var services = new ServiceCollection();

        var exception = Should.Throw<ArgumentNullException>(() => services.AddSignalR(new ConfigurationBuilder().Build(), null!));

        exception.ParamName.ShouldBe("messagePackTypes");
    }

    private static MessagePack.MessagePackReader ReadResult(IHubProtocol protocol, object value)
    {
        var frame = protocol.GetMessageBytes(CompletionMessage.WithResult("1", value));
        var reader = new MessagePack.MessagePackReader(new ReadOnlySequence<byte>(frame[1..]));
        reader.ReadArrayHeader().ShouldBe(5);
        reader.ReadInt32().ShouldBe(3);
        reader.ReadMapHeader().ShouldBe(0);
        reader.ReadString().ShouldBe("1");
        reader.ReadInt32().ShouldBe(3);
        return reader;
    }

    public enum ProtocolState { Ready = 1 }

    private sealed record UnknownContract(string Name);

    [GenerateShapeFor<ProtocolState>]
    private partial class ProtocolTypes;
}
