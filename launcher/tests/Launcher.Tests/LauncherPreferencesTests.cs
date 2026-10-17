using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Tests;

public sealed class LauncherPreferencesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wwe-launcher-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private PortableLayout Layout => new(_root);
    private string FilePath => Path.Combine(_root, "userdata", "launcher-preferences.json");

    [Fact]
    public void The_update_check_is_on_until_the_player_turns_it_off()
    {
        Assert.True(LauncherPreferences.Load(Layout).CheckForUpdates);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Turning_the_check_off_is_remembered_in_userdata()
    {
        LauncherPreferences.Load(Layout).With(checkForUpdates: false).Save(Layout);

        Assert.True(File.Exists(FilePath));
        Assert.False(LauncherPreferences.Load(Layout).CheckForUpdates);

        LauncherPreferences.Load(Layout).With(checkForUpdates: true).Save(Layout);
        Assert.True(LauncherPreferences.Load(Layout).CheckForUpdates);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("""{"check_for_updates":"no"}""")]
    [InlineData("[false]")]
    public void A_damaged_file_falls_back_to_the_defaults(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, text);

        Assert.True(LauncherPreferences.Load(Layout).CheckForUpdates);
    }

    [Fact]
    public void Saving_keeps_keys_this_launcher_does_not_know()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """{"window":{"x":10,"y":20},"check_for_updates":true}""");

        LauncherPreferences.Load(Layout).With(checkForUpdates: false).Save(Layout);

        var text = File.ReadAllText(FilePath);
        Assert.Contains("\"window\"", text);
        Assert.Contains("\"x\": 10", text);
        Assert.False(LauncherPreferences.Load(Layout).CheckForUpdates);
        Assert.Equal([FilePath], Directory.GetFiles(Path.GetDirectoryName(FilePath)!));
    }

    [Fact]
    public void Effects_are_full_until_the_player_reduces_them()
    {
        Assert.False(LauncherPreferences.Load(Layout).ReduceEffects);
    }

    [Fact]
    public void Reducing_effects_is_remembered_and_leaves_the_update_check_alone()
    {
        LauncherPreferences.Load(Layout).With(checkForUpdates: false).Save(Layout);

        LauncherPreferences.Load(Layout).With(reduceEffects: true).Save(Layout);

        var reopened = LauncherPreferences.Load(Layout);
        Assert.True(reopened.ReduceEffects);
        Assert.False(reopened.CheckForUpdates);

        reopened.With(checkForUpdates: true).Save(Layout);
        Assert.True(LauncherPreferences.Load(Layout).ReduceEffects);
    }
}
