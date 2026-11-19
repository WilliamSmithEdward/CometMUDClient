using Comet.Core.Configuration;

namespace Comet.Core.Tests;

public sealed class AppPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "comet-paths-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void NoOverrideGivesThePerUserDefaults()
    {
        AppPaths paths = AppPaths.Resolve([], null, out string? warning);

        Assert.Null(warning);
        Assert.Equal(AppPaths.Default(), paths);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OptionKeepsEverythingUnderOneFolder(bool equalsForm)
    {
        string[] arguments = equalsForm ? ["--other", "--config-dir=" + _root] : ["--config-dir", _root];
        AppPaths paths = AppPaths.Resolve(arguments, null, out string? warning);

        Assert.Null(warning);
        Assert.Equal(_root, paths.ConfigDirectory);
        Assert.Equal(Path.Combine(_root, "settings.json"), paths.SettingsFile);
        Assert.Equal(Path.Combine(_root, "scripts"), paths.ScriptsDirectory);
        Assert.Equal(Path.Combine(_root, "logs"), paths.LogsDirectory);
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public void EnvironmentVariableIsUsedAndTheOptionWinsOverIt()
    {
        string other = Path.Combine(_root, "from-option");

        Assert.Equal(_root, AppPaths.Resolve([], _root, out _).ConfigDirectory);
        Assert.Equal(other, AppPaths.Resolve(["--config-dir", other], _root, out _).ConfigDirectory);
    }

    [Fact]
    public void MissingOrUnusableFolderFallsBackWithAWarning()
    {
        Assert.Equal(AppPaths.Default(), AppPaths.Resolve(["--config-dir"], null, out string? missing));
        Assert.NotNull(missing);

        // A folder cannot be created underneath an existing file.
        Directory.CreateDirectory(_root);
        string file = Path.Combine(_root, "a-file");
        File.WriteAllText(file, "x");
        Assert.Equal(AppPaths.Default(), AppPaths.Resolve(["--config-dir", Path.Combine(file, "sub")], null, out string? unusable));
        Assert.NotNull(unusable);
    }

    [Fact]
    public void SettingsSavedUnderAnOverrideStayThere()
    {
        AppPaths paths = AppPaths.Resolve(["--config-dir", _root], null, out _);
        var settings = new AppSettings();
        settings.Profiles.Add(new SessionProfile { Name = "Isolated", Host = "mud.example.org" });
        SettingsStore.Save(paths, settings);

        Assert.True(File.Exists(Path.Combine(_root, "settings.json")));
        Assert.Equal("Isolated", Assert.Single(SettingsStore.Load(paths, out _).Profiles).Name);
    }
}
