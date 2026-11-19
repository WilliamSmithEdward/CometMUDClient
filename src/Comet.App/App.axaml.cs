using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Comet.Core.Configuration;

namespace Comet.App;

public sealed partial class App : Application
{
    /// <summary>The folders this run reads and writes. Set once at startup.</summary>
    public static AppPaths Paths { get; private set; } = AppPaths.Default();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Paths = AppPaths.Resolve(
                desktop.Args ?? [],
                Environment.GetEnvironmentVariable(AppPaths.OverrideVariable),
                out string? pathWarning);
            AppSettings settings = SettingsStore.Load(Paths, out string? settingsWarning);
            string? warning = pathWarning is null ? settingsWarning : $"{pathWarning} {settingsWarning}".Trim();
            desktop.MainWindow = new MainWindow(settings, Paths, warning);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
