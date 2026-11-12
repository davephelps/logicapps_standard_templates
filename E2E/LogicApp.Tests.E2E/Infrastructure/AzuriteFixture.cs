using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace LogicApp.Tests.E2E.Infrastructure;

/// <summary>
/// Azurite emulator. Honours AZURE_STORAGE_CONNECTION_STRING escape hatch (logicapp-local-run
/// skill section 7).
/// </summary>
public sealed class AzuriteFixture : IAsyncDisposable
{
    /// <summary>
    /// Image is pinned to a tested default so runs stay repeatable; override with the
    /// AZURITE_IMAGE environment variable in CI/dev when a different tag is needed.
    /// </summary>
    private const string DefaultAzuriteImage = "mcr.microsoft.com/azure-storage/azurite:3.33.0";

    private static string AzuriteImage =>
        Environment.GetEnvironmentVariable("AZURITE_IMAGE") is { Length: > 0 } image
            ? image
            : DefaultAzuriteImage;

    private IContainer? _container;
    public int BlobPort { get; private set; }
    public int QueuePort { get; private set; }
    public int TablePort { get; private set; }
    public string BlobServiceEndpoint { get; private set; } = string.Empty;
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var escape = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(escape))
        {
            ConnectionString = escape;
            return;
        }

        // --skipApiVersionCheck: ExtensionBundle.Workflows 1.194.31+ sends Storage API version
        // 2025-11-05, which azurite:3.33.0 rejects unless API-version checking is bypassed locally.
        _container = new ContainerBuilder()
            .WithImage(AzuriteImage)
            .WithPortBinding(10000, true)
            .WithPortBinding(10001, true)
            .WithPortBinding(10002, true)
            .WithCommand("azurite", "--blobHost", "0.0.0.0", "--queueHost", "0.0.0.0", "--tableHost", "0.0.0.0", "--skipApiVersionCheck")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(10000))
            .Build();

        await _container.StartAsync();

        BlobPort = _container.GetMappedPublicPort(10000);
        QueuePort = _container.GetMappedPublicPort(10001);
        TablePort = _container.GetMappedPublicPort(10002);
        BlobServiceEndpoint = $"http://127.0.0.1:{BlobPort}/devstoreaccount1";

        ConnectionString =
            "DefaultEndpointsProtocol=http;" +
            "AccountName=devstoreaccount1;" +
            "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;" +
            $"BlobEndpoint=http://127.0.0.1:{BlobPort}/devstoreaccount1;" +
            $"QueueEndpoint=http://127.0.0.1:{QueuePort}/devstoreaccount1;" +
            $"TableEndpoint=http://127.0.0.1:{TablePort}/devstoreaccount1;";
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }
}
