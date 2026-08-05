using System.Net.Http;
using Microsoft.Extensions.Configuration;

namespace LogicApp.Tests.Integration.Infrastructure;

/// <summary>
/// Per-scenario state container injected by Reqnroll DI. Long-lived emulator fixtures
/// (Azurite, SB emulator, WireMock, func host) are owned by TestRunHooks and exposed
/// here as pass-through accessors.
/// </summary>
public sealed class TestEnvironment
{
    public IConfiguration Config { get; }
    public FuncHostFixture FuncHost => TestRunHooks.FuncHost!;
    public AzuriteFixture Azurite => TestRunHooks.Azurite!;
    public ServiceBusEmulatorFixture ServiceBusEmulator => TestRunHooks.ServiceBusEmulator!;
    public WireMockSetup WireMock => TestRunHooks.WireMock!;
    public ServiceBusHelper ServiceBus => TestRunHooks.ServiceBus!;
    public LogicAppTrigger Trigger => TestRunHooks.Trigger!;

    public HttpClient Http { get; } = new() { Timeout = TimeSpan.FromMinutes(3) };

    public HttpResponseMessage? LastResponse { get; set; }
    public string? LastResponseBody { get; set; }
    public List<Azure.Messaging.ServiceBus.ServiceBusReceivedMessage> LastMessages { get; } = new();

    /// <summary>Name of the Service Bus queue the sample workflow publishes to.</summary>
    public string SampleQueueName => Config["ServiceBus:SampleQueue"] ?? "sample-queue";

    public TestEnvironment()
    {
        Config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.test.json", optional: false)
            .Build();
    }
}
