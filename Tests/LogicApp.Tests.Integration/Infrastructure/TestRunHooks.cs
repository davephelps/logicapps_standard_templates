using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using Reqnroll;

namespace LogicApp.Tests.Integration.Infrastructure;

/// <summary>
/// Test-run lifecycle. Brings up the emulator stack + WireMock + func host once, tears
/// down at the end. Patches LogicApp/connections.json and local.settings.json on disk in [BeforeTestRun] to:
///   - swap ManagedServiceIdentity Azure connector entries in connections.json to emulator connectionString/accessKey shapes
///     (leaving retained non-Entra connectionString entries untouched)
///   - strip WORKFLOWS_AUTHENTICATION_METHOD from local.settings.json and workflow-designtime/local.settings.json
///   - point Service Bus and Storage settings at the Service Bus and Azurite emulators
///   - point externalApiUrl at the WireMock base URL (external HTTP stub)
///   - force FUNCTIONS_WORKER_RUNTIME=dotnet + FUNCTIONS_INPROC_NET8_ENABLED=1
/// and restores the original files in [AfterTestRun].
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
    public static Exception? InfrastructureStartupError { get; private set; }

    private static readonly System.Text.UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static string? _originalLocalSettings;
    private static string? _localSettingsPath;
    private static string? _originalDesigntimeSettings;
    private static string? _designtimeSettingsPath;
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

            // Emulator stack first -- func host reads connection strings from local.settings.json.
            await Task.WhenAll(
                Azurite.InitializeAsync(),
                ServiceBusEmulator.InitializeAsync(),
                WireMock.InitializeAsync());

            var projectRelative = config["LogicApp:ProjectRelativePath"] ?? "../../../../../LogicApp_Template";
            var startupTimeout = int.Parse(config["LogicApp:StartupTimeoutSeconds"] ?? "90");

            // Snapshot connections.json, parameters.json, and local.settings.json before booting the host.
            var logicAppRoot = FuncHostFixture.LocateLogicAppRoot(projectRelative);
            JObject? connectionsDoc = null;
            _connectionsPath = Path.Combine(logicAppRoot, "connections.json");
            if (File.Exists(_connectionsPath))
            {
                _originalConnections = File.ReadAllText(_connectionsPath, Utf8NoBom);
                connectionsDoc = JObject.Parse(_originalConnections);
            }

            _parametersPath = Path.Combine(logicAppRoot, "parameters.json");
            if (File.Exists(_parametersPath))
            {
                _originalParameters = File.ReadAllText(_parametersPath, Utf8NoBom);
            }

            _designtimeSettingsPath = Path.Combine(logicAppRoot, "workflow-designtime", "local.settings.json");
            if (File.Exists(_designtimeSettingsPath))
            {
                _originalDesigntimeSettings = File.ReadAllText(_designtimeSettingsPath, Utf8NoBom);
                var dtDoc = JObject.Parse(_originalDesigntimeSettings);
                if (dtDoc["Values"] is JObject dtValues && StripWorkflowsAuthenticationMethod(dtValues))
                {
                    File.WriteAllText(_designtimeSettingsPath, dtDoc.ToString(), Utf8NoBom);
                }
            }

            _localSettingsPath = Path.Combine(logicAppRoot, "local.settings.json");
            if (File.Exists(_localSettingsPath))
            {
                _originalLocalSettings = File.ReadAllText(_localSettingsPath, Utf8NoBom);
                var doc = JObject.Parse(_originalLocalSettings);
                var values = (JObject)(doc["Values"] ?? new JObject());

                var connectionsPatched = PatchForEmulators(
                    connectionsDoc,
                    values,
                    ServiceBusEmulator.ConnectionString,
                    Azurite.ConnectionString,
                    WireMock.BaseUrl);

                values["externalApiUrl"] = WireMock.BaseUrl.TrimEnd('/') + (config["External:SamplePath"] ?? "/external/api");
                // Add further product-specific app-setting patches here, e.g.:
                //   values["myAuthHeader"] = "test-token";
                //   values["anotherServiceUrl"] = WireMock.BaseUrl.TrimEnd('/') + "/another";
                doc["Values"] = values;
                File.WriteAllText(_localSettingsPath, doc.ToString(), Utf8NoBom);

                if (connectionsPatched && _connectionsPath is not null && connectionsDoc is not null)
                {
                    File.WriteAllText(_connectionsPath, connectionsDoc.ToString(), Utf8NoBom);
                }
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
                $"Integration test infrastructure failed during [BeforeTestRun]: {InfrastructureStartupError.Message}",
                InfrastructureStartupError);
        }
    }

    [AfterTestRun]
    public static async Task AfterAll()
    {
        // Restore patched LogicApp config files FIRST (UTF-8 no BOM) so that a later fixture-dispose
        // failure can never leave local.settings.json, connections.json, or parameters.json dirty.
        RestoreConfigFiles();

        if (FuncHost is not null) await FuncHost.DisposeAsync();
        if (ServiceBus is not null) await ServiceBus.DisposeAsync();
        if (WireMock is not null) await WireMock.DisposeAsync();
        if (ServiceBusEmulator is not null) await ServiceBusEmulator.DisposeAsync();
        if (Azurite is not null) await Azurite.DisposeAsync();
    }

    public static bool StripWorkflowsAuthenticationMethod(JObject values)
    {
        var matches = values.Properties()
            .Where(p => string.Equals(p.Name, "WORKFLOWS_AUTHENTICATION_METHOD", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var prop in matches)
        {
            prop.Remove();
        }
        return matches.Count > 0;
    }

    /// <summary>
    /// Swaps ManagedServiceIdentity built-in Azure connector entries in connections.json to local
    /// emulator shapes (connectionString, or accessKey for eventGridPublisher), populates matching
    /// local.settings.json Values, and strips WORKFLOWS_AUTHENTICATION_METHOD. Retained non-Entra
    /// connectionString entries (such as on-premises SQL Server) are left untouched.
    /// </summary>
    public static bool PatchForEmulators(
        JObject? connectionsDoc,
        JObject localSettingsValues,
        string sbConnectionString,
        string azuriteConnectionString,
        string wireMockBaseUrl,
        string? sqlConnectionString = null,
        string? eventHubConnectionString = null)
    {
        StripWorkflowsAuthenticationMethod(localSettingsValues);

        localSettingsValues["FUNCTIONS_WORKER_RUNTIME"] = "dotnet";
        localSettingsValues["FUNCTIONS_INPROC_NET8_ENABLED"] = "1";
        localSettingsValues["AzureWebJobsStorage"] = azuriteConnectionString;
        localSettingsValues["serviceBus_connectionString"] = sbConnectionString;
        localSettingsValues["AzureBlob_connectionString"] = azuriteConnectionString;
        localSettingsValues["azureTables_connectionString"] = azuriteConnectionString;
        localSettingsValues["azurequeues_connectionString"] = azuriteConnectionString;
        localSettingsValues["AzureFile_connectionString"] = azuriteConnectionString;

        if (connectionsDoc?["serviceProviderConnections"] is not JObject spConnections)
        {
            return false;
        }

        var mutated = false;
        foreach (var prop in spConnections.Properties())
        {
            if (prop.Value is not JObject connObj) continue;

            var paramSet = connObj["parameterSetName"]?.ToString() ?? string.Empty;
            if (!string.Equals(paramSet, "ManagedServiceIdentity", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var spId = connObj["serviceProvider"]?["id"]?.ToString() ?? string.Empty;
            var connName = prop.Name;

            if (string.Equals(spId, "/serviceProviders/serviceBus", StringComparison.OrdinalIgnoreCase))
            {
                var settingKey = $"{connName}_connectionString";
                connObj["parameterSetName"] = "connectionString";
                connObj["parameterValues"] = new JObject { ["connectionString"] = $"@appsetting('{settingKey}')" };
                localSettingsValues[settingKey] = sbConnectionString;
                mutated = true;
            }
            else if (string.Equals(spId, "/serviceProviders/AzureBlob", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(spId, "/serviceProviders/azureTables", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(spId, "/serviceProviders/azurequeues", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(spId, "/serviceProviders/AzureFile", StringComparison.OrdinalIgnoreCase))
            {
                var settingKey = $"{connName}_connectionString";
                connObj["parameterSetName"] = "connectionString";
                connObj["parameterValues"] = new JObject { ["connectionString"] = $"@appsetting('{settingKey}')" };
                localSettingsValues[settingKey] = azuriteConnectionString;
                mutated = true;
            }
            else if (string.Equals(spId, "/serviceProviders/eventHub", StringComparison.OrdinalIgnoreCase))
            {
                var settingKey = $"{connName}_connectionString";
                connObj["parameterSetName"] = "connectionString";
                connObj["parameterValues"] = new JObject { ["connectionString"] = $"@appsetting('{settingKey}')" };
                var resolvedEh = !string.IsNullOrWhiteSpace(eventHubConnectionString)
                    ? eventHubConnectionString
                    : Environment.GetEnvironmentVariable("EVENTHUB_CONNECTION_STRING");
                localSettingsValues[settingKey] = !string.IsNullOrWhiteSpace(resolvedEh)
                    ? resolvedEh
                    : "Endpoint=sb://127.0.0.1:5672/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";
                mutated = true;
            }
            else if (string.Equals(spId, "/serviceProviders/eventGridPublisher", StringComparison.OrdinalIgnoreCase))
            {
                var endpointKey = $"{connName}_topicEndpoint";
                var accessKeySetting = $"{connName}_accessKey";
                connObj["parameterSetName"] = "accessKey";
                connObj["parameterValues"] = new JObject
                {
                    ["topicEndpoint"] = $"@appsetting('{endpointKey}')",
                    ["accessKey"] = $"@appsetting('{accessKeySetting}')"
                };
                localSettingsValues[endpointKey] = wireMockBaseUrl.TrimEnd('/') + "/eventgrid/api/events";
                localSettingsValues[accessKeySetting] = "emulator-access-key";
                mutated = true;
            }
            else if (string.Equals(spId, "/serviceProviders/sql", StringComparison.OrdinalIgnoreCase))
            {
                var settingKey = $"{connName}_connectionString";
                connObj["parameterSetName"] = "connectionString";
                connObj["parameterValues"] = new JObject { ["connectionString"] = $"@appsetting('{settingKey}')" };
                var resolvedSql = !string.IsNullOrWhiteSpace(sqlConnectionString)
                    ? sqlConnectionString
                    : Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING");
                if (!string.IsNullOrWhiteSpace(resolvedSql))
                {
                    localSettingsValues[settingKey] = resolvedSql;
                }
                else if (localSettingsValues[settingKey] is null)
                {
                    localSettingsValues[settingKey] = "YOUR_SQL_CONNECTION_STRING";
                }
                mutated = true;
            }
        }

        return mutated;
    }

    private static void RestoreConfigFiles()
    {
        RestoreSingleFile(_localSettingsPath, _originalLocalSettings);
        RestoreSingleFile(_designtimeSettingsPath, _originalDesigntimeSettings);
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
