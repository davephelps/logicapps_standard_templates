// WorkflowHost_Hooks -- Reqnroll lifecycle for the Logic App Standard host
// and the in-process WireMock + Service Bus stubs.
//
// The Logic Apps Standard host (`func host start`) is expected to be started OUTSIDE
// this process (CI step or developer terminal) before running the test suite. These
// hooks therefore:
//
//   - register a TestEnvironment in the Reqnroll DI container so step definitions can
//     resolve it via constructor injection (one instance per scenario);
//   - start a fresh WireMock server before each scenario;
//   - load the static __mocks/ stub files into WireMock;
//   - purge the sample Service Bus queue before each scenario when the connection is configured;
//   - reset WireMock stubs after each scenario.

using Reqnroll;
using Reqnroll.BoDi;
using LogicApp.Tests.Integration.Infrastructure;

namespace LogicApp.Tests.Integration.Hooks;

[Binding]
public sealed class WorkflowHost_Hooks
{
    private readonly TestEnvironment _env;

    public WorkflowHost_Hooks(TestEnvironment env)
    {
        _env = env;
    }

    [BeforeTestRun]
    public static void RegisterTestEnvironment(IObjectContainer container)
    {
        // Register TestEnvironment as a scenario-scoped singleton. Reqnroll resolves
        // this via constructor injection on the [Binding] classes below.
        container.RegisterTypeAs<TestEnvironment, TestEnvironment>();
    }

    [BeforeScenario(Order = 10)]
    public void StartWireMock()
    {
        _env.WireMock.EnsureStarted();
        var mocksFolder = Path.Combine(AppContext.BaseDirectory, "__mocks");
        _env.WireMock.LoadStubs(mocksFolder);
    }

    [BeforeScenario(Order = 20)]
    public async Task PurgeOperationalQueues()
    {
        if (_env.ServiceBus is null) return;
        await _env.ServiceBus.PurgeQueue(_env.SampleQueueName);
    }

    [AfterScenario]
    public void StopWireMock()
    {
        // WireMock lifetime is test-run scoped (owned by TestRunHooks). Only reset stubs
        // between scenarios -- disposing here would invalidate the BaseUrl that was
        // baked into local.settings.json at BeforeTestRun.
        _env.WireMock.Reset();
    }
}
