using Avalonia;

namespace Comet.App;

internal static class Program
{
    // Avalonia must not be touched before AppMain runs: no UI types in static initializers here.
    [STAThread]
    public static int Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
