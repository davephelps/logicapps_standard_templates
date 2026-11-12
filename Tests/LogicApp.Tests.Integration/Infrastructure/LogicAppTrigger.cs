using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace LogicApp.Tests.Integration.Infrastructure;

/// <summary>
/// Sole entry point for invoking a workflow from tests. Posts to the management
/// /triggers/{name}/run endpoint with 90 s exponential-backoff retry on 404 and on
/// 409 + WorkflowTriggerNotReady (per logicapp-local-run skill section 5.1).
///
/// On terminal failure throws with the three-part diagnostic payload defined by the
/// local-run skill section 8: SAS-resolved outputs for each failed action, raw runs
/// /{run}/actions JSON, and the last 8 KB of func host stdout/stderr.
/// </summary>
public sealed class LogicAppTrigger
{
    private readonly HttpClient _http;
    private readonly Uri _baseAddress;
    private readonly FuncHostFixture _funcHost;
    private readonly string? _defaultWorkflow;
    private readonly string? _defaultTrigger;

    public LogicAppTrigger(HttpClient http, Uri baseAddress, FuncHostFixture funcHost,
        string? defaultWorkflow = null, string? defaultTrigger = null)
    {
        _http = http;
        _baseAddress = baseAddress;
        _funcHost = funcHost;
        _defaultWorkflow = defaultWorkflow;
        _defaultTrigger = defaultTrigger;
    }

