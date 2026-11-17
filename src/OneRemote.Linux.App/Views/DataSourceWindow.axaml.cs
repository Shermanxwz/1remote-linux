using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OneRemote.Linux.Core.Config;
using OneRemote.Linux.Core.DataSources;
using OneRemote.Linux.Core.Security;

namespace OneRemote.Linux.App.Views;

public partial class DataSourceWindow : Window
{
    private readonly DataSourceHub _hub;
    private readonly LinuxAppConfig _config;

    public DataSourceWindow() : this(new DataSourceHub(), new LinuxAppConfig())
    {
    }

    public DataSourceWindow(DataSourceHub hub, LinuxAppConfig config)
    {
        InitializeComponent();
        _hub = hub;
        _config = config;

        DbTypeCombo.ItemsSource = new[] { "MySQL", "PostgreSQL" };
        DbTypeCombo.SelectedIndex = 0;
        SaltBox.Text = _config.EncryptionSalt;

        RefreshSourceList();
    }

    private void RefreshSourceList()
    {
        SourcesList.ItemsSource = _hub.AllSources
            .Select(s => $"{s.DataSourceName} — {s.Description} [{s.Status}]")
            .ToList();
    }

    private void OnSourceSelected(object? sender, SelectionChangedEventArgs e)
    {
        var idx = SourcesList.SelectedIndex;
        if (idx < 0 || idx >= _hub.AllSources.Count) return;
        var src = _hub.AllSources[idx];

        if (src is MysqlSource my)
        {
            SourceNameBox.Text = my.DataSourceName;
            DbTypeCombo.SelectedItem = "MySQL";
            HostBox.Text = my.Host;
            PortBox.Text = my.Port.ToString();
            DbNameBox.Text = my.DatabaseName;
            UserBox.Text = my.UserName;
            PasswordBox.Text = my.Password;
        }
        else if (src is PgsqlSource pg)
        {
            SourceNameBox.Text = pg.DataSourceName;
            DbTypeCombo.SelectedItem = "PostgreSQL";
            HostBox.Text = pg.Host;
            PortBox.Text = pg.Port.ToString();
            DbNameBox.Text = pg.DatabaseName;
            UserBox.Text = pg.UserName;
            PasswordBox.Text = pg.Password;
        }
    }

    private DataSourceBase BuildCandidateSource()
    {
        var salt = string.IsNullOrWhiteSpace(SaltBox.Text) ? UnSafeStringEncipher.DEFAULT_SALT : SaltBox.Text;
        UnSafeStringEncipher.Init(salt);
        _config.EncryptionSalt = salt;

        var dbType = DbTypeCombo.SelectedItem as string ?? "MySQL";
        var port = int.TryParse(PortBox.Text, out var p) ? p : (dbType == "PostgreSQL" ? 5432 : 3306);
        var name = string.IsNullOrWhiteSpace(SourceNameBox.Text) ? dbType : SourceNameBox.Text.Trim();

        if (dbType == "PostgreSQL")
        {
            return new PgsqlSource
            {
                DataSourceName = name,
                Host = (HostBox.Text ?? "127.0.0.1").Trim(),
                Port = port,
                DatabaseName = (DbNameBox.Text ?? "1remote").Trim(),
                UserName = (UserBox.Text ?? string.Empty).Trim(),
                Password = PasswordBox.Text ?? string.Empty,
            };
        }

        return new MysqlSource
        {
            DataSourceName = name,
            Host = (HostBox.Text ?? "127.0.0.1").Trim(),
            Port = port,
            DatabaseName = (DbNameBox.Text ?? "1remote").Trim(),
            UserName = (UserBox.Text ?? string.Empty).Trim(),
            Password = PasswordBox.Text ?? string.Empty,
        };
    }

    private void OnTestConnectionClick(object? sender, RoutedEventArgs e)
    {
        var candidate = BuildCandidateSource();
        var check = candidate.SelfCheck(10);
        if (check.IsOk)
        {
            var servers = candidate.LoadServers(true);
            StatusLabel.Text = $"连接与加解密校验成功！读取到 {servers.Count} 台设备。";
        }
        else
        {
            StatusLabel.Text = $"连接校验未通过 [{check.Status}]：{check.ErrorMessage}";
        }
    }

    private void OnSaveSourceClick(object? sender, RoutedEventArgs e)
    {
        var candidate = BuildCandidateSource();
        _config.Save();
        var check = _hub.AddOrUpdateRemoteSource(candidate);
        _hub.SaveRemoteSourcesToJsonFile(LinuxAppConfig.GetAdditionalSourcesPath());
        RefreshSourceList();

        if (check.IsOk)
        {
            var count = candidate.LoadServers(true).Count;
            StatusLabel.Text = $"已保存并同步 {candidate.DataSourceName}（共 {count} 台设备）。";
        }
        else
        {
            StatusLabel.Text = $"已保存配置，但当前连接状态为 {check.Status}：{check.ErrorMessage}";
        }
    }

    private async void OnImportJsonClick(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 Windows 1Remote 的 AdditionalDataSource.json",
            AllowMultiple = false
        });
        var path = picked.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        var imported = DataSourceHub.LoadSourcesFromJsonFile(path);
        foreach (var src in imported)
        {
            _hub.AddOrUpdateRemoteSource(src);
        }
        _hub.SaveRemoteSourcesToJsonFile(LinuxAppConfig.GetAdditionalSourcesPath());
        RefreshSourceList();
        StatusLabel.Text = $"成功导入 {imported.Count} 个远程数据源配置。";
    }

    private void OnRemoveSelectedSourceClick(object? sender, RoutedEventArgs e)
    {
        var idx = SourcesList.SelectedIndex;
        if (idx < 0 || idx >= _hub.AllSources.Count) return;
        var src = _hub.AllSources[idx];
        if (src.DataSourceName == "Local")
        {
            StatusLabel.Text = "本地默认 SQLite 数据源不可删除。";
            return;
        }
        _hub.RemoveRemoteSource(src.DataSourceName);
        _hub.SaveRemoteSourcesToJsonFile(LinuxAppConfig.GetAdditionalSourcesPath());
        RefreshSourceList();
        StatusLabel.Text = $"已移除数据源 {src.DataSourceName}。";
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
