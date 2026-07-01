using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace LogicApp.Tests.E2E.Infrastructure;

/// <summary>
/// Microsoft Service Bus emulator + Azure SQL Edge backing store on a private Docker network.
/// Pre-declares EVERY queue the workflows under test send to (add them to the array in
/// InitializeAsync). Honours the SERVICEBUS_CONNECTION_STRING escape hatch; when that is set,
/// the target real namespace MUST already have those queues declared.
///
/// Emulator limitations: no sessions / scheduled messages / cross-entity transactions /
/// auto-forward. Check the emulator release notes before trusting a passing test for those.
/// </summary>
public sealed class ServiceBusEmulatorFixture : IAsyncDisposable
{
    private INetwork? _network;
    private IContainer? _sqlEdge;
    private IContainer? _sbEmulator;
    private string? _configMountFile;

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var escape = Environment.GetEnvironmentVariable("SERVICEBUS_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(escape))
        {
            ConnectionString = escape;
            return;
        }

        // Pre-declare every queue the workflows under test send to. The sample workflow
        // sends to "sample-queue"; add more names here as your workflows need them.
        _configMountFile = Path.Combine(Path.GetTempPath(), $"sb-emu-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(_configMountFile, BuildConfigJson(new[] { "sample-queue" }));

        _network = new NetworkBuilder().Build();
        await _network.CreateAsync();

        const string sqlPwd = "Pwd-12345-StrongEnough!";

        _sqlEdge = new ContainerBuilder()
            .WithImage("mcr.microsoft.com/azure-sql-edge:latest")
            .WithNetwork(_network)
            .WithNetworkAliases("sqledge")
            .WithEnvironment("ACCEPT_EULA", "Y")
            .WithEnvironment("MSSQL_SA_PASSWORD", sqlPwd)
            .Build();
        await _sqlEdge.StartAsync();

        _sbEmulator = new ContainerBuilder()
            .WithImage("mcr.microsoft.com/azure-messaging/servicebus-emulator:latest")
            .WithNetwork(_network)
            .WithPortBinding(5672, true)
            .WithEnvironment("ACCEPT_EULA", "Y")
            .WithEnvironment("SQL_SERVER", "sqledge")
            .WithEnvironment("MSSQL_SA_PASSWORD", sqlPwd)
            .WithBindMount(_configMountFile, "/ServiceBus_Emulator/ConfigFiles/Config.json")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("Emulator Service is Successfully Up"))
            .Build();
        await _sbEmulator.StartAsync();

        var amqpPort = _sbEmulator.GetMappedPublicPort(5672);
        ConnectionString =
            $"Endpoint=sb://localhost:{amqpPort};" +
            "SharedAccessKeyName=RootManageSharedAccessKey;" +
            "SharedAccessKey=SAS_KEY_VALUE;" +
            "UseDevelopmentEmulator=true";
    }

    private static string BuildConfigJson(IEnumerable<string> queueNames)
    {
        var queues = string.Join(",\n            ", queueNames.Select(q => $@"{{ ""Name"": ""{q}"" }}"));
        return $@"{{
  ""UserConfig"": {{
    ""Namespaces"": [
      {{
        ""Name"": ""sbemulatorns"",
        ""Queues"": [
            {queues}
        ],
        ""Topics"": []
      }}
    ],
    ""Logging"": {{ ""Type"": ""File"" }}
  }}
}}";
    }

    public async ValueTask DisposeAsync()
    {
        if (_sbEmulator is not null) await _sbEmulator.DisposeAsync();
        if (_sqlEdge is not null) await _sqlEdge.DisposeAsync();
        if (_network is not null) await _network.DisposeAsync();
        if (_configMountFile is not null && File.Exists(_configMountFile))
        {
            try { File.Delete(_configMountFile); } catch { }
        }
    }
}
