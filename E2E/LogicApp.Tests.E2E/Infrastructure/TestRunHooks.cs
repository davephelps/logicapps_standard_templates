using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using Reqnroll;

namespace LogicApp.Tests.E2E.Infrastructure;

/// <summary>
/// Test-run lifecycle for the domain-level E2E suite. Brings up the full self-contained stack
/// once -- Azurite + Service Bus emulator + backend WireMock + func host + the APIM emulator --
/// and tears it down at the end. Patches the Logic App's local.settings.json on disk to point at
/// the emulators (same pattern as the per-Logic-App integration harness), and restores it after.
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
    public static ApimEmulator? Apim { get; private set; }
    public static APIMClient? ApimClient { get; private set; }
    public static Exception? InfrastructureStartupError { get; private set; }

    private static readonly System.Text.UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static string? _originalLocalSettings;
    private static string? _localSettingsPath;
    private static string? _originalConnections;
    private static string? _connectionsPath;
    private static string? _originalParameters;
    private static string? _parametersPath;

    [BeforeTestRun(Order = 0)]
    public static async Task BeforeAll()
    {
        InfrastructureStartupError = null;
        try
        {
            var config = new ConfigurationBuilder()
                .AddJsonFile("appsettings.test.json", optional: false)
                .Build();

            Azurite = new AzuriteFixture();
            ServiceBusEmulator = new ServiceBusEmulatorFixture();
            WireMock = new WireMockSetup();
            await Task.WhenAll(
                Azurite.InitializeAsync(),
                ServiceBusEmulator.InitializeAsync(),
                WireMock.InitializeAsync());

            var projectRelative = config["LogicApp:ProjectRelativePath"] ?? "../../../../../LogicApp_Template";
            var startupTimeout = int.Parse(config["LogicApp:StartupTimeoutSeconds"] ?? "90");

            var logicAppRoot = FuncHostFixture.LocateLogicAppRoot(projectRelative);
            _connectionsPath = Path.Combine(logicAppRoot, "connections.json");
            if (File.Exists(_connectionsPath))
            {
                _originalConnections = File.ReadAllText(_connectionsPath, Utf8NoBom);
            }

            _parametersPath = Path.Combine(logicAppRoot, "parameters.json");
            if (File.Exists(_parametersPath))
            {
                _originalParameters = File.ReadAllText(_parametersPath, Utf8NoBom);
            }

            _localSettingsPath = Path.Combine(logicAppRoot, "local.settings.json");
            if (File.Exists(_localSettingsPath))
            {
                _originalLocalSettings = File.ReadAllText(_localSettingsPath, Utf8NoBom);
                var doc = JObject.Parse(_originalLocalSettings);
                var values = (JObject)(doc["Values"] ?? new JObject());
                values["FUNCTIONS_WORKER_RUNTIME"] = "dotnet";
                values["FUNCTIONS_INPROC_NET8_ENABLED"] = "1";
                values["AzureWebJobsStorage"] = Azurite.ConnectionString;
                values["serviceBus_connectionString"] = ServiceBusEmulator.ConnectionString;
                values["externalApiUrl"] = WireMock.BaseUrl.TrimEnd('/') + (config["External:SamplePath"] ?? "/external/api");
                doc["Values"] = values;
                File.WriteAllText(_localSettingsPath, doc.ToString(), Utf8NoBom);
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

            var subKey = config["Apim:ValidSubscriptionKey"] ?? "test-subscription-key";
            Apim = new ApimEmulator(
                validSubscriptionKey: subKey,
                basePath: config["Apim:BasePath"] ?? "/api",
                assetOperationPath: config["Apim:AssetOperationPath"] ?? "/asset",
                healthOperationPath: config["Apim:HealthOperationPath"] ?? "/health");
            Apim.EnsureStarted();
            ApimClient = new APIMClient(Apim.AssetUrl, Apim.HealthUrl, subKey);
        }
        catch (Exception ex)
        {
            InfrastructureStartupError = ex;
            RestoreConfigFiles();
        }
    }

    [BeforeScenario(Order = 0)]
    public void FailFastOnStartupError()
    {
        if (InfrastructureStartupError is not null)
        {
            throw new InvalidOperationException(
                $"E2E test infrastructure failed during [BeforeTestRun]: {InfrastructureStartupError.Message}",
                InfrastructureStartupError);
        }
    }

    [AfterTestRun]
    public static async Task AfterAll()
    {
        // Restore patched LogicApp config files FIRST (UTF-8 no BOM) so a later fixture-dispose
        // failure can never leave local.settings.json, connections.json, or parameters.json dirty.
        RestoreConfigFiles();

        ApimClient?.Dispose();
        Apim?.Dispose();
        if (FuncHost is not null) await FuncHost.DisposeAsync();
        if (ServiceBus is not null) await ServiceBus.DisposeAsync();
        if (WireMock is not null) await WireMock.DisposeAsync();
        if (ServiceBusEmulator is not null) await ServiceBusEmulator.DisposeAsync();
        if (Azurite is not null) await Azurite.DisposeAsync();
    }

    private static void RestoreConfigFiles()
    {
        RestoreSingleFile(_localSettingsPath, _originalLocalSettings);
        RestoreSingleFile(_connectionsPath, _originalConnections);
        RestoreSingleFile(_parametersPath, _originalParameters);
    }

    private static void RestoreSingleFile(string? path, string? originalContent)
    {
        if (path is not null && originalContent is not null && File.Exists(path))
        {
            try { File.WriteAllText(path, originalContent, Utf8NoBom); } catch { }
        }
    }
}
