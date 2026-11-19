using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Comet.App.Dialogs;
using Comet.Core.Configuration;
using Comet.Core.Sessions;

namespace Comet.App.Tests;

/// <summary>Entry point the headless session uses to build the real application with real (Skia) drawing.</summary>
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Drives the real windows without a display. Each test also saves a screenshot under
/// artifacts/screenshots so the rendering can be reviewed by eye.
/// </summary>
public sealed class UiTests : IDisposable
{
    private static readonly HeadlessUnitTestSession Ui = HeadlessUnitTestSession.StartNew(typeof(TestApp));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "comet-ui-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public UiTests()
    {
        _paths = new AppPaths(_root, Path.Combine(_root, "scripts"), Path.Combine(_root, "logs"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string ScreenshotPath(string name)
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Comet.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }
        string folder = Path.Combine(directory ?? AppContext.BaseDirectory, "artifacts", "screenshots");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, name + ".png");
    }

    private static void Capture(TopLevel window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(frame.PixelSize.Width > 100 && frame.PixelSize.Height > 100);
        frame.Save(ScreenshotPath(name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > 15_000)
            {
                throw new TimeoutException("Timed out waiting for " + what);
            }
            await Task.Delay(20).ConfigureAwait(true);
        }
    }

    private static string[] Lines(MudSession session) =>
        [.. Enumerable.Range(0, session.Buffer.Count).Select(i => session.Buffer[i].Text)];

    [Fact]
    public async Task SessionShowsOutputSendsInputAndScrollsBack()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = new List<string>();

        Task server = Task.Run(async () =>
        {
            using TcpClient client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            NetworkStream stream = client.GetStream();
            var greeting = new StringBuilder();
            greeting.Append("\x1b[1;33mWelcome to the Test MUD\x1b[0m\r\n");
            greeting.Append("\x1b[31mred \x1b[32mgreen \x1b[33myellow \x1b[34mblue \x1b[35mmagenta \x1b[36mcyan \x1b[37mwhite \x1b[1;31mbright \x1b[0;4munderline\x1b[0m \x1b[7minverse\x1b[0m \x1b[38;5;208m256-color\x1b[0m \x1b[38;2;120;200;255mtruecolor\x1b[0m\r\n");
            greeting.Append("Help is at https://example.org/help for new players.\r\n");
            greeting.Append("An orc arrives from the north.\r\n");
            greeting.Append("<100hp 50mv> ");
            await stream.WriteAsync(Encoding.UTF8.GetBytes(greeting.ToString())).ConfigureAwait(false);
            await stream.WriteAsync(new byte[] { 255, 249 }).ConfigureAwait(false);

            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (received)
                {
                    received.Add(line);
                }
                var reply = new StringBuilder();
                if (line == "look")
                {
                    reply.Append("\x1b[1;37mThe Town Square\x1b[0m\r\n");
                    for (int i = 1; i <= 80; i++)
                    {
                        reply.Append("Line ").Append(i).Append(" of a long description that is here to fill the scrollback buffer.\r\n");
                    }
                }
                reply.Append("<100hp 50mv> ");
                await stream.WriteAsync(Encoding.UTF8.GetBytes(reply.ToString())).ConfigureAwait(false);
                await stream.WriteAsync(new byte[] { 255, 249 }).ConfigureAwait(false);
            }
        });

        await Ui.Dispatch(async () =>
        {
            var settings = new AppSettings { ConfirmCloseWhenConnected = false };
            var profile = new SessionProfile { Name = "Test MUD", Host = "127.0.0.1", Port = port };
            settings.Profiles.Add(profile);
            var window = new MainWindow(settings, _paths, null);
            window.Show();
            Capture(window, "01-empty-state");

            window.OpenSession(profile);
            MudSession session = window.ActiveView!.Session;
            session.Engine.Execute("#highlight {light red} {orc};#message off");
            await WaitForAsync(() => Lines(session).Contains("<100hp 50mv> "), "the greeting and prompt").ConfigureAwait(true);
            Assert.Equal(SessionState.Connected, session.State);
            Capture(window, "02-connected");

            window.KeyTextInput("look");
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await WaitForAsync(() => Lines(session).Any(line => line.StartsWith("Line 80 ", StringComparison.Ordinal)), "the room description").ConfigureAwait(true);
            lock (received)
            {
                Assert.Equal(["look"], received);
            }
            Assert.Contains("<100hp 50mv> look", Lines(session));
            Capture(window, "03-after-look");

            window.KeyPressQwerty(PhysicalKey.PageUp, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.PageUp, RawInputModifiers.None);
            Capture(window, "04-scrolled-back");

            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await WaitForAsync(() => { lock (received) { return received.Count == 2; } }, "the recalled command").ConfigureAwait(true);
            lock (received)
            {
                Assert.Equal("look", received[1]);
            }

            window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
            window.KeyTextInput("orc");
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Capture(window, "05-find");

            window.Close();
            return true;
        }, CancellationToken.None).ConfigureAwait(true);

        listener.Stop();
        await Task.WhenAny(server, Task.Delay(2000)).ConfigureAwait(true);
    }

    [Fact]
    public async Task DialogsOpenAndRender()
    {
        await Ui.Dispatch(async () =>
        {
            var settings = new AppSettings();
            settings.Profiles.Add(new SessionProfile { Name = "Aardwolf", Host = "aardmud.org", Port = 4000 });
            settings.Profiles.Add(new SessionProfile { Name = "Secure MUD", Host = "mud.example.org", Port = 4443, UseTls = true });

            var connect = new ConnectDialog(settings, static () => { });
            connect.Show();
            Capture(connect, "10-connect-dialog");
            connect.Close();
            Assert.Equal(2, settings.Profiles.Count);

            var preferences = new PreferencesDialog(settings);
            preferences.Show();
            Capture(preferences, "11-preferences");
            preferences.Close();

            await using var session = new MudSession(settings.Profiles[0], settings, _paths);
            session.Engine.ShowMessages = false;
            session.Engine.Execute("#alias {k} {kill %1;loot corpse};#alias {gt} {gtell};#action {^%1 tells you '%2'} {reply %1 hello} {5};#highlight {light red} {orc};#var target orc");
            var scripts = new ScriptDialog(session);
            scripts.Show();
            Assert.Equal(2, scripts.ItemList.ItemCount);
            Assert.Equal("gt", scripts.KeyBox.Text);
            Capture(scripts, "12-script-editor");
            scripts.Close();

            var help = new HelpWindow('#');
            help.Show();
            Capture(help, "13-help");
            help.Close();

            var about = new AboutDialog();
            about.Show();
            Capture(about, "14-about");
            about.Close();
            return true;
        }, CancellationToken.None).ConfigureAwait(true);
    }
}
