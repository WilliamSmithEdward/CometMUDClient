using System.Text.Json;
using System.Text.Json.Serialization;

namespace Comet.Core.Configuration;

/// <summary>A saved MUD connection. Passwords are never stored.</summary>
public sealed class SessionProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 4000;
    public bool UseTls { get; set; }
    public bool AcceptInvalidCertificate { get; set; }
    public string Encoding { get; set; } = "utf-8";
    public bool AutoReconnect { get; set; }

    /// <summary>Script loaded when the session opens. Empty means the profile's own file in the scripts folder.</summary>
    public string ScriptFile { get; set; } = string.Empty;

    public string Notes { get; set; } = string.Empty;

    public SessionProfile Clone() => (SessionProfile)MemberwiseClone();

    /// <summary>Returns a problem with the profile that would stop it connecting, or null.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            return "Enter the MUD's address.";
        }
        if (Uri.CheckHostName(Host.Trim()) == UriHostNameType.Unknown)
        {
            return "The address is not a valid host name or IP address.";
        }
        if (Port is < 1 or > 65_535)
        {
            return "The port must be from 1 to 65535.";
        }
        return null;
    }
}

/// <summary>User preferences, saved as JSON in the configuration folder.</summary>
public sealed class AppSettings
{
    public const string DefaultFontFamily = "Cascadia Mono,Consolas,Menlo,DejaVu Sans Mono,Liberation Mono,monospace";

    /// <summary>The 16 ANSI colors, normal then bright, as #RRGGBB.</summary>
    public static IReadOnlyList<string> DefaultPalette { get; } =
    [
        "#000000", "#BB0000", "#00BB00", "#BBBB00", "#2472C8", "#BB00BB", "#00BBBB", "#BBBBBB",
        "#555555", "#FF5555", "#55FF55", "#FFFF55", "#5555FF", "#FF55FF", "#55FFFF", "#FFFFFF",
    ];

    public string FontFamily { get; set; } = DefaultFontFamily;
    public double FontSize { get; set; } = 13;
    public string Foreground { get; set; } = "#BBBBBB";
    public string Background { get; set; } = "#000000";
    public List<string> Palette { get; set; } = [.. DefaultPalette];

    /// <summary>Draw bold text in the bright variant of its color, as terminals traditionally do.</summary>
    public bool BoldAsBright { get; set; } = true;

    public int ScrollbackLines { get; set; } = 50_000;
    public bool WordWrap { get; set; } = true;

    /// <summary>Show the commands sent to the MUD in the output.</summary>
    public bool EchoCommands { get; set; } = true;

    /// <summary>Leave the sent command selected in the input box, so Enter repeats it.</summary>
    public bool KeepInputAfterSend { get; set; }

    public bool AudibleBell { get; set; } = true;
    public bool ConfirmCloseWhenConnected { get; set; } = true;
    public bool ShowToolbar { get; set; } = true;
    public bool ShowStatusBar { get; set; } = true;

    public double WindowWidth { get; set; } = 1000;
    public double WindowHeight { get; set; } = 700;
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public bool WindowMaximized { get; set; }

    public List<SessionProfile> Profiles { get; set; } = [];

    /// <summary>Clamps values a hand-edited or damaged file may have pushed out of range.</summary>
    public void Normalize()
    {
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? DefaultFontFamily : FontFamily;
        FontSize = double.IsFinite(FontSize) ? Math.Clamp(FontSize, 6, 72) : 13;
        ScrollbackLines = Math.Clamp(ScrollbackLines, 100, 1_000_000);
        WindowWidth = double.IsFinite(WindowWidth) ? Math.Clamp(WindowWidth, 480, 16_000) : 1000;
        WindowHeight = double.IsFinite(WindowHeight) ? Math.Clamp(WindowHeight, 320, 16_000) : 700;
        Palette ??= [];
        for (int i = Palette.Count; i < 16; i++)
        {
            Palette.Add(DefaultPalette[i]);
        }
        Profiles ??= [];
        Profiles.RemoveAll(static p => p is null);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>Where the client keeps its files.</summary>
public sealed record AppPaths(string ConfigDirectory, string ScriptsDirectory, string LogsDirectory)
{
    public string SettingsFile => Path.Combine(ConfigDirectory, "settings.json");

    /// <summary>Per-user locations: the platform's application-data folder, and Documents for logs.</summary>
    public static AppPaths Default()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create);
        if (string.IsNullOrEmpty(appData))
        {
            appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }
        string config = Path.Combine(appData, "Comet");

        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string logs = string.IsNullOrEmpty(documents) ? Path.Combine(config, "logs") : Path.Combine(documents, "Comet", "Logs");
        return new AppPaths(config, Path.Combine(config, "scripts"), logs);
    }

    public string ScriptFileFor(SessionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return string.IsNullOrWhiteSpace(profile.ScriptFile)
            ? Path.Combine(ScriptsDirectory, profile.Id + ".tin")
            : Path.GetFullPath(profile.ScriptFile, ScriptsDirectory);
    }
}

/// <summary>Loads and saves <see cref="AppSettings"/>.</summary>
public static class SettingsStore
{
    /// <summary>
    /// Reads settings. A missing file gives defaults. An unreadable file is set aside as
    /// settings.json.bad and defaults are used, so one bad edit never blocks startup.
    /// </summary>
    public static AppSettings Load(AppPaths paths, out string? warning)
    {
        ArgumentNullException.ThrowIfNull(paths);
        warning = null;
        string file = paths.SettingsFile;
        try
        {
            if (!File.Exists(file))
            {
                return new AppSettings();
            }
            AppSettings settings = JsonSerializer.Deserialize(File.ReadAllText(file), SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
            settings.Normalize();
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            warning = $"Settings could not be read ({ex.Message}). Defaults are in use.";
            try
            {
                File.Move(file, file + ".bad", overwrite: true);
                warning += " The old file was kept as settings.json.bad.";
            }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
            {
                // Nothing more can be done; the warning already tells the user.
            }
            return new AppSettings();
        }
    }

    public static void Save(AppPaths paths, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(settings);
        AtomicFile.WriteAllText(paths.SettingsFile, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
    }
}
