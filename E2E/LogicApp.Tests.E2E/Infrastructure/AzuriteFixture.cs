using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace LogicApp.Tests.E2E.Infrastructure;

/// <summary>
/// Azurite emulator. Honours AZURE_STORAGE_CONNECTION_STRING escape hatch (logicapp-local-run
/// skill section 7).
/// </summary>
public sealed class AzuriteFixture : IAsyncDisposable
{
    private IContainer? _container;
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var escape = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(escape))
        {
            ConnectionString = escape;
            return;
        }

        _container = new ContainerBuilder()
            .WithImage("mcr.microsoft.com/azure-storage/azurite:latest")
            .WithPortBinding(10000, true)
            .WithPortBinding(10001, true)
            .WithPortBinding(10002, true)
            .WithCommand("azurite", "--blobHost", "0.0.0.0", "--queueHost", "0.0.0.0", "--tableHost", "0.0.0.0")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(10000))
            .Build();

        await _container.StartAsync();

        var blobPort = _container.GetMappedPublicPort(10000);
        var queuePort = _container.GetMappedPublicPort(10001);
        var tablePort = _container.GetMappedPublicPort(10002);

        ConnectionString =
            "DefaultEndpointsProtocol=http;" +
            "AccountName=devstoreaccount1;" +
            "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;" +
            $"BlobEndpoint=http://127.0.0.1:{blobPort}/devstoreaccount1;" +
            $"QueueEndpoint=http://127.0.0.1:{queuePort}/devstoreaccount1;" +
            $"TableEndpoint=http://127.0.0.1:{tablePort}/devstoreaccount1;";
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }
}
