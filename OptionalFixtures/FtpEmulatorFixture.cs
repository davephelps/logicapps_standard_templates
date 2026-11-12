using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LogicApp.Tests.Integration.Infrastructure;

/// <summary>
/// Opt-in lightweight in-process RFC 959 passive FTP emulator for testing the Logic Apps Standard
/// built-in /serviceProviders/Ftp connector locally without external FTP containers.
/// Copy into Infrastructure/ when needed. Honours FTP_HOST, FTP_PORT, FTP_USER, and FTP_PASSWORD
/// environment-variable escape hatches.
///
/// Note on retry assertions: use LogicAppTrigger.GetLatestActionRepetitionCountAsync to assert
/// workflow action retry counts inside Until loops; GetCommandCount tracks raw FTP protocol commands.
/// </summary>
public sealed class FtpEmulatorFixture : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _commandCounts = new(StringComparer.OrdinalIgnoreCase);
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public string Host { get; private set; } = "127.0.0.1";
    public int Port { get; private set; }
    public string Username { get; private set; } = "ftpuser";
    public string Password { get; private set; } = "test-ftp-password";
    public bool SimulateServerFailure { get; set; }
    public bool SimulateStoreFailure { get; set; }

    public Task InitializeAsync()
    {
        var escapeHost = Environment.GetEnvironmentVariable("FTP_HOST");
        if (!string.IsNullOrWhiteSpace(escapeHost))
        {
            Host = escapeHost;
            Port = int.TryParse(Environment.GetEnvironmentVariable("FTP_PORT"), out var p) ? p : 21;
            Username = Environment.GetEnvironmentVariable("FTP_USER") ?? "ftpuser";
            Password = Environment.GetEnvironmentVariable("FTP_PASSWORD") ?? string.Empty;
            return Task.CompletedTask;
        }

        Port = TcpPortPicker.GetFreePort();
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, Port);
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptClientsAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public void Reset()
    {
        _files.Clear();
        _commandCounts.Clear();
        SimulateServerFailure = false;
        SimulateStoreFailure = false;
    }

    public void SeedFile(string virtualPath, string content)
    {
        _files[NormalizePath(virtualPath)] = Encoding.UTF8.GetBytes(content);
    }

    public void SeedFileBytes(string virtualPath, byte[] content)
    {
        _files[NormalizePath(virtualPath)] = content;
    }

    public bool FileExists(string virtualPath) => _files.ContainsKey(NormalizePath(virtualPath));

    public string? ReadFileText(string virtualPath) =>
        _files.TryGetValue(NormalizePath(virtualPath), out var bytes) ? Encoding.UTF8.GetString(bytes) : null;

    public int GetCommandCount(string commandVerb) =>
        _commandCounts.TryGetValue(commandVerb.ToUpperInvariant(), out var count) ? count : 0;

    public IReadOnlyList<string> ListFilesInDirectory(string directoryPath)
    {
        var dir = NormalizeDirectory(directoryPath);
        return _files.Keys
            .Where(k => IsDirectChildOf(k, dir))
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void RecordCommand(string verb)
    {
        _commandCounts.AddOrUpdate(verb.ToUpperInvariant(), 1, (_, c) => c + 1);
    }

    private async Task AcceptClientsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                if (ct.IsCancellationRequested) break;
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true)
            {
                NewLine = "\r\n",
                AutoFlush = true
            };

            if (SimulateServerFailure)
            {
                RecordCommand("CONNECT_FAIL");
                await writer.WriteLineAsync("421 Service not available, closing control connection.");
                return;
            }

            await writer.WriteLineAsync("220 Local FTP Emulator Ready.");
            var cwd = "/";
            TcpListener? passiveListener = null;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null) break;

                    var spaceIdx = line.IndexOf(' ');
                    var cmd = (spaceIdx < 0 ? line : line[..spaceIdx]).Trim().ToUpperInvariant();
                    var arg = spaceIdx < 0 ? string.Empty : line[(spaceIdx + 1)..].Trim();
                    RecordCommand(cmd);

                    if (SimulateServerFailure && cmd is "LIST" or "NLST" or "MLSD" or "RETR" or "STOR" or "DELE")
                    {
                        await writer.WriteLineAsync("550 Simulated FTP server outage.");
                        continue;
                    }

                    switch (cmd)
                    {
                        case "USER":
                            await writer.WriteLineAsync("331 User name okay, need password.");
                            break;
                        case "PASS":
                            await writer.WriteLineAsync("230 User logged in, proceed.");
                            break;
                        case "SYST":
                            await writer.WriteLineAsync("215 UNIX Type: L8");
                            break;
                        case "FEAT":
                            await writer.WriteLineAsync("211-Features:");
                            await writer.WriteLineAsync(" PASV");
                            await writer.WriteLineAsync(" EPSV");
                            await writer.WriteLineAsync(" UTF8");
                            await writer.WriteLineAsync(" SIZE");
                            await writer.WriteLineAsync(" MDTM");
                            await writer.WriteLineAsync("211 End");
                            break;
                        case "OPTS":
                        case "NOOP":
                        case "TYPE":
                            await writer.WriteLineAsync("200 Command okay.");
                            break;
                        case "PWD":
                        case "XPWD":
                            await writer.WriteLineAsync($"257 \"{cwd}\" is current directory.");
                            break;
                        case "CWD":
                        case "XCWD":
                            cwd = NormalizeDirectory(ResolvePath(cwd, arg));
                            await writer.WriteLineAsync($"250 Directory changed to {cwd}.");
                            break;
                        case "CDUP":
                        case "XCUP":
                            cwd = "/";
                            await writer.WriteLineAsync("250 Directory changed to /.");
                            break;
                        case "PASV":
                        {
                            passiveListener?.Stop();
                            var pasvPort = TcpPortPicker.GetFreePort();
                            passiveListener = new TcpListener(IPAddress.Loopback, pasvPort);
                            passiveListener.Start();
                            await writer.WriteLineAsync($"227 Entering Passive Mode (127,0,0,1,{pasvPort / 256},{pasvPort % 256}).");
                            break;
                        }
                        case "EPSV":
                        {
                            passiveListener?.Stop();
                            var epsvPort = TcpPortPicker.GetFreePort();
                            passiveListener = new TcpListener(IPAddress.Loopback, epsvPort);
                            passiveListener.Start();
                            await writer.WriteLineAsync($"229 Entering Extended Passive Mode (|||{epsvPort}|).");
                            break;
                        }
                        case "SIZE":
                        {
                            var path = ResolvePath(cwd, arg);
                            await writer.WriteLineAsync(_files.TryGetValue(path, out var data) ? $"213 {data.Length}" : "550 File not found.");
                            break;
                        }
                        case "MDTM":
                            await writer.WriteLineAsync("213 20261015010101");
                            break;
                        case "LIST":
                        case "NLST":
                        case "MLSD":
                        {
                            var listArg = StripListFlags(arg);
                            var targetDir = string.IsNullOrEmpty(listArg) ? cwd : ResolvePath(cwd, listArg);
                            if (passiveListener is null)
                            {
                                await writer.WriteLineAsync("425 Use PASV or EPSV first.");
                                break;
                            }

                            await writer.WriteLineAsync("150 Opening ASCII mode data connection for file list.");
                            using (var dataClient = await passiveListener.AcceptTcpClientAsync(ct))
                            await using (var dataStream = dataClient.GetStream())
                            await using (var dataWriter = new StreamWriter(dataStream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true })
                            {
                                foreach (var fileName in ListFilesInDirectory(targetDir))
                                {
                                    var fullPath = NormalizePath($"{targetDir.TrimEnd('/')}/{fileName}");
                                    var len = _files.TryGetValue(fullPath, out var b) ? b.Length : 0;
                                    if (cmd == "NLST")
                                        await dataWriter.WriteLineAsync(fileName);
                                    else if (cmd == "MLSD")
                                        await dataWriter.WriteLineAsync($"type=file;size={len};modify=20261015010101; {fileName}");
                                    else
                                        await dataWriter.WriteLineAsync($"-rw-r--r-- 1 ftpuser ftpuser {len.ToString(CultureInfo.InvariantCulture)} Oct 15 01:01 {fileName}");
                                }
                            }
                            passiveListener.Stop();
                            passiveListener = null;
                            await writer.WriteLineAsync("226 Transfer complete.");
                            break;
                        }
                        case "RETR":
                        {
                            var filePath = ResolvePath(cwd, arg);
                            if (!_files.TryGetValue(filePath, out var bytes))
                            {
                                passiveListener?.Stop();
                                passiveListener = null;
                                await writer.WriteLineAsync("550 File not found.");
                                break;
                            }
                            if (passiveListener is null)
                            {
                                await writer.WriteLineAsync("425 Use PASV or EPSV first.");
                                break;
                            }

                            await writer.WriteLineAsync("150 Opening binary mode data connection.");
                            using (var dataClient = await passiveListener.AcceptTcpClientAsync(ct))
                            await using (var dataStream = dataClient.GetStream())
                            {
                                await dataStream.WriteAsync(bytes, ct);
                                await dataStream.FlushAsync(ct);
                            }
                            passiveListener.Stop();
                            passiveListener = null;
                            await writer.WriteLineAsync("226 Transfer complete.");
                            break;
                        }
                        case "STOR":
                        {
                            var filePath = ResolvePath(cwd, arg);
                            if (SimulateStoreFailure)
                            {
                                passiveListener?.Stop();
                                passiveListener = null;
                                await writer.WriteLineAsync("550 Simulated FTP store error.");
                                break;
                            }
                            if (passiveListener is null)
                            {
                                await writer.WriteLineAsync("425 Use PASV or EPSV first.");
                                break;
                            }

                            await writer.WriteLineAsync("150 Opening binary mode data connection for store.");
                            using var ms = new MemoryStream();
                            using (var dataClient = await passiveListener.AcceptTcpClientAsync(ct))
                            await using (var dataStream = dataClient.GetStream())
                            {
                                await dataStream.CopyToAsync(ms, ct);
                            }
                            _files[filePath] = ms.ToArray();
                            passiveListener.Stop();
                            passiveListener = null;
                            await writer.WriteLineAsync("226 Transfer complete.");
                            break;
                        }
                        case "DELE":
                        {
                            var filePath = ResolvePath(cwd, arg);
                            await writer.WriteLineAsync(_files.TryRemove(filePath, out _) ? "250 File deleted." : "550 File not found.");
                            break;
                        }
                        case "MKD":
                        case "XMKD":
                            await writer.WriteLineAsync($"257 \"{NormalizeDirectory(ResolvePath(cwd, arg))}\" directory created.");
                            break;
                        case "QUIT":
                            await writer.WriteLineAsync("221 Goodbye.");
                            return;
                        default:
                            await writer.WriteLineAsync("502 Command not implemented.");
                            break;
                    }
                }
            }
            finally
            {
                passiveListener?.Stop();
            }
        }
    }

    private static string ResolvePath(string cwd, string arg)
    {
        if (string.IsNullOrWhiteSpace(arg)) return NormalizePath(cwd);
        var p = arg.Replace('\\', '/').Trim();
        return p.StartsWith('/') ? NormalizePath(p) : NormalizePath($"{cwd.TrimEnd('/')}/{p}");
    }

    private static string NormalizePath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new Stack<string>();
        foreach (var part in parts)
        {
            if (part == ".") continue;
            if (part == "..") { if (stack.Count > 0) stack.Pop(); continue; }
            stack.Push(part);
        }
        return "/" + string.Join("/", stack.Reverse());
    }

    private static string NormalizeDirectory(string path) => NormalizePath(path);

    private static bool IsDirectChildOf(string fileKey, string dir)
    {
        var prefix = dir == "/" ? "/" : dir.TrimEnd('/') + "/";
        if (!fileKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = fileKey[prefix.Length..];
        return rest.Length > 0 && !rest.Contains('/');
    }

    private static string StripListFlags(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg)) return string.Empty;
        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', tokens.Where(t => !t.StartsWith('-')));
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
        _cts?.Dispose();
    }
}
