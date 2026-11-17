using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OneRemote.Linux.App.ViewModels;
using OneRemote.Linux.App.Views;

namespace OneRemote.Linux.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainWindowViewModel();
            desktop.MainWindow = new MainWindow
            {
                DataContext = vm
            };
            desktop.Exit += (_, _) => vm.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
