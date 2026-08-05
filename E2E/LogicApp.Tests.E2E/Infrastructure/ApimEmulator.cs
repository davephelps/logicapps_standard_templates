// ApimEmulator -- in-process simulation of an Azure API Management gateway that fronts the
// Logic App. Stands in for a real APIM instance when running E2E tests locally or in CI
// without an APIM dev tenant. It is a SECOND WireMockServer instance (separate port from the
// backend stubs in WireMockSetup); tests address it via APIMClient.
//
// It mirrors the OBSERVABLE gateway behaviours that a domain-level E2E cares about:
//   - subscription-key check (Ocp-Apim-Subscription-Key header) -> 401 if missing/wrong;
//   - request body-shape sanity check -> 400 if the body is not JSON;
//   - a health operation -> static 200 acknowledgement, never touches the backend;
//   - a valid asset request -> 202 Accepted (gateway accepted). In production APIM rewrites
//     the URI and forwards to the Logic App; in the E2E the LA processing is then driven via
//     the Request-trigger callback (see the step definitions) so the whole chain is asserted.
//
// Add/adjust mappings here to mirror your real APIM policies. Keep it narrow -- only model the
// behaviours that change the response a caller can observe.

using System.Text.Json;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Settings;

namespace LogicApp.Tests.E2E.Infrastructure;

public sealed class ApimEmulator : IDisposable
{
    private WireMockServer? _server;
    private readonly string _validSubscriptionKey;
    private readonly string _basePath;
    private readonly string _assetOpPath;
    private readonly string _healthOpPath;

    /// <summary>
    /// Regex for "body looks like JSON": optional whitespace then '{' or '['. Good enough to
    /// drive the malformed-body negative scenario. Tighten to a JsonMatcher / schema matcher if
    /// your real APIM policy validates against a specific schema.
    /// </summary>
    private const string ValidBodyRegex = @"^\s*[\[{]";

    public ApimEmulator(
        string validSubscriptionKey,
        string basePath,
        string assetOperationPath,
        string healthOperationPath)
    {
        _validSubscriptionKey = validSubscriptionKey;
        _basePath = basePath.TrimEnd('/');
        _assetOpPath = assetOperationPath.StartsWith("/") ? assetOperationPath : "/" + assetOperationPath;
        _healthOpPath = healthOperationPath.StartsWith("/") ? healthOperationPath : "/" + healthOperationPath;
    }

    public string BaseUrl =>
        _server?.Url ?? throw new InvalidOperationException("APIM emulator not started.");

    public string AssetUrl => $"{BaseUrl}{_basePath}{_assetOpPath}";
    public string HealthUrl => $"{BaseUrl}{_basePath}{_healthOpPath}";

    private WireMockServer Server =>
        _server ?? throw new InvalidOperationException("APIM emulator not started.");

    public void EnsureStarted(int port = 0)
    {
        if (_server is { IsStarted: true }) return;
        _server = WireMockServer.Start(new WireMockServerSettings
        {
            Port = port,
            StartAdminInterface = true
        });

        ConfigureMappings();
    }

    private void ConfigureMappings()
    {
        var assetPath = $"{_basePath}{_assetOpPath}";
        var healthPath = $"{_basePath}{_healthOpPath}";

        // ----------------------------------------------------------------
        // GET {basePath}/health  ->  static return-response
        // (matches getHealth.policy.xml).
        // ----------------------------------------------------------------
        Server
            .Given(Request.Create().WithPath(healthPath).UsingGet())
            .AtPriority(10)
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(BuildHealthEnvelope()));

        // ----------------------------------------------------------------
        // POST {basePath}/asset with valid subscription key AND a body that
        // looks like JSON -> 202 Accepted (gateway accepted the request).
        // Highest priority among /asset POST mappings.
        // ----------------------------------------------------------------
        Server
            .Given(Request.Create()
                .WithPath(assetPath)
                .UsingPost()
                .WithHeader("Ocp-Apim-Subscription-Key", _validSubscriptionKey)
                .WithBody(new RegexMatcher(ValidBodyRegex)))
            .AtPriority(20)
            .RespondWith(Response.Create()
                .WithStatusCode(202)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"status\":\"accepted\"}"));

        // ----------------------------------------------------------------
        // POST {basePath}/asset with valid subscription key but a body that
        // is not JSON -> 400 (body schema rejection).
        // ----------------------------------------------------------------
        Server
            .Given(Request.Create()
                .WithPath(assetPath)
                .UsingPost()
                .WithHeader("Ocp-Apim-Subscription-Key", _validSubscriptionKey))
            .AtPriority(30)
            .RespondWith(Response.Create()
                .WithStatusCode(400)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"statusCode\":400,\"message\":\"Request body is not valid JSON.\"}"));

        // ----------------------------------------------------------------
        // POST {basePath}/asset without a valid subscription key (header
        // missing entirely OR header value not equal to the configured
        // valid key) -> 401 (subscription-key check).
        // Lowest priority -- fires only when the higher-priority mappings
        // do not match.
        // ----------------------------------------------------------------
        Server
            .Given(Request.Create()
                .WithPath(assetPath)
                .UsingPost())
            .AtPriority(40)
            .RespondWith(Response.Create()
                .WithStatusCode(401)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"statusCode\":401,\"message\":\"Access denied due to missing or invalid subscription key.\"}"));
    }

    /// <summary>
    /// A simple health acknowledgement envelope with a random messageId.
    /// </summary>
    private static string BuildHealthEnvelope()
    {
        var msgId = Guid.NewGuid().ToString();
        var body = new
        {
            acknowledgment = new
            {
                messageId = msgId,
                status = "Success",
                description = "gateway is up and running"
            }
        };
        return JsonSerializer.Serialize(body);
    }

    public void Dispose()
    {
        try
        {
            _server?.Stop();
        }
        catch
        {
            // best-effort
        }
        _server?.Dispose();
        _server = null;
    }
}
