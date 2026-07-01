using Reqnroll;
using Reqnroll.BoDi;
using LogicApp.Tests.E2E.Infrastructure;

namespace LogicApp.Tests.E2E.Hooks;

/// <summary>
/// Per-scenario Reqnroll lifecycle. Registers the TestEnvironment for constructor injection,
/// (re)loads static WireMock backend stubs, and purges the sample Service Bus queue before each
/// scenario. The heavy fixtures live in <see cref="TestRunHooks"/> (test-run scoped).
/// </summary>
[Binding]
public sealed class E2E_Hooks
{
    private readonly TestEnvironment _env;

    public E2E_Hooks(TestEnvironment env) => _env = env;

    [BeforeTestRun]
    public static void RegisterTestEnvironment(IObjectContainer container)
        => container.RegisterTypeAs<TestEnvironment, TestEnvironment>();

    [BeforeScenario(Order = 10)]
    public void StartBackendWireMock()
    {
        _env.WireMock.EnsureStarted();
        _env.WireMock.LoadStubs(Path.Combine(AppContext.BaseDirectory, "__mocks"));
    }

    [BeforeScenario(Order = 20)]
    public async Task PurgeQueues()
        => await _env.ServiceBus.PurgeQueue(_env.SampleQueueName);

    [AfterScenario]
    public void ResetWireMock() => _env.WireMock.Reset();
}
