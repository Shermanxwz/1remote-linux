using System.Text;
using OneRemote.Linux.Core.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace OneRemote.Linux.Core.Connections;

public sealed class SshTerminalSession : IDisposable
{
    private SshClient? _client;
    private ShellStream? _shell;
    private CancellationTokenSource? _readCts;
    private readonly AnsiTerminalBuffer _buffer = new(3000);

    public event Action<string>? ScreenUpdated;
    public event Action<string>? Disconnected;

    public bool IsConnected => _client?.IsConnected == true && _shell != null;

    public static ConnectionInfo BuildSshConnectionInfo(string host, int port, Credential cred)
    {
        var user = string.IsNullOrWhiteSpace(cred.UserName) ? "root" : cred.UserName.Trim();
        var authMethods = new List<AuthenticationMethod>();

        if (!string.IsNullOrWhiteSpace(cred.PrivateKeyPath))
        {
            var expandedPath = ExpandHomePath(cred.PrivateKeyPath);
            if (File.Exists(expandedPath))
            {
                var keyFile = string.IsNullOrEmpty(cred.Password)
                    ? new PrivateKeyFile(expandedPath)
                    : new PrivateKeyFile(expandedPath, cred.Password);
                authMethods.Add(new PrivateKeyAuthenticationMethod(user, keyFile));
            }
        }

        if (!string.IsNullOrEmpty(cred.Password))
        {
            authMethods.Add(new PasswordAuthenticationMethod(user, cred.Password));
            var kbd = new KeyboardInteractiveAuthenticationMethod(user);
            kbd.AuthenticationPrompt += (_, args) =>
            {
                foreach (var prompt in args.Prompts)
                {
                    prompt.Response = cred.Password;
                }
            };
            authMethods.Add(kbd);
        }

        if (authMethods.Count == 0)
        {
            // Fallback to default ~/.ssh/id_ed25519 or ~/.ssh/id_rsa if present
            foreach (var defaultKey in new[] { "~/.ssh/id_ed25519", "~/.ssh/id_rsa" })
            {
                var expanded = ExpandHomePath(defaultKey);
                if (File.Exists(expanded))
                {
                    try
                    {
                        authMethods.Add(new PrivateKeyAuthenticationMethod(user, new PrivateKeyFile(expanded)));
                    }
                    catch
                    {
                        // Ignore passphrase-protected default key
                    }
                }
            }
        }

        if (authMethods.Count == 0)
        {
            authMethods.Add(new PasswordAuthenticationMethod(user, string.Empty));
        }

        return new ConnectionInfo(host, port, user, authMethods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(15),
            Encoding = Encoding.UTF8,
        };
    }

    public static string ExpandHomePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;
        var trimmed = path.Trim();
        if (trimmed.StartsWith("~/", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, trimmed[2..]);
        }
        return trimmed;
    }

    public async Task ConnectAsync(SSH server, Credential resolvedCred, uint columns = 120, uint rows = 36, CancellationToken ct = default)
    {
        var connInfo = BuildSshConnectionInfo(server.Address, server.GetPort(22), resolvedCred);
        _client = new SshClient(connInfo)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(25),
        };

        await _client.ConnectAsync(ct);
        _shell = _client.CreateShellStream("xterm-256color", columns, rows, 0, 0, 65536);

        _readCts = new CancellationTokenSource();
        _ = Task.Run(() => ReadLoopAsync(_readCts.Token));

        if (!string.IsNullOrWhiteSpace(server.StartupAutoCommand))
        {
            await SendLineAsync(server.StartupAutoCommand);
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buf = new byte[8192];
        try
        {
            while (!ct.IsCancellationRequested && _shell != null && _client?.IsConnected == true)
            {
                var read = await _shell.ReadAsync(buf.AsMemory(0, buf.Length), ct);
                if (read <= 0)
                {
                    await Task.Delay(25, ct);
                    continue;
                }
                var rendered = _buffer.AppendBytes(buf.AsSpan(0, read));
                ScreenUpdated?.Invoke(rendered);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal close
        }
        catch (SshConnectionException ex)
        {
            Disconnected?.Invoke($"SSH 会话已断开：{ex.Message}");
        }
        catch (Exception ex)
        {
            Disconnected?.Invoke($"终端流结束：{ex.Message}");
        }
    }

    public async Task SendRawAsync(string rawInput)
    {
        if (_shell == null || _client?.IsConnected != true)
            return;
        var bytes = Encoding.UTF8.GetBytes(rawInput);
        await _shell.WriteAsync(bytes, CancellationToken.None);
        await _shell.FlushAsync(CancellationToken.None);
    }

    public Task SendLineAsync(string command) => SendRawAsync(command + "\n");

    public Task SendInterruptAsync() => SendRawAsync("\x03"); // Ctrl+C
    public Task SendEofAsync() => SendRawAsync("\x04");       // Ctrl+D

    public void ClearBuffer()
    {
        _buffer.Clear();
        ScreenUpdated?.Invoke(string.Empty);
    }

    public void Dispose()
    {
        try { _readCts?.Cancel(); } catch { /* ignore */ }
        try { _shell?.Dispose(); } catch { /* ignore */ }
        try { _client?.Disconnect(); } catch { /* ignore */ }
        try { _client?.Dispose(); } catch { /* ignore */ }
        _shell = null;
        _client = null;
    }
}
