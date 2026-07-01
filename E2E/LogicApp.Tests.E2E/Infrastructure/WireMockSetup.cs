using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Settings;

namespace LogicApp.Tests.E2E.Infrastructure;

/// <summary>
/// WireMock lifecycle. STUBS EXTERNAL HTTP ONLY (third-party / vendor APIs the workflow
/// calls over HTTP). Internal Azure services (Service Bus, Blob, Queue, Table, SFTP) go
/// through the emulator stack, never WireMock -- the workflow reaches them via
/// ServiceProvider connectors that speak the real protocol.
/// </summary>
public sealed class WireMockSetup : IAsyncDisposable, IDisposable
{
    private WireMockServer? _server;
    public string BaseUrl => _server?.Url ?? string.Empty;
    public WireMockServer Server => _server
        ?? throw new InvalidOperationException("WireMock not started");

    public Task InitializeAsync()
    {
        EnsureStarted();
        return Task.CompletedTask;
    }

    public void EnsureStarted()
    {
        if (_server is not null) return;
        _server = WireMockServer.Start(new WireMockServerSettings
        {
            ReadStaticMappings = false
        });
    }

    public void LoadStubs(string stubsFolder)
    {
        EnsureStarted();
        if (Directory.Exists(stubsFolder))
        {
            _server!.ReadStaticMappings(stubsFolder);
        }
    }

    public void ResetAll() => Reset();

    public void Reset()
    {
        _server?.Reset();
        _server?.ResetLogEntries();
    }

    public int CallCount(string pathContains)
    {
        if (_server is null) return 0;
        return _server.LogEntries.Count(e =>
            (e.RequestMessage.Path ?? string.Empty)
                .Contains(pathContains, StringComparison.OrdinalIgnoreCase));
    }

    public void StubGet(string path, int statusCode, string? body = null)
        => Stub("GET", path, statusCode, body);

    public void StubPost(string path, int statusCode, string? body = null)
        => Stub("POST", path, statusCode, body);

    public void StubPut(string path, int statusCode, string? body = null)
        => Stub("PUT", path, statusCode, body);

    private void Stub(string method, string path, int statusCode, string? body)
    {
        EnsureStarted();
        var matcher = path.Contains('*')
            ? Request.Create().WithPath(new WireMock.Matchers.WildcardMatcher(path)).UsingMethod(method)
            : Request.Create().WithPath(path).UsingMethod(method);
        var resp = Response.Create().WithStatusCode(statusCode);
        if (body is not null) resp = resp.WithBody(body).WithHeader("Content-Type", "application/json");
        _server!.Given(matcher).RespondWith(resp);
    }

    public void Dispose()
    {
        _server?.Stop();
        _server?.Dispose();
        _server = null;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
