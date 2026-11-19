using Avalonia;
using Velopack;

namespace Comet.App;

internal static class Program
{
    // Avalonia must not be touched before AppMain runs: no UI types in static initializers here.
    [STAThread]
    public static int Main(string[] args)
    {
        // Must run first. When the installer starts the app to finish an install, update or
        // uninstall, this handles the request and exits before any window opens.
        VelopackApp.Build().Run();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
