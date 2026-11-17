using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OneRemote.Linux.Core.Connections;
using OneRemote.Linux.Core.Models;

namespace OneRemote.Linux.App.ViewModels;

public abstract partial class SessionTabViewModel : ObservableObject, IDisposable
{
    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _protocolBadge;

    [ObservableProperty]
    private string _statusText = "正在准备连接...";

    public ProtocolBase Server { get; }
    public Action<SessionTabViewModel>? CloseRequested { get; set; }

    public bool IsSshTab => this is SshSessionTabViewModel;
    public bool IsFileTab => this is FileBrowserTabViewModel;
    public bool IsRdpTab => this is RdpSessionTabViewModel;

    protected SessionTabViewModel(ProtocolBase server, string protocolBadge)
    {
        Server = server;
        _title = server.DisplayName;
        _protocolBadge = protocolBadge;
    }

    [RelayCommand]
    public void RequestClose() => CloseRequested?.Invoke(this);

    public abstract void Dispose();
}

public sealed partial class SshSessionTabViewModel : SessionTabViewModel
{
    private readonly SSH _ssh;
    private readonly Credential _cred;
    private readonly Action<ProtocolBase>? _openSftpCallback;
    private SshTerminalSession? _session;
    private readonly List<string> _commandHistory = new();
    private int _historyIndex = -1;

    [ObservableProperty]
    private string _terminalOutput = string.Empty;

    [ObservableProperty]
    private string _commandInput = string.Empty;

    [ObservableProperty]
    private bool _isConnected;

    public SshSessionTabViewModel(SSH ssh, Credential resolvedCred, Action<ProtocolBase>? openSftpCallback = null)
        : base(ssh, "SSH")
    {
        _ssh = ssh;
        _cred = resolvedCred;
        _openSftpCallback = openSftpCallback;
    }

    public async Task ConnectAsync()
    {
        DisposeSession();
        StatusText = $"正在连接 {_ssh.Address}:{_ssh.GetPort(22)} ({_cred.UserName})...";
        _session = new SshTerminalSession();
        _session.ScreenUpdated += text =>
        {
            Dispatcher.UIThread.Post(() => TerminalOutput = text);
        };
        _session.Disconnected += reason =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                IsConnected = false;
                StatusText = reason;
                TerminalOutput += $"\n\n[{reason}]";
            });
        };

        try
        {
            await _session.ConnectAsync(_ssh, _cred);
            IsConnected = true;
            StatusText = $"已连接 {_cred.UserName}@{_ssh.Address}:{_ssh.GetPort(22)} (UTF-8 / xterm-256color)";
            if (_ssh.OpenSftpOnConnected)
            {
                _openSftpCallback?.Invoke(_ssh);
            }
        }
        catch (Exception ex)
        {
            IsConnected = false;
            StatusText = $"SSH 连接失败：{ex.Message}";
            TerminalOutput = $"[SSH 连接失败] {_ssh.Address}:{_ssh.GetPort(22)}\n原因：{ex.Message}\n";
        }
    }

    [RelayCommand]
    public async Task SendCommandAsync()
    {
        if (_session == null || !_session.IsConnected)
            return;

        var cmd = CommandInput;
        if (!string.IsNullOrWhiteSpace(cmd))
        {
            _commandHistory.Add(cmd);
            _historyIndex = _commandHistory.Count;
        }
        CommandInput = string.Empty;
        await _session.SendLineAsync(cmd);
    }

    public void RecallHistory(int delta)
    {
        if (_commandHistory.Count == 0) return;
        _historyIndex = Math.Clamp(_historyIndex + delta, 0, _commandHistory.Count);
        CommandInput = _historyIndex < _commandHistory.Count ? _commandHistory[_historyIndex] : string.Empty;
    }

    [RelayCommand]
    public async Task SendCtrlCAsync()
    {
        if (_session != null) await _session.SendInterruptAsync();
    }

    [RelayCommand]
    public async Task SendCtrlDAsync()
    {
        if (_session != null) await _session.SendEofAsync();
    }

    [RelayCommand]
    public void ClearTerminal() => _session?.ClearBuffer();

    [RelayCommand]
    public Task ReconnectAsync() => ConnectAsync();

    [RelayCommand]
    public void OpenCompanionSftp() => _openSftpCallback?.Invoke(_ssh);

    private void DisposeSession()
    {
        try { _session?.Dispose(); } catch { /* ignore */ }
        _session = null;
    }

    public override void Dispose() => DisposeSession();
}

