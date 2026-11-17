using FluentFTP;
using OneRemote.Linux.Core.Models;
using Renci.SshNet;

namespace OneRemote.Linux.Core.Connections;

public sealed record RemoteFileEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long SizeBytes,
    DateTime LastModified)
{
    public string Icon => IsDirectory ? "📁" : "📄";
    public string FormattedSize => IsDirectory ? "<DIR>" : FormatBytes(SizeBytes);
    public string FormattedTime => LastModified == DateTime.MinValue ? "-" : LastModified.ToString("yyyy-MM-dd HH:mm");

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}

public interface IFileTransferSession : IDisposable
{
    bool IsConnected { get; }
    string CurrentDirectory { get; }
    Task ConnectAsync(CancellationToken ct = default);
    Task<List<RemoteFileEntry>> ListDirectoryAsync(string path, CancellationToken ct = default);
    Task DownloadFileAsync(string remotePath, string localPath, CancellationToken ct = default);
    Task UploadFileAsync(string localPath, string remotePath, CancellationToken ct = default);
    Task CreateDirectoryAsync(string remotePath, CancellationToken ct = default);
    Task DeleteAsync(RemoteFileEntry item, CancellationToken ct = default);
}

public sealed class SftpTransferSession : IFileTransferSession
{
    private readonly string _host;
    private readonly int _port;
    private readonly Credential _cred;
    private SftpClient? _sftp;

    public bool IsConnected => _sftp?.IsConnected == true;
    public string CurrentDirectory { get; private set; }

    public SftpTransferSession(string host, int port, Credential cred, string startupPath = "/")
    {
        _host = host;
        _port = port;
        _cred = cred;
        CurrentDirectory = string.IsNullOrWhiteSpace(startupPath) ? "/" : startupPath;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var connInfo = SshTerminalSession.BuildSshConnectionInfo(_host, _port, _cred);
        _sftp = new SftpClient(connInfo)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(25),
        };
        await _sftp.ConnectAsync(ct);
        if (!string.IsNullOrWhiteSpace(_sftp.WorkingDirectory) && CurrentDirectory == "/")
        {
            CurrentDirectory = _sftp.WorkingDirectory;
        }
    }

    public Task<List<RemoteFileEntry>> ListDirectoryAsync(string path, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (_sftp == null || !_sftp.IsConnected)
                throw new InvalidOperationException("SFTP 未连接。");

            var target = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
            var items = _sftp.ListDirectory(target)
                .Where(f => f.Name is not "." and not "..")
                .Select(f => new RemoteFileEntry(
                    f.Name,
                    f.FullName,
                    f.IsDirectory,
                    f.Length,
                    f.LastWriteTime))
                .OrderByDescending(f => f.IsDirectory)
                .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            CurrentDirectory = target;
            return items;
        }, ct);
    }

    public Task DownloadFileAsync(string remotePath, string localPath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (_sftp == null) throw new InvalidOperationException("SFTP 未连接。");
            var dir = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            using var fs = File.Create(localPath);
            _sftp.DownloadFile(remotePath, fs);
        }, ct);
    }

    public Task UploadFileAsync(string localPath, string remotePath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (_sftp == null) throw new InvalidOperationException("SFTP 未连接。");
            using var fs = File.OpenRead(localPath);
            _sftp.UploadFile(fs, remotePath, canOverride: true);
        }, ct);
    }

    public Task CreateDirectoryAsync(string remotePath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (_sftp == null) throw new InvalidOperationException("SFTP 未连接。");
            _sftp.CreateDirectory(remotePath);
        }, ct);
    }

    public Task DeleteAsync(RemoteFileEntry item, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (_sftp == null) throw new InvalidOperationException("SFTP 未连接。");
            if (item.IsDirectory)
                _sftp.DeleteDirectory(item.FullPath);
            else
                _sftp.DeleteFile(item.FullPath);
        }, ct);
    }

    public void Dispose()
    {
        try { _sftp?.Disconnect(); } catch { /* ignore */ }
        try { _sftp?.Dispose(); } catch { /* ignore */ }
        _sftp = null;
    }
}

public sealed class FtpTransferSession : IFileTransferSession
{
    private readonly string _host;
    private readonly int _port;
    private readonly Credential _cred;
    private AsyncFtpClient? _ftp;

    public bool IsConnected => _ftp?.IsConnected == true;
    public string CurrentDirectory { get; private set; }

    public FtpTransferSession(string host, int port, Credential cred, string startupPath = "/")
    {
        _host = host;
        _port = port;
        _cred = cred;
        CurrentDirectory = string.IsNullOrWhiteSpace(startupPath) ? "/" : startupPath;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var user = string.IsNullOrWhiteSpace(_cred.UserName) ? "anonymous" : _cred.UserName;
        _ftp = new AsyncFtpClient(_host, user, _cred.Password, _port);
        _ftp.Config.ValidateAnyCertificate = true;
        await _ftp.AutoConnect(ct);
    }

    public async Task<List<RemoteFileEntry>> ListDirectoryAsync(string path, CancellationToken ct = default)
    {
        if (_ftp == null) throw new InvalidOperationException("FTP 未连接。");
        var target = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
        var listing = await _ftp.GetListing(target, ct);
        CurrentDirectory = target;
        return listing
            .Where(x => x.Name is not "." and not "..")
            .Select(x => new RemoteFileEntry(
                x.Name,
                x.FullName,
                x.Type == FtpObjectType.Directory,
                x.Size,
                x.Modified))
            .OrderByDescending(x => x.IsDirectory)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task DownloadFileAsync(string remotePath, string localPath, CancellationToken ct = default)
    {
        if (_ftp == null) throw new InvalidOperationException("FTP 未连接。");
        await _ftp.DownloadFile(localPath, remotePath, FtpLocalExists.Overwrite, FtpVerify.None, null, ct);
    }

    public async Task UploadFileAsync(string localPath, string remotePath, CancellationToken ct = default)
    {
        if (_ftp == null) throw new InvalidOperationException("FTP 未连接。");
        await _ftp.UploadFile(localPath, remotePath, FtpRemoteExists.Overwrite, createRemoteDir: true, FtpVerify.None, null, ct);
    }

    public async Task CreateDirectoryAsync(string remotePath, CancellationToken ct = default)
    {
        if (_ftp == null) throw new InvalidOperationException("FTP 未连接。");
        await _ftp.CreateDirectory(remotePath, ct);
    }

    public async Task DeleteAsync(RemoteFileEntry item, CancellationToken ct = default)
    {
        if (_ftp == null) throw new InvalidOperationException("FTP 未连接。");
        if (item.IsDirectory)
            await _ftp.DeleteDirectory(item.FullPath, ct);
        else
            await _ftp.DeleteFile(item.FullPath, ct);
    }

    public void Dispose()
    {
        try { _ftp?.Dispose(); } catch { /* ignore */ }
        _ftp = null;
    }
}
