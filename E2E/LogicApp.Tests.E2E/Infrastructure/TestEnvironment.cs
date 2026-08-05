using System.Net.Http;
using Microsoft.Extensions.Configuration;

namespace LogicApp.Tests.E2E.Infrastructure;

/// <summary>
/// Per-scenario state container injected by Reqnroll DI. The long-lived fixtures (func host,
/// emulators, WireMock, APIM emulator) are owned by <see cref="TestRunHooks"/> and exposed here
/// as pass-through accessors.
/// </summary>
public sealed class TestEnvironment
{
    public IConfiguration Config { get; }
    public FuncHostFixture FuncHost => TestRunHooks.FuncHost!;
    public WireMockSetup WireMock => TestRunHooks.WireMock!;
    public ServiceBusHelper ServiceBus => TestRunHooks.ServiceBus!;
    public LogicAppTrigger Trigger => TestRunHooks.Trigger!;
    public ApimEmulator Apim => TestRunHooks.Apim!;
    public APIMClient ApimClient => TestRunHooks.ApimClient!;

    public HttpResponseMessage? LastResponse { get; set; }
    public string? LastResponseBody { get; set; }
    public List<Azure.Messaging.ServiceBus.ServiceBusReceivedMessage> LastMessages { get; } = new();

    public string SampleQueueName => Config["ServiceBus:SampleQueue"] ?? "sample-queue";

    public TestEnvironment()
    {
        Config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.test.json", optional: false)
            .Build();
    }
}