public sealed partial class FileBrowserTabViewModel : SessionTabViewModel
{
    private readonly ProtocolBaseWithAddressPortUserPwd _proto;
    private readonly Credential _cred;
    private IFileTransferSession? _transfer;

    [ObservableProperty]
    private string _currentPath = "/";

    [ObservableProperty]
    private RemoteFileEntry? _selectedEntry;

    [ObservableProperty]
    private string _newFolderName = string.Empty;

    public ObservableCollection<RemoteFileEntry> Entries { get; } = new();

    public FileBrowserTabViewModel(ProtocolBaseWithAddressPortUserPwd proto, Credential resolvedCred)
        : base(proto, proto is FTP ? "FTP" : "SFTP")
    {
        _proto = proto;
        _cred = resolvedCred;
        _currentPath = proto switch
        {
            SFTP sftp when !string.IsNullOrWhiteSpace(sftp.StartupPath) => sftp.StartupPath,
            FTP ftp when !string.IsNullOrWhiteSpace(ftp.StartupPath) => ftp.StartupPath,
            _ => "/"
        };
    }

    public async Task ConnectAndListAsync()
    {
        DisposeSession();
        var host = string.IsNullOrWhiteSpace(_cred.Address) ? _proto.Address : _cred.Address;
        var defaultPort = _proto is FTP ? 21 : 22;
        var port = int.TryParse(_cred.Port, out var cp) && cp > 0 ? cp : _proto.GetPort(defaultPort);

        StatusText = $"正在连接 {ProtocolBadge} {host}:{port}...";
        _transfer = _proto is FTP
            ? new FtpTransferSession(host, port, _cred, CurrentPath)
            : new SftpTransferSession(host, port, _cred, CurrentPath);

        try
        {
            await _transfer.ConnectAsync();
            CurrentPath = _transfer.CurrentDirectory;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"{ProtocolBadge} 连接失败：{ex.Message}";
        }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_transfer == null || !_transfer.IsConnected)
            return;
        try
        {
            var items = await _transfer.ListDirectoryAsync(CurrentPath);
            Entries.Clear();
            foreach (var item in items)
                Entries.Add(item);
            StatusText = $"{CurrentPath}（共 {Entries.Count} 项）";
        }
        catch (Exception ex)
        {
            StatusText = $"读取目录失败：{ex.Message}";
        }
    }

    [RelayCommand]
    public async Task NavigateUpAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentPath) || CurrentPath == "/")
            return;
        var trimmed = CurrentPath.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        CurrentPath = idx <= 0 ? "/" : trimmed[..idx];
        await RefreshAsync();
    }

    public async Task EnterEntryAsync(RemoteFileEntry? entry)
    {
        if (entry is not { IsDirectory: true })
            return;
        CurrentPath = entry.FullPath;
        await RefreshAsync();
    }

    public async Task UploadLocalFileAsync(string localFilePath)
    {
        if (_transfer == null || !_transfer.IsConnected || !File.Exists(localFilePath))
            return;
        try
        {
            var fileName = Path.GetFileName(localFilePath);
            var remoteDest = CurrentPath.EndsWith('/') ? CurrentPath + fileName : $"{CurrentPath}/{fileName}";
            StatusText = $"正在上传 {fileName}...";
            await _transfer.UploadFileAsync(localFilePath, remoteDest);
            await RefreshAsync();
            StatusText = $"上传完成：{remoteDest}";
        }
        catch (Exception ex)
        {
            StatusText = $"上传失败：{ex.Message}";
        }
    }

    public async Task DownloadSelectedToAsync(string destinationPath)
    {
        if (_transfer == null || SelectedEntry is null || SelectedEntry.IsDirectory)
            return;
        try
        {
            StatusText = $"正在下载 {SelectedEntry.Name}...";
            await _transfer.DownloadFileAsync(SelectedEntry.FullPath, destinationPath);
            StatusText = $"已下载至 {destinationPath}";
        }
        catch (Exception ex)
        {
            StatusText = $"下载失败：{ex.Message}";
        }
    }

    [RelayCommand]
    public async Task CreateFolderAsync()
    {
        if (_transfer == null || string.IsNullOrWhiteSpace(NewFolderName))
            return;
        try
        {
            var target = CurrentPath.EndsWith('/') ? CurrentPath + NewFolderName.Trim() : $"{CurrentPath}/{NewFolderName.Trim()}";
            await _transfer.CreateDirectoryAsync(target);
            NewFolderName = string.Empty;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"新建目录失败：{ex.Message}";
        }
    }

    [RelayCommand]
    public async Task DeleteSelectedAsync()
    {
        if (_transfer == null || SelectedEntry == null)
            return;
        try
        {
            var name = SelectedEntry.Name;
            await _transfer.DeleteAsync(SelectedEntry);
            await RefreshAsync();
            StatusText = $"已删除 {name}";
        }
        catch (Exception ex)
        {
            StatusText = $"删除失败：{ex.Message}";
        }
    }

    private void DisposeSession()
    {
        try { _transfer?.Dispose(); } catch { /* ignore */ }
        _transfer = null;
    }

    public override void Dispose() => DisposeSession();
}