    public async Task<HttpResponseMessage> PostAsync(string body, IDictionary<string, string>? headers = null)
    {
        if (_defaultWorkflow is null || _defaultTrigger is null)
            throw new InvalidOperationException(
                "PostAsync requires defaultWorkflow + defaultTrigger to be configured on LogicAppTrigger.");

        var contentType = "application/json";
        if (headers is not null && headers.TryGetValue("Content-Type", out var ct) && !string.IsNullOrWhiteSpace(ct))
            contentType = ct;

        var content = new StringContent(body, System.Text.Encoding.UTF8, contentType);
        if (headers is not null)
        {
            foreach (var kv in headers)
            {
                if (string.Equals(kv.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
                content.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }
        }

        var callback = await GetCallbackUrlWithRetry(_defaultWorkflow, _defaultTrigger, TimeSpan.FromSeconds(90));
        var req = new HttpRequestMessage(HttpMethod.Post, callback) { Content = content };
        if (headers is not null)
        {
            foreach (var kv in headers)
            {
                if (string.Equals(kv.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
                req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }
        }
        return await _http.SendAsync(req);
    }

    public Task<HttpResponseMessage> InvokeHttpTrigger(string workflow, string triggerName, HttpContent? body) =>
        InvokeHttpTrigger(HttpMethod.Post, workflow, triggerName, body);

    public async Task<HttpResponseMessage> InvokeHttpTrigger(
        HttpMethod method,
        string workflow,
        string triggerName,
        HttpContent? body = null,
        string? relativePath = null,
        string? extraQuery = null,
        IDictionary<string, string>? headers = null)
    {
        // Fetch the workflow's Request-trigger callback URL. Invoking this URL executes the
        // workflow synchronously and returns the Response action's actual status + body, whereas
        // the management /triggers/{name}/run endpoint is fire-and-forget and always returns 202.
        var callback = await GetCallbackUrlWithRetry(workflow, triggerName, TimeSpan.FromSeconds(90));
        var raw = callback.OriginalString;
        var qIndex = raw.IndexOf('?');
        var basePart = qIndex >= 0 ? raw[..qIndex] : raw;
        var queryPart = qIndex >= 0 ? raw[qIndex..] : string.Empty;

        if (!string.IsNullOrWhiteSpace(relativePath))
        {
            var normalizedRel = relativePath.StartsWith('/') ? relativePath : "/" + relativePath;
            if (basePart.EndsWith("/invoke", StringComparison.OrdinalIgnoreCase))
            {
                basePart = $"{basePart}{normalizedRel}";
            }
            else
            {
                var invokeIdx = basePart.IndexOf("/invoke/", StringComparison.OrdinalIgnoreCase);
                if (invokeIdx >= 0)
                {
                    basePart = $"{basePart[..(invokeIdx + "/invoke".Length)]}{normalizedRel}";
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(extraQuery))
        {
            var trimmedExtra = extraQuery.TrimStart('?', '&');
            queryPart = string.IsNullOrEmpty(queryPart)
                ? "?" + trimmedExtra
                : queryPart + "&" + trimmedExtra;
        }

        var req = new HttpRequestMessage(method, new Uri($"{basePart}{queryPart}"));
        if (body is not null) req.Content = body;
        if (headers is not null)
        {
            foreach (var kv in headers)
            {
                if (string.Equals(kv.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    req.Content?.Headers.Remove("Content-Type");
                    req.Content?.Headers.TryAddWithoutValidation("Content-Type", kv.Value);
                    continue;
                }
                req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }
        }
        return await _http.SendAsync(req);
    }

    private async Task<Uri> GetCallbackUrlWithRetry(string workflow, string triggerName, TimeSpan timeout)
    {
        var url = new Uri(
            _baseAddress,
            $"runtime/webhooks/workflow/api/management/workflows/{workflow}/triggers/{triggerName}/listCallbackUrl" +
            "?api-version=2020-05-01-preview");

        var deadline = DateTime.UtcNow + timeout;
        var delay = TimeSpan.FromMilliseconds(500);
        HttpResponseMessage? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var resp = await _http.PostAsync(url, content: null);
            last = resp;
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var value = doc.RootElement.GetProperty("value").GetString();
                if (!string.IsNullOrEmpty(value)) return new Uri(value);
            }

            var raw = await resp.Content.ReadAsStringAsync();
            var notReady = (int)resp.StatusCode == 404
                || ((int)resp.StatusCode == 409 && raw.Contains("WorkflowTriggerNotReady", StringComparison.OrdinalIgnoreCase));
            if (!notReady)
                throw new InvalidOperationException(
                    $"listCallbackUrl for {workflow}/{triggerName} failed: {(int)resp.StatusCode} {raw}");

            await Task.Delay(delay);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 5000));
        }

        var healthInfo = await GetWorkflowHealthSummaryAsync(workflow);
        throw new TimeoutException(
            $"listCallbackUrl for {workflow}/{triggerName} not ready within deadline (last={(int?)last?.StatusCode}).{healthInfo}\nFuncHost tail:\n{_funcHost.GetCapturedOutput()}");
    }

    private async Task<string> GetWorkflowHealthSummaryAsync(string workflow)
    {
        try
        {
            var wfUrl = new Uri(
                _baseAddress,
                $"runtime/webhooks/workflow/api/management/workflows/{workflow}?api-version=2020-05-01-preview");
            var wfResp = await _http.GetAsync(wfUrl);
            var raw = await wfResp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("properties", out var props)
                && props.TryGetProperty("health", out var health))
            {
                var state = health.TryGetProperty("state", out var s) ? s.GetString() : null;
                string? errMsg = null;
                if (health.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var m))
                    errMsg = m.GetString();
                else if (health.TryGetProperty("errorMessage", out var em))
                    errMsg = em.GetString();

                return $" Workflow health state='{state ?? "unknown"}' error='{errMsg ?? "none"}'.";
            }
            return $" Workflow health ({(int)wfResp.StatusCode}): {raw}";
        }
        catch (Exception ex)
        {
            return $" Workflow health lookup failed: {ex.Message}";
        }
    }

    public async Task<string> ForceTrigger(string workflow, string triggerName)
    {
        var resp = await InvokeHttpTrigger(workflow, triggerName, body: null);
        return await WaitForLatestRunStatus(workflow);
    }

    public async Task<string> WaitForLatestRunStatus(string workflow)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        var url = new Uri(
            _baseAddress,
            $"runtime/webhooks/workflow/api/management/workflows/{workflow}/runs?api-version=2020-05-01-preview&$top=1");

        string? runName = null;
        while (DateTime.UtcNow < deadline)
        {
            var pollDelay = TimeSpan.FromMilliseconds(2000);
            try
            {
                var resp = await _http.GetAsync(url);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("value", out var arr) && arr.GetArrayLength() > 0)
                    {
                        var props = arr[0].GetProperty("properties");
                        var status = props.GetProperty("status").GetString();
                        runName = arr[0].GetProperty("name").GetString();
                        if (status is "Succeeded") return status;
                        if (status is "Running" or "Waiting") pollDelay = TimeSpan.FromMilliseconds(500);
                        if (status is "Failed" or "Cancelled" or "TimedOut")
                        {
                            var diag = await BuildFailureDiagnostics(workflow, runName!);
                            throw new InvalidOperationException(
                                $"Workflow {workflow} finished with status '{status}'.\n{diag}");
                        }
                    }
                }
            }
            catch (InvalidOperationException) { throw; }
            catch { }
            await Task.Delay(pollDelay);
        }
        throw new TimeoutException($"Workflow {workflow} did not complete within timeout");
    }

    /// <summary>
    /// Returns the number of recorded workflow action repetitions for an action inside the latest run
    /// (for example an action inside an Until loop). Prefer this over counting raw protocol commands
    /// when asserting workflow retry limits.
    /// </summary>
    public async Task<int> GetLatestActionRepetitionCountAsync(string workflow, string actionName)
    {
        var runsUrl = new Uri(
            _baseAddress,
            $"runtime/webhooks/workflow/api/management/workflows/{workflow}/runs?api-version=2020-05-01-preview&$top=1");
        var runsResp = await _http.GetAsync(runsUrl);
        runsResp.EnsureSuccessStatusCode();
        using var runsDoc = JsonDocument.Parse(await runsResp.Content.ReadAsStringAsync());
        if (!runsDoc.RootElement.TryGetProperty("value", out var runs) || runs.GetArrayLength() == 0)
            return 0;

        var runName = runs[0].GetProperty("name").GetString()!;
        var repsUrl = new Uri(
            _baseAddress,
            $"runtime/webhooks/workflow/api/management/workflows/{workflow}/runs/{runName}/actions/{actionName}/repetitions?api-version=2020-05-01-preview");
        var repsResp = await _http.GetAsync(repsUrl);
        if (!repsResp.IsSuccessStatusCode) return 0;

        using var repsDoc = JsonDocument.Parse(await repsResp.Content.ReadAsStringAsync());
        return repsDoc.RootElement.TryGetProperty("value", out var reps) ? reps.GetArrayLength() : 0;
    }

    private async Task<string> BuildFailureDiagnostics(string workflow, string runName)
    {
        return await BuildRunDiagnostics(workflow, runName, includeAllActions: false);
    }

    public async Task<string> DumpLatestRunDiagnostics(string workflow)
    {
        try
        {
            var url = new Uri(
                _baseAddress,
                $"runtime/webhooks/workflow/api/management/workflows/{workflow}/runs?api-version=2020-05-01-preview&$top=1");
            var resp = await _http.GetAsync(url);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("value", out var arr) || arr.GetArrayLength() == 0)
                return "(no runs found)";
            var runName = arr[0].GetProperty("name").GetString()!;
            return await BuildRunDiagnostics(workflow, runName, includeAllActions: true);
        }
        catch (Exception ex)
        {
            return $"(failed to dump latest run: {ex.Message})";
        }
    }

    private async Task<string> BuildRunDiagnostics(string workflow, string runName, bool includeAllActions)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            var actionsUrl = new Uri(
                _baseAddress,
                $"runtime/webhooks/workflow/api/management/workflows/{workflow}/runs/{runName}/actions?api-version=2020-05-01-preview");
            var resp = await _http.GetAsync(actionsUrl);
            var raw = await resp.Content.ReadAsStringAsync();

            sb.AppendLine("--- failed action outputs ---");
            using (var doc = JsonDocument.Parse(raw))
            {
                if (doc.RootElement.TryGetProperty("value", out var arr))
                {
                    foreach (var act in arr.EnumerateArray())
                    {
                        var props = act.GetProperty("properties");
                        var status = props.TryGetProperty("status", out var s) ? s.GetString() : "";
                        if (!includeAllActions && status != "Failed") continue;
                        var name = act.GetProperty("name").GetString();
                        sb.AppendLine($"[{name}] status={status}");
                        if (props.TryGetProperty("outputsLink", out var link)
                            && link.TryGetProperty("uri", out var uri))
                        {
                            var sas = uri.GetString();
                            try
                            {
                                var outResp = await _http.GetAsync(sas);
                                sb.AppendLine(await outResp.Content.ReadAsStringAsync());
                            }
                            catch (Exception ex) { sb.AppendLine($"(failed to fetch outputs SAS: {ex.Message})"); }
                        }
                    }
                }
            }

            sb.AppendLine("--- run actions (raw) ---");
            sb.AppendLine(raw);
        }
        catch (Exception ex) { sb.AppendLine($"(failed to gather action diagnostics: {ex.Message})"); }

        sb.AppendLine("--- func host output (tail) ---");
        sb.AppendLine(_funcHost.GetCapturedOutput());
        return sb.ToString();
    }
}
