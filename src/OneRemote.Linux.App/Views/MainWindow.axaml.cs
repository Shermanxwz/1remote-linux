using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OneRemote.Linux.App.ViewModels;
using OneRemote.Linux.Core.Models;

namespace OneRemote.Linux.App.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    public MainWindow()
    {
        InitializeComponent();
        KeyDown += OnWindowKeyDown;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.K)
        {
            SearchBox.Focus();
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.R)
        {
            Vm?.ReloadServerCatalog(true);
            e.Handled = true;
        }
    }

    private async void OnServerDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm?.SelectedServer != null)
            await Vm.ConnectServerAsync(Vm.SelectedServer);
    }

    private async void OnQuickConnectItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: ProtocolBase srv } && Vm != null)
            await Vm.ConnectServerAsync(srv);
    }

    private async void OnConnectRowClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProtocolBase srv } && Vm != null)
            await Vm.ConnectServerAsync(srv);
    }

    private async void OnEditRowClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProtocolBase srv } && Vm != null)
        {
            var dlg = new ServerEditorWindow(Vm.Hub, srv);
            var saved = await dlg.ShowDialog<bool>(this);
            if (saved)
                Vm.ReloadServerCatalog(true);
        }
    }

    private async void OnAddServerClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var dlg = new ServerEditorWindow(Vm.Hub, existing: null);
        var saved = await dlg.ShowDialog<bool>(this);
        if (saved)
            Vm.ReloadServerCatalog(true);
    }

    private async void OnOpenDataSourcesClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var dlg = new DataSourceWindow(Vm.Hub, Vm.AppConfig);
        await dlg.ShowDialog(this);
        Vm.ReloadServerCatalog(true);
    }

    private void OnOpenSftpSelectedClick(object? sender, RoutedEventArgs e) =>
        Vm?.OpenSftpForServer(Vm.SelectedServer);

    private void OnDeleteSelectedClick(object? sender, RoutedEventArgs e) =>
        Vm?.DeleteSelectedServer(Vm.SelectedServer);

    private void OnSelectTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SessionTabViewModel tab } && Vm != null)
            Vm.SelectedTab = tab;
    }

    private async void OnSshCommandKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: SshSessionTabViewModel sshVm })
            return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await sshVm.SendCommandAsync();
        }
        else if (e.Key == Key.Up)
        {
            e.Handled = true;
            sshVm.RecallHistory(-1);
        }
        else if (e.Key == Key.Down)
        {
            e.Handled = true;
            sshVm.RecallHistory(1);
        }
    }

    private async void OnSftpPathKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox { DataContext: FileBrowserTabViewModel fileVm })
        {
            e.Handled = true;
            await fileVm.RefreshAsync();
        }
    }

    private async void OnRemoteFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { DataContext: FileBrowserTabViewModel fileVm } && fileVm.SelectedEntry != null)
        {
            await fileVm.EnterEntryAsync(fileVm.SelectedEntry);
        }
    }

    private async void OnUploadFileClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FileBrowserTabViewModel fileVm })
            return;
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要上传到远程服务器的文件",
            AllowMultiple = false
        });
        var file = picked.FirstOrDefault();
        var localPath = file?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(localPath))
        {
            await fileVm.UploadLocalFileAsync(localPath);
        }
    }

    private async void OnDownloadFileClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FileBrowserTabViewModel fileVm } || fileVm.SelectedEntry == null || fileVm.SelectedEntry.IsDirectory)
            return;

        var saveFile = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存远程文件到本地",
            SuggestedFileName = fileVm.SelectedEntry.Name
        });
        var localPath = saveFile?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(localPath))
        {
            await fileVm.DownloadSelectedToAsync(localPath);
        }
    }
}
