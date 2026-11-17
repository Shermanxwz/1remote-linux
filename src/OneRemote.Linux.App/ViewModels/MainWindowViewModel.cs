using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OneRemote.Linux.Core.Config;
using OneRemote.Linux.Core.Connections;
using OneRemote.Linux.Core.DataSources;
using OneRemote.Linux.Core.Models;
using OneRemote.Linux.Core.Search;
using OneRemote.Linux.Core.Security;

namespace OneRemote.Linux.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    public DataSourceHub Hub { get; } = new();
    public LinuxAppConfig AppConfig { get; private set; }

    private List<ProtocolBase> _allServers = new();
    private List<Credential> _allCredentials = new();

    public ObservableCollection<ProtocolBase> FilteredServers { get; } = new();
    public ObservableCollection<string> AvailableTags { get; } = new() { "ALL" };
    public ObservableCollection<SessionTabViewModel> OpenTabs { get; } = new();

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private string _selectedProtocolFilter = "ALL";

    [ObservableProperty]
    private string _selectedTagFilter = "ALL";

    [ObservableProperty]
    private ProtocolBase? _selectedServer;

    [ObservableProperty]
    private SessionTabViewModel? _selectedTab;

    [ObservableProperty]
    private string _syncBannerText = "正在初始化本地与云端数据源...";

    [ObservableProperty]
    private string _environmentBadge = string.Empty;

    [ObservableProperty]
    private bool _isQuickConnectOpen;

    public string[] ProtocolFilters { get; } = ["ALL", "SSH", "RDP", "SFTP", "FTP", "VNC"];

    public MainWindowViewModel(bool skipDiskInit = false)
    {
        AppConfig = skipDiskInit ? new LinuxAppConfig() : LinuxAppConfig.Load();
        UnSafeStringEncipher.Init(AppConfig.EncryptionSalt);

        var env = FreeRdpLauncher.DetectDesktopEnvironment();
        var rdpBin = FreeRdpLauncher.ResolveBinary(AppConfig.PreferredFreeRdpBinary) ?? "未安装 xfreerdp3/xfreerdp";
        EnvironmentBadge = $"Ubuntu/{env.DisplayServer} · RDP: {rdpBin}";

        if (!skipDiskInit)
        {
            InitializeDataSources();
        }
    }

    private void InitializeDataSources()
    {
        Hub.InitLocalSqlite(LinuxAppConfig.GetLocalDatabasePath());

        var remoteSources = DataSourceHub.LoadSourcesFromJsonFile(LinuxAppConfig.GetAdditionalSourcesPath());
        foreach (var src in remoteSources)
        {
            Hub.AddOrUpdateRemoteSource(src);
        }

        Hub.RemoteChangesDetected += () =>
        {
            Dispatcher.UIThread.Post(() => ReloadServerCatalog(force: false));
        };

        ReloadServerCatalog(force: true);
        Hub.StartBackgroundSync(TimeSpan.FromSeconds(Math.Max(5, AppConfig.SyncIntervalSeconds)));
    }

    partial void OnSearchQueryChanged(string value) => ApplyFilter();
    partial void OnSelectedProtocolFilterChanged(string value) => ApplyFilter();
    partial void OnSelectedTagFilterChanged(string value) => ApplyFilter();

    [RelayCommand]
    public void SyncNow() => ReloadServerCatalog(force: true);

    public void ReloadServerCatalog(bool force = true)
    {
        _allServers = Hub.GetAllServers(force);
        _allCredentials = Hub.GetAllCredentials(force);

        var currentTag = SelectedTagFilter;
        var tags = _allServers
            .SelectMany(s => s.Tags.Concat(s.TreeNodes))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

        AvailableTags.Clear();
        AvailableTags.Add("ALL");
        foreach (var t in tags)
            AvailableTags.Add(t);

        SelectedTagFilter = AvailableTags.Contains(currentTag) ? currentTag : "ALL";
        ApplyFilter();

        var sourceSummaries = Hub.AllSources
            .Select(s => $"{s.DataSourceName}: {(s.Status == EnumDatabaseStatus.OK ? $"{s.CachedServers.Count} 台" : s.Status.ToString())}");
        SyncBannerText = $"同步就绪 · 共 {_allServers.Count} 台设备（{string.Join(" | ", sourceSummaries)}）";
    }

    public void ApplyFilter()
    {
        var list = ServerFilterService.FilterAndSort(_allServers, SearchQuery, SelectedProtocolFilter, SelectedTagFilter);
        FilteredServers.Clear();
        foreach (var srv in list)
            FilteredServers.Add(srv);
    }

    [RelayCommand]
    public void ToggleQuickConnect() => IsQuickConnectOpen = !IsQuickConnectOpen;

    [RelayCommand]
    public async Task ConnectServerAsync(ProtocolBase? server)
    {
        var target = server ?? SelectedServer;
        if (target == null)
            return;

        IsQuickConnectOpen = false;
        var cred = (target as ProtocolBaseWithAddressPortUserPwd)?.ResolveEffectiveCredential(_allCredentials)
                   ?? new Credential();

        switch (target)
        {
            case SSH ssh:
            {
                var tab = new SshSessionTabViewModel(ssh, cred, OpenSftpForServer)
                {
                    CloseRequested = CloseTab
                };
                OpenTabs.Add(tab);
                SelectedTab = tab;
                await tab.ConnectAsync();
                break;
            }
            case SFTP or FTP:
            {
                var p = (ProtocolBaseWithAddressPortUserPwd)target;
                var tab = new FileBrowserTabViewModel(p, cred)
                {
                    CloseRequested = CloseTab
                };
                OpenTabs.Add(tab);
                SelectedTab = tab;
                await tab.ConnectAndListAsync();
                break;
            }
            case RDP rdp:
            {
                var tab = new RdpSessionTabViewModel(rdp, cred, AppConfig.PreferredFreeRdpBinary)
                {
                    CloseRequested = CloseTab
                };
                OpenTabs.Add(tab);
                SelectedTab = tab;
                tab.LaunchRdp();
                break;
            }
            default:
                SyncBannerText = $"暂未内置 {target.Protocol} 原生会话器，已保留完整数据库配置。";
                break;
        }
    }

    [RelayCommand]
    public async void OpenSftpForServer(ProtocolBase? server)
    {
        var target = server ?? SelectedServer;
        if (target is SSH ssh)
        {
            var sftpClone = new SFTP
            {
                Id = ssh.Id,
                DisplayName = $"{ssh.DisplayName} (SFTP)",
                Address = ssh.Address,
                Port = ssh.Port,
                UserName = ssh.UserName,
                Password = ssh.Password,
                PrivateKey = ssh.PrivateKey,
                InheritedCredentialName = ssh.InheritedCredentialName,
                StartupPath = "/"
            };
            var cred = sftpClone.ResolveEffectiveCredential(_allCredentials);
            var tab = new FileBrowserTabViewModel(sftpClone, cred)
            {
                CloseRequested = CloseTab
            };
            OpenTabs.Add(tab);
            SelectedTab = tab;
            await tab.ConnectAndListAsync();
        }
        else if (target is SFTP or FTP)
        {
            await ConnectServerAsync(target);
        }
    }

    public void CloseTab(SessionTabViewModel tab)
    {
        tab.Dispose();
        OpenTabs.Remove(tab);
        SelectedTab = OpenTabs.LastOrDefault();
    }

    [RelayCommand]
    public void DeleteSelectedServer(ProtocolBase? server)
    {
        var target = server ?? SelectedServer;
        if (target == null) return;
        var ds = target.DataSource ?? Hub.LocalSource;
        if (ds == null) return;

        var res = ds.DeleteServer(target.Id);
        if (res.IsSuccess)
        {
            ReloadServerCatalog(force: true);
        }
        else
        {
            SyncBannerText = $"删除失败：{res.ErrorInfo}";
        }
    }

    public void Dispose()
    {
        foreach (var tab in OpenTabs)
            tab.Dispose();
        Hub.Dispose();
    }
}
