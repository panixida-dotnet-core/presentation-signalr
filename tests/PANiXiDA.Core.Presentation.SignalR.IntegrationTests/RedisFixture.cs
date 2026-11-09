using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace PANiXiDA.Core.Presentation.SignalR.IntegrationTests;

public sealed class RedisFixture : IAsyncLifetime
{
    private readonly IContainer _container = new ContainerBuilder("redis:7.4-alpine")
        .WithPortBinding(6379, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections"))
        .Build();

    public string ConnectionString => $"{_container.Hostname}:{_container.GetMappedPublicPort(6379)}";

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}
