using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OneRemote.Linux.Core.DataSources;
using OneRemote.Linux.Core.Models;

namespace OneRemote.Linux.App.Views;

public partial class ServerEditorWindow : Window
{
    private readonly DataSourceHub _hub;
    private readonly ProtocolBase? _existing;
    private readonly List<DataSourceBase> _writableSources;

    public ServerEditorWindow() : this(new DataSourceHub(), null)
    {
    }

    public ServerEditorWindow(DataSourceHub hub, ProtocolBase? existing)
    {
        InitializeComponent();
        _hub = hub;
        _existing = existing;

        _writableSources = _hub.AllSources.Where(x => x.IsWritable).ToList();
        SourceCombo.ItemsSource = _writableSources.Select(x => x.DataSourceName).ToList();
        ProtocolCombo.ItemsSource = new[] { "SSH", "RDP", "SFTP", "FTP", "VNC" };

        if (existing != null)
        {
            PopulateFromExisting(existing);
        }
        else
        {
            SourceCombo.SelectedIndex = _writableSources.Count > 1 ? 1 : 0; // Prefer remote MySQL when configured
            ProtocolCombo.SelectedItem = "SSH";
        }
    }

    private void PopulateFromExisting(ProtocolBase srv)
    {
        var clone = srv.Clone();
        clone.DecryptToConnectLevel();

        var srcIdx = _writableSources.FindIndex(x => x.DataSourceName == srv.DataSource?.DataSourceName);
        SourceCombo.SelectedIndex = srcIdx >= 0 ? srcIdx : 0;

        ProtocolCombo.SelectedItem = clone.Protocol;
        ProtocolCombo.IsEnabled = false;
        DisplayNameBox.Text = clone.DisplayName;
        TagsBox.Text = string.Join(", ", clone.Tags);
        NoteBox.Text = clone.Note;

        if (clone is ProtocolBaseWithAddressPort ap)
        {
            AddressBox.Text = ap.Address;
            PortBox.Text = ap.Port;
        }

        if (clone is ProtocolBaseWithAddressPortUserPwd up)
        {
            UserBox.Text = up.UserName;
            PasswordBox.Text = up.Password;
            PrivateKeyBox.Text = up.PrivateKey;
        }

        if (clone is SSH ssh)
        {
            StartupCmdBox.Text = ssh.StartupAutoCommand;
            OpenSftpCheck.IsChecked = ssh.OpenSftpOnConnected;
        }
        else if (clone is RDP rdp)
        {
            DomainBox.Text = rdp.Domain;
            RdpClipboardCheck.IsChecked = rdp.EnableClipboard != false;
            RdpFullScreenCheck.IsChecked = rdp.IsConnWithFullScreen == true;
            RdpDriveCheck.IsChecked = rdp.EnableDiskDrives == true;
        }
    }

    private void OnProtocolSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var proto = ProtocolCombo.SelectedItem as string ?? "SSH";
        SshPanel.IsVisible = proto is "SSH" or "SFTP";
        RdpPanel.IsVisible = proto == "RDP";

        if (_existing == null)
        {
            PortBox.Text = proto switch
            {
                "RDP" => "3389",
                "FTP" => "21",
                "VNC" => "5900",
                _ => "22"
            };
            if (proto == "RDP" && UserBox.Text == "root")
                UserBox.Text = "Administrator";
            else if (proto is "SSH" or "SFTP" && UserBox.Text == "Administrator")
                UserBox.Text = "root";
        }
    }

    private async void OnBrowseKeyClick(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 SSH 私钥文件",
            AllowMultiple = false
        });
        var path = picked.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
            PrivateKeyBox.Text = path;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var host = (AddressBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(host))
        {
            ErrorText.Text = "请填写主机地址 / IP。";
            return;
        }

        var protoName = ProtocolCombo.SelectedItem as string ?? "SSH";
        ProtocolBase model = _existing?.Clone() ?? protoName switch
        {
            "RDP" => new RDP(),
            "SFTP" => new SFTP(),
            "FTP" => new FTP(),
            "VNC" => new VNC(),
            _ => new SSH(),
        };

        model.DecryptToConnectLevel();
        model.DisplayName = string.IsNullOrWhiteSpace(DisplayNameBox.Text) ? host : DisplayNameBox.Text.Trim();
        model.Tags = (TagsBox.Text ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .ToList();
        model.Note = NoteBox.Text ?? string.Empty;

        if (model is ProtocolBaseWithAddressPort ap)
        {
            ap.Address = host;
            ap.Port = string.IsNullOrWhiteSpace(PortBox.Text) ? "22" : PortBox.Text.Trim();
        }

        if (model is ProtocolBaseWithAddressPortUserPwd up)
        {
            up.UserName = (UserBox.Text ?? string.Empty).Trim();
            up.Password = PasswordBox.Text ?? string.Empty;
            up.PrivateKey = (PrivateKeyBox.Text ?? string.Empty).Trim();
            up.UsePrivateKeyForConnect = !string.IsNullOrEmpty(up.PrivateKey);
        }

        if (model is SSH ssh)
        {
            ssh.StartupAutoCommand = StartupCmdBox.Text ?? string.Empty;
            ssh.OpenSftpOnConnected = OpenSftpCheck.IsChecked == true;
        }
        else if (model is RDP rdp)
        {
            rdp.Domain = (DomainBox.Text ?? string.Empty).Trim();
            rdp.EnableClipboard = RdpClipboardCheck.IsChecked == true;
            rdp.IsConnWithFullScreen = RdpFullScreenCheck.IsChecked == true;
            rdp.RdpWindowResizeMode = rdp.IsConnWithFullScreen == true
                ? ERdpWindowResizeMode.StretchFullScreen
                : ERdpWindowResizeMode.AutoResize;
            rdp.EnableDiskDrives = RdpDriveCheck.IsChecked == true;
        }

        var selectedSrcName = SourceCombo.SelectedItem as string;
        var targetSource = _writableSources.FirstOrDefault(x => x.DataSourceName == selectedSrcName)
                           ?? _writableSources.FirstOrDefault();

        if (targetSource == null)
        {
            ErrorText.Text = "未找到可写入的数据源。";
            return;
        }

        var res = targetSource.SaveServer(model);
        if (!res.IsSuccess)
        {
            ErrorText.Text = $"保存失败：{res.ErrorInfo}";
            return;
        }

        Close(true);
    }
}
