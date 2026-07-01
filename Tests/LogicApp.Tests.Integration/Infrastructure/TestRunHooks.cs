using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using Reqnroll;

namespace LogicApp.Tests.Integration.Infrastructure;

/// <summary>
/// Test-run lifecycle. Brings up the emulator stack + WireMock + func host once, tears
/// down at the end. Patches the Logic App's local.settings.json on disk in [BeforeTestRun] to:
///   - point serviceBus_connectionString at the Service Bus emulator
///   - point externalApiUrl at the WireMock base URL (external HTTP stub)
///   - point AzureWebJobsStorage at the Azurite emulator
///   - force FUNCTIONS_WORKER_RUNTIME=dotnet + FUNCTIONS_INPROC_NET8_ENABLED=1
/// and restores the original file in [AfterTestRun].
///
/// This is the reusable harness. Add product-specific app-setting patches (auth headers,
/// extra endpoint URLs) to the block in BeforeAll and matching keys in appsettings.test.json.
///
/// Per-scenario state cleanup (purge the sample queue, reset WireMock) is in CommonSteps.
/// </summary>
[Binding]
public sealed class TestRunHooks
{
    public static FuncHostFixture? FuncHost { get; private set; }
    public static AzuriteFixture? Azurite { get; private set; }
    public static ServiceBusEmulatorFixture? ServiceBusEmulator { get; private set; }
    public static WireMockSetup? WireMock { get; private set; }
    public static ServiceBusHelper? ServiceBus { get; private set; }
    public static LogicAppTrigger? Trigger { get; private set; }

    private static string? _originalLocalSettings;
    private static string? _localSettingsPath;

    [BeforeTestRun(Order = 0)]
    public static async Task BeforeAll()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.test.json", optional: false)
            .Build();

        Azurite = new AzuriteFixture();
        ServiceBusEmulator = new ServiceBusEmulatorFixture();
        WireMock = new WireMockSetup();

        // Emulator stack first -- func host reads connection strings from local.settings.json.
        await Task.WhenAll(
            Azurite.InitializeAsync(),
            ServiceBusEmulator.InitializeAsync(),
            WireMock.InitializeAsync());

        var projectRelative = config["LogicApp:ProjectRelativePath"] ?? "../../../../../LogicApp_Template";
        var startupTimeout = int.Parse(config["LogicApp:StartupTimeoutSeconds"] ?? "90");

        // Patch local.settings.json on disk before booting the host. Env-var overrides are
        // unreliable -- the func CLI reloads local.settings.json over the inherited env.
        var logicAppRoot = FuncHostFixture.LocateLogicAppRoot(projectRelative);
        _localSettingsPath = Path.Combine(logicAppRoot, "local.settings.json");
        if (File.Exists(_localSettingsPath))
        {
            _originalLocalSettings = File.ReadAllText(_localSettingsPath);
            var doc = JObject.Parse(_originalLocalSettings);
            var values = (JObject)(doc["Values"] ?? new JObject());
            values["FUNCTIONS_WORKER_RUNTIME"] = "dotnet";
            values["FUNCTIONS_INPROC_NET8_ENABLED"] = "1";
            values["AzureWebJobsStorage"] = Azurite.ConnectionString;
            values["serviceBus_connectionString"] = ServiceBusEmulator.ConnectionString;
            values["externalApiUrl"] = WireMock.BaseUrl.TrimEnd('/') + (config["External:SamplePath"] ?? "/external/api");
            // Add further product-specific app-setting patches here, e.g.:
            //   values["myAuthHeader"] = "test-token";
            //   values["anotherServiceUrl"] = WireMock.BaseUrl.TrimEnd('/') + "/another";
            doc["Values"] = values;
            File.WriteAllText(_localSettingsPath, doc.ToString());
        }

        FuncHost = new FuncHostFixture();
        await FuncHost.InitializeAsync(projectRelative, startupTimeout, new Dictionary<string, string>
        {
            ["AzureWebJobsStorage"] = Azurite.ConnectionString,
            ["FUNCTIONS_WORKER_RUNTIME"] = "dotnet",
            ["FUNCTIONS_INPROC_NET8_ENABLED"] = "1",
        });

        ServiceBus = new ServiceBusHelper(ServiceBusEmulator.ConnectionString);
        Trigger = new LogicAppTrigger(
            new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(5) },
            FuncHost.BaseAddress,
            FuncHost,
            defaultWorkflow: config["Workflows:Sample"] ?? "sample-order-servicebus",
            defaultTrigger: config["Triggers:Sample"] ?? "When_a_HTTP_request_is_received");
    }

    [AfterTestRun]
    public static async Task AfterAll()
    {
        // Restore local.settings.json FIRST so that a later fixture-dispose failure can never
        // leave the file patched with emulator connection strings (which would break the next
        // native F5 run / designer load).
        RestoreLocalSettings();

        if (FuncHost is not null) await FuncHost.DisposeAsync();
        if (ServiceBus is not null) await ServiceBus.DisposeAsync();
        if (WireMock is not null) await WireMock.DisposeAsync();
        if (ServiceBusEmulator is not null) await ServiceBusEmulator.DisposeAsync();
        if (Azurite is not null) await Azurite.DisposeAsync();
    }

    private static void RestoreLocalSettings()
    {
        if (_localSettingsPath is not null && _originalLocalSettings is not null && File.Exists(_localSettingsPath))
        {
            try { File.WriteAllText(_localSettingsPath, _originalLocalSettings); } catch { }
        }
    }
}
