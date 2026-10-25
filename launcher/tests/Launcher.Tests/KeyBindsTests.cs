using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Tests;

public sealed class KeyBindsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wwe-keybinds-").FullName;
    private string SettingsPath => Path.Combine(_root, "wweallstars.settings.toml");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private SettingsFile File_() => new(SettingsPath, SettingsSchema.Committed());

    [Fact]
    public void Every_controller_input_has_a_binding_with_the_port_defaults()
    {
        Assert.Equal(24, KeyBinds.Inputs.Count);
        Assert.Equal("K", KeyBinds.Find("keybind_a")!.Default);
        Assert.Equal("Up", KeyBinds.Find("keybind_dpad_up")!.Default);
        Assert.Equal("Tab,Return", KeyBinds.Find("keybind_start")!.Default);
    }

    [Fact]
    public void The_defaults_match_the_ones_the_game_applies()
    {
        string? header = null;
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null && header is null; folder = folder.Parent)
        {
            var candidate = Path.Combine(folder.FullName, "src", "settings", "port_defaults.h");
            if (File.Exists(Path.Combine(folder.FullName, "launcher", "global.json")) && File.Exists(candidate))
                header = candidate;
        }
        Assert.NotNull(header);
        var game = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(header),
                "\\{\"(keybind_[a-z_]+)\", \"([^\"]*)\"\\}")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        Assert.Equal(KeyBinds.Inputs.ToDictionary(i => i.Key, i => i.Default), game);
    }

    [Fact]
    public void A_value_parses_into_its_alternatives_and_formats_back()
    {
        Assert.Equal(["Q", "I"], KeyBinds.Parse("Q, I"));
        Assert.Equal(["Shift+Up"], KeyBinds.Parse("Shift+Up"));
        Assert.Empty(KeyBinds.Parse(""));
        Assert.Equal("Q,I", KeyBinds.Format(["Q", "I"]));
    }

    [Fact]
    public void A_key_takes_its_held_modifiers_in_the_SDK_order()
    {
        Assert.Equal("Shift+Ctrl+Alt+K", KeyBinds.Token("K", shift: true, ctrl: true, alt: true));
        Assert.Equal("Space", KeyBinds.Token("Space", false, false, false));
    }

    [Fact]
    public void Values_read_as_the_player_knows_the_keys()
    {
        Assert.Equal("Semicolon or Space", KeyBinds.DisplayValue("Semicolon,Space"));
        Assert.Equal("Apostrophe or Backspace", KeyBinds.DisplayValue("Quote,Backspace"));
        Assert.Equal("Shift + Up arrow", KeyBinds.DisplayValue("Shift+Up"));
        Assert.Equal("None", KeyBinds.DisplayValue(""));
    }

    [Fact]
    public void Bindings_read_as_saved_or_as_the_default()
    {
        File.WriteAllText(SettingsPath, "keybind_a = \"J\"\n");

        var binds = File_().ReadKeyBinds();

        Assert.Equal("J", binds["keybind_a"]);
        Assert.Equal("I,Backspace", binds["keybind_b"]);
    }

    [Fact]
    public void Rebinding_writes_a_quoted_line_and_keeps_every_other_line()
    {
        File.WriteAllText(SettingsPath, "# PC SETTINGS\r\nmnk_mode = true\r\nwwe_tick_log = true\r\n");

        File_().SetKeyBind("keybind_x", "Shift+J");

        Assert.Equal("# PC SETTINGS\r\nmnk_mode = true\r\nwwe_tick_log = true\r\nkeybind_x = \"Shift+J\"\n",
            File.ReadAllText(SettingsPath));
    }

    private static Dictionary<string, string> Defaults() => KeyBinds.Inputs.ToDictionary(i => i.Key, i => i.Default);

    [Fact]
    public void A_key_replaces_the_inputs_keys_or_is_added_to_them()
    {
        Assert.Equal("J", KeyBinds.Bind(Defaults(), "keybind_a", "J", add: false)["keybind_a"]);
        Assert.Equal("K,J", KeyBinds.Bind(Defaults(), "keybind_a", "J", add: true)["keybind_a"]);
        // Adding a key the input already has keeps one copy.
        Assert.Equal("K", KeyBinds.Bind(Defaults(), "keybind_a", "K", add: true)["keybind_a"]);
    }

    [Fact]
    public void A_key_another_input_has_is_found_and_can_be_moved()
    {
        Assert.Equal("keybind_x", KeyBinds.UsedBy(Defaults(), "keybind_a", "J")!.Key);
        Assert.Null(KeyBinds.UsedBy(Defaults(), "keybind_a", "M"));
        // Shift+Up is not Up (D-pad): modifiers match exactly in the game.
        Assert.Equal("keybind_dpad_up", KeyBinds.UsedBy(Defaults(), "keybind_a", "Up")!.Key);
        Assert.Null(KeyBinds.UsedBy(Defaults(), "keybind_a", "Shift+Up"));

        var changed = KeyBinds.Bind(Defaults(), "keybind_a", "E", add: true, moveFrom: "keybind_left_trigger");

        Assert.Equal("K,E", changed["keybind_a"]);
        Assert.Equal("F", changed["keybind_left_trigger"]);
    }

    [Theory]
    [InlineData("F4")]
    [InlineData("Alt+F4")]
    [InlineData("F12")]
    [InlineData("Backtick")]
    [InlineData("Escape")]
    [InlineData("Alt+Return")]
    [InlineData("Shift+Alt+Return")]
    public void Keys_the_game_uses_cannot_be_bound(string token) => Assert.NotNull(KeyBinds.Reserved(token));

    [Theory]
    [InlineData("Return")]
    [InlineData("Shift+Return")]
    [InlineData("F5")]
    [InlineData("J")]
    public void Other_keys_can_be_bound(string token) => Assert.Null(KeyBinds.Reserved(token));

    [Fact]
    public void A_move_saves_both_inputs_in_one_write()
    {
        File.WriteAllText(SettingsPath, "mnk_mode = true\n");

        File_().SetKeyBinds(new Dictionary<string, string> { ["keybind_a"] = "L", ["keybind_x"] = "" });

        Assert.Equal("mnk_mode = true\nkeybind_a = \"L\"\nkeybind_x = \"\"\n", File.ReadAllText(SettingsPath));
        Assert.Equal("", File_().ReadKeyBinds()["keybind_x"]);
    }

    [Fact]
    public void Reset_removes_every_binding_line_and_keeps_the_rest()
    {
        File.WriteAllText(SettingsPath, "keybind_a = \"J\"\r\nmnk_mode = true\r\nkeybind_x = \"K\"\r\nkeybind_extra = 1\r\n");

        File_().ResetKeyBinds();

        Assert.Equal("mnk_mode = true\r\nkeybind_extra = 1\r\n", File.ReadAllText(SettingsPath));
        Assert.Equal("K", File_().ReadKeyBinds()["keybind_a"]);
    }

    [Fact]
    public void Only_keyboard_bindings_are_written()
    {
        Assert.Throws<LauncherException>(() => File_().SetKeyBind("bind_console", "F1"));
        Assert.False(File.Exists(SettingsPath));
    }
}
