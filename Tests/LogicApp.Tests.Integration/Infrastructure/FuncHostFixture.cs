using System.Diagnostics;
using System.Net.Http;

namespace LogicApp.Tests.Integration.Infrastructure;

/// <summary>
/// Spawns `func host start` against the Logic App project folder and drains
/// stdout/stderr into a bounded tail buffer so failure diagnostics surface in test exceptions
/// (per logicapp-local-run skill section 8).
/// </summary>
public sealed class FuncHostFixture : IAsyncDisposable
{
    private Process? _proc;
    private readonly object _tailLock = new();
    private readonly System.Text.StringBuilder _tail = new();
    private const int TailMaxBytes = 8 * 1024;

    public Uri BaseAddress { get; private set; } = new("http://localhost:0/");
    public string LogicAppRoot { get; private set; } = string.Empty;

    public async Task InitializeAsync(string projectRelativePath, int startupTimeoutSeconds, IDictionary<string, string>? envOverrides = null)
    {
        LogicAppRoot = LocateLogicAppRoot(projectRelativePath);
        var port = TcpPortPicker.GetFreePort();
        BaseAddress = new Uri($"http://localhost:{port}/");

        var psi = new ProcessStartInfo
        {
            FileName = ResolveFuncBinary(),
            Arguments = $"host start --port {port} --no-build",
            WorkingDirectory = LogicAppRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var home = Environment.GetEnvironmentVariable("USERPROFILE") ?? string.Empty;
        if (!string.IsNullOrEmpty(home))
        {
            var depsRoot = Path.Combine(home, ".azurelogicapps", "dependencies");
            if (Directory.Exists(depsRoot))
            {
                var existingPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                psi.Environment["PATH"] =
                    Path.Combine(depsRoot, "NodeJs") + Path.PathSeparator +
                    Path.Combine(depsRoot, "DotNetSDK") + Path.PathSeparator +
                    existingPath;
            }
        }

        if (envOverrides is not null)
        {
            foreach (var kv in envOverrides)
                psi.Environment[kv.Key] = kv.Value;
        }

        try
        {
            _proc = Process.Start(psi);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(
                "Azure Functions Core Tools ('func') not found on PATH. Install before running integration tests.", ex);
        }

        if (_proc is not null)
        {
            _proc.OutputDataReceived += (_, e) => AppendTail(e.Data);
            _proc.ErrorDataReceived += (_, e) => AppendTail(e.Data);
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
        }

        try
        {
            await WaitForReady(BaseAddress, TimeSpan.FromSeconds(startupTimeoutSeconds));
        }
        catch (TimeoutException ex)
        {
            var exited = _proc?.HasExited == true ? $" (process exited with code {_proc!.ExitCode})" : string.Empty;
            throw new TimeoutException(
                $"func host did not become ready at {BaseAddress} within {startupTimeoutSeconds}s{exited}. Captured output:\n{GetCapturedOutput()}", ex);
        }
    }

    private static string ResolveFuncBinary()
    {
        if (!OperatingSystem.IsWindows()) return "func";
        foreach (var name in new[] { "func.exe", "func.cmd", "func.bat" })
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                try
                {
                    var candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
        }
        // Fall back to the bundled Azure Logic Apps (Standard) func core tools that the VS Code task uses.
        var home = Environment.GetEnvironmentVariable("USERPROFILE") ?? string.Empty;
        if (!string.IsNullOrEmpty(home))
        {
            var bundledRoot = Path.Combine(home, ".azurelogicapps", "dependencies", "FuncCoreTools");
            foreach (var name in new[] { "func.exe", "func.cmd" })
            {
                var candidate = Path.Combine(bundledRoot, name);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return "func.cmd";
    }

    private void AppendTail(string? line)
    {
        if (line is null) return;
        lock (_tailLock)
        {
            _tail.AppendLine(line);
            if (_tail.Length > TailMaxBytes)
            {
                var excess = _tail.Length - TailMaxBytes;
                _tail.Remove(0, excess);
            }
        }
    }

    public string GetCapturedOutput()
    {
        lock (_tailLock) { return _tail.ToString(); }
    }

    public static string LocateLogicAppRoot(string relative) => ResolveLogicAppRoot(relative);

    private static string ResolveLogicAppRoot(string relative)
    {
        var dir = AppContext.BaseDirectory;
        var candidate = Path.GetFullPath(Path.Combine(dir, relative));
        if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "host.json")))
            return candidate;

        var cwd = Directory.GetCurrentDirectory();
        candidate = Path.GetFullPath(Path.Combine(cwd, relative));
        if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "host.json")))
            return candidate;

        throw new DirectoryNotFoundException(
            $"Unable to locate Logic App project (host.json) using relative path '{relative}'");
    }

    private static async Task WaitForReady(Uri baseAddress, TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow + timeout;
        var mgmtUrl = new Uri(baseAddress, "/admin/host/status");
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var resp = await http.GetAsync(mgmtUrl);
                if ((int)resp.StatusCode < 500) return;
            }
            catch { }
            await Task.Delay(1000);
        }
        throw new TimeoutException($"func host did not become ready at {baseAddress} within {timeout}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_proc is { HasExited: false })
        {
            try { _proc.Kill(entireProcessTree: true); } catch { }
            try { await _proc.WaitForExitAsync(); } catch { }
        }
        _proc?.Dispose();
    }
}