public sealed partial class RdpSessionTabViewModel : SessionTabViewModel
{
    private readonly RDP _rdp;
    private readonly Credential _cred;
    private readonly string? _preferredBinary;
    private Process? _process;

    [ObservableProperty]
    private string _diagnosticLog = string.Empty;

    [ObservableProperty]
    private string _sessionSummary = string.Empty;

    [ObservableProperty]
    private bool _isRunning;

    public RdpSessionTabViewModel(RDP rdp, Credential resolvedCred, string? preferredBinary)
        : base(rdp, "RDP")
    {
        _rdp = rdp;
        _cred = resolvedCred;
        _preferredBinary = preferredBinary;
    }

    [RelayCommand]
    public void LaunchRdp()
    {
        StopRdp();
        var env = FreeRdpLauncher.DetectDesktopEnvironment();
        var bin = FreeRdpLauncher.ResolveBinary(_preferredBinary);

        if (bin == null)
        {
            IsRunning = false;
            StatusText = "未检测到系统 FreeRDP 客户端 (xfreerdp3 / xfreerdp)";
            DiagnosticLog = "未在 PATH 中找到 xfreerdp3 或 xfreerdp。\n请在 Ubuntu 终端执行以下命令安装：\n  sudo apt update && sudo apt install -y freerdp3-x11\n（或 sudo apt install -y freerdp2-x11）\n";
            return;
        }

        var argsPreview = FreeRdpLauncher.BuildArguments(_rdp, _cred, env, pipePasswordFromStdin: !string.IsNullOrEmpty(_cred.Password));
        SessionSummary = $"客户端：{bin} | 桌面会话：{env.DisplayServer} | 参数：{string.Join(' ', argsPreview)}";
        DiagnosticLog = $"[启动 FreeRDP] {SessionSummary}\n";

        try
        {
            _process = FreeRdpLauncher.StartProcess(
                bin,
                _rdp,
                _cred,
                onLogLine: line => Dispatcher.UIThread.Post(() =>
                {
                    DiagnosticLog += line + "\n";
                }),
                onExited: code => Dispatcher.UIThread.Post(() =>
                {
                    IsRunning = false;
                    StatusText = $"FreeRDP 会话已结束 (退出码: {code})";
                    DiagnosticLog += $"\n[进程退出，代码 {code}]\n";
                }));

            IsRunning = true;
            StatusText = $"正在运行 {bin} (PID {_process.Id}) — 支持动态分辨率与双向剪贴板";
        }
        catch (Exception ex)
        {
            IsRunning = false;
            StatusText = $"启动 FreeRDP 失败：{ex.Message}";
            DiagnosticLog += $"\n[异常] {ex}\n";
        }
    }

    [RelayCommand]
    public void StopRdp()
    {
        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); } catch { /* ignore */ }
        }
        try { _process?.Dispose(); } catch { /* ignore */ }
        _process = null;
        IsRunning = false;
    }

    public override void Dispose() => StopRdp();
}
