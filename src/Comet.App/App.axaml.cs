using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Comet.Core.Configuration;

namespace Comet.App;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppPaths paths = AppPaths.Default();
            AppSettings settings = SettingsStore.Load(paths, out string? warning);
            desktop.MainWindow = new MainWindow(settings, paths, warning);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
