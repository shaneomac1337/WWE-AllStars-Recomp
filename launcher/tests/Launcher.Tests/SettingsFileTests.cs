using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Tests;

public sealed class SettingsFileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wwe-settings-").FullName;
    private string SettingsPath => Path.Combine(_root, "wweallstars.settings.toml");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // A small schema in the game's export format: three startup settings (one Native only),
    // one live setting the launcher does not show, and online play with its player name.
    private const string SchemaJson = """
        {
          "format": 2,
          "settings": [
            {"key": "fullscreen", "label": "Display mode", "tab": "DISPLAY", "type": "bool", "default": "false", "applies_at_startup": true, "native_only": false, "next_start": false, "choices": [{"value": "false", "label": "Windowed"}, {"value": "true", "label": "Fullscreen"}], "help": "Play in a window or fill the screen."},
            {"key": "wwe_native_renderer", "label": "Renderer", "tab": "GRAPHICS", "type": "bool", "default": "false", "applies_at_startup": true, "native_only": false, "next_start": true, "choices": [{"value": "true", "label": "Native"}, {"value": "false", "label": "Emulated"}], "help": "Native or Emulated."},
            {"key": "wwe_native_resolution_scale", "label": "Render resolution", "tab": "GRAPHICS", "type": "int", "default": "1", "applies_at_startup": true, "native_only": true, "next_start": true, "choices": [{"value": "1", "label": "720p"}, {"value": "2", "label": "1440p"}], "help": "Native only."},
            {"key": "audio_mute", "label": "Mute", "tab": "AUDIO", "type": "bool", "default": "false", "applies_at_startup": false, "native_only": false, "next_start": false, "choices": [{"value": "false", "label": "Off"}, {"value": "true", "label": "On"}], "help": "Silences all game audio."},
            {"key": "wwe_online", "label": "Online play", "tab": "ONLINE", "type": "bool", "default": "false", "applies_at_startup": true, "native_only": false, "next_start": true, "choices": [{"value": "false", "label": "Off"}, {"value": "true", "label": "On"}], "help": "Online."},
            {"key": "wwe_online_name", "label": "Player name", "tab": "ONLINE", "type": "string", "default": "", "applies_at_startup": true, "native_only": false, "next_start": true, "kind": "text", "max_length": 15, "rule": "gamertag", "choices": [], "help": "The name."},
            {"key": "wwe_online_server", "label": "Game server", "tab": "ONLINE", "type": "string", "default": "https://allstars.komplexaci.cz", "applies_at_startup": true, "native_only": false, "next_start": true, "kind": "text", "max_length": 253, "rule": "server_address", "choices": [], "help": "The server."}
          ]
        }
        """;

    private static readonly SettingsSchema Schema = SettingsSchema.Parse(SchemaJson);

    [Fact]
    public void Changing_a_setting_rewrites_only_its_line_and_keeps_every_other_line()
    {
        File.WriteAllText(SettingsPath,
            "# PC SETTINGS\r\naudio_mute = true\r\nfullscreen = false\r\nwwe_tick_log = true\r\n");

        new SettingsFile(SettingsPath, Schema).Set("fullscreen", "true");

        Assert.Equal("# PC SETTINGS\r\naudio_mute = true\r\nfullscreen = true\r\nwwe_tick_log = true\r\n",
            File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Switching_to_Emulated_after_a_fallback_saves_the_Emulated_renderer_and_keeps_every_other_line()
    {
        File.WriteAllText(SettingsPath,
            "# PC SETTINGS\r\naudio_mute = true\r\nwwe_native_renderer = true\r\nwwe_tick_log = true\r\n");

        new SettingsFile(SettingsPath, Schema).SwitchToEmulated();

        Assert.Equal("# PC SETTINGS\r\naudio_mute = true\r\nwwe_native_renderer = false\r\nwwe_tick_log = true\r\n",
            File.ReadAllText(SettingsPath));
    }

    [Theory]
    [InlineData("fullscreen", "maybe")]
    [InlineData("wwe_native_resolution_scale", "4")]
    [InlineData("audio_mute", "true")]  // a live setting the launcher does not show
    [InlineData("wwe_unknown", "1")]
    public void A_value_outside_the_schema_or_a_setting_the_launcher_does_not_show_is_rejected(string key, string value)
    {
        File.WriteAllText(SettingsPath, "fullscreen = false\n");

        Assert.Throws<LauncherException>(() => new SettingsFile(SettingsPath, Schema).Set(key, value));

        Assert.Equal("fullscreen = false\n", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Reading_gives_each_shown_setting_its_saved_value_or_its_default()
    {
        File.WriteAllText(SettingsPath,
            "audio_mute = true\nwwe_native_renderer = true # set in game\nwwe_native_resolution_scale = 7\n");

        var values = new SettingsFile(SettingsPath, Schema).Read();

        Assert.Equal(new Dictionary<string, string>
        {
            ["fullscreen"] = "false",               // absent: the default
            ["wwe_native_renderer"] = "true",       // saved, comment ignored
            ["wwe_native_resolution_scale"] = "1",  // not a choice: the default
            ["wwe_online"] = "false",
            ["wwe_online_name"] = "",
            ["wwe_online_server"] = "https://allstars.komplexaci.cz",
        }, values);
    }

    [Theory]
    [InlineData("wwe_native_supersample = true\n", "wwe_native_resolution_scale = 2\n")]
    [InlineData("wwe_native_resolution_scale = 1\r\nwwe_native_supersample = true\r\n", "wwe_native_resolution_scale = 2\r\n")]
    [InlineData("wwe_native_supersample = true\nwwe_native_resolution_scale = 3\n", "wwe_native_resolution_scale = 3\n")]
    [InlineData("fullscreen = true\nwwe_native_supersample = false\n", "fullscreen = true\nwwe_native_resolution_scale = 1\n")]
    // The same cases as the game's test_settings_config, so both migrate a file alike.
    [InlineData("wwe_native_supersample = true # set in game\n", "wwe_native_resolution_scale = 2\n")]
    [InlineData("wwe_native_supersample = \"true\"\n", "wwe_native_resolution_scale = 2\n")]
    [InlineData("wwe_native_supersample =\n", "wwe_native_resolution_scale = 1\n")]
    public void An_old_Supersampling_line_becomes_a_render_resolution_once_when_the_file_is_read(string before, string after)
    {
        File.WriteAllText(SettingsPath, before);

        new SettingsFile(SettingsPath, Schema).Read();

        Assert.Equal(after, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void A_file_without_the_old_Supersampling_line_is_not_rewritten_on_read()
    {
        File.WriteAllText(SettingsPath, "wwe_native_resolution_scale = 1\nwwe_removed_key = 3\n");
        var written = File.GetLastWriteTimeUtc(SettingsPath);

        var values = new SettingsFile(SettingsPath, Schema).Read();

        Assert.Equal("1", values["wwe_native_resolution_scale"]);
        Assert.Equal(written, File.GetLastWriteTimeUtc(SettingsPath));
        Assert.Equal("wwe_native_resolution_scale = 1\nwwe_removed_key = 3\n", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void A_missing_settings_file_reads_as_the_defaults_and_the_first_change_creates_it()
    {
        var file = new SettingsFile(SettingsPath, Schema);

        Assert.Equal("false", file.Read()["wwe_native_renderer"]);
        file.Set("wwe_native_renderer", "true");

        Assert.Equal("wwe_native_renderer = true\n", File.ReadAllText(SettingsPath));
        Assert.Equal("true", file.Read()["wwe_native_renderer"]);
    }

    [Theory]
    [InlineData(null, "true")]                                  // new install: no file yet
    [InlineData("", "true")]                                    // an empty file
    [InlineData("fullscreen = false\n", "true")]                // a file that names no renderer
    [InlineData("wwe_native_renderer = false\n", "false")]      // an explicit Emulated choice is kept
    [InlineData("wwe_native_renderer = true\n", "true")]
    public void The_game_starts_on_Native_unless_the_settings_file_chose_Emulated(string? text, string renderer)
    {
        // The committed schema is the game's own export (drift test), so this is the game's default too.
        if (text is not null) File.WriteAllText(SettingsPath, text);

        var values = new SettingsFile(SettingsPath, SettingsSchema.Committed()).Read();

        Assert.Equal(renderer, values[SettingsFile.RendererKey]);
    }

    [Fact]
    public void Turning_online_play_on_saves_it_in_the_file_the_game_reads_and_keeps_every_other_line()
    {
        // No flags: the game reads online play from this file at startup (launcher-online spec).
        File.WriteAllText(SettingsPath, "# PC SETTINGS\r\nfullscreen = false\r\nwwe_online_name = \"Owner\"\r\n");

        new SettingsFile(SettingsPath, SettingsSchema.Committed()).Set("wwe_online", "true");

        Assert.Equal("# PC SETTINGS\r\nfullscreen = false\r\nwwe_online_name = \"Owner\"\r\nwwe_online = true\n",
            File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void A_player_name_is_saved_as_a_TOML_string_and_read_back()
    {
        File.WriteAllText(SettingsPath, "fullscreen = true\n");
        var file = new SettingsFile(SettingsPath, Schema);

        file.Set("wwe_online_name", "Big Show 2");

        Assert.Equal("fullscreen = true\nwwe_online_name = \"Big Show 2\"\n", File.ReadAllText(SettingsPath));
        Assert.Equal("Big Show 2", file.Read()["wwe_online_name"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1Up")]
    [InlineData("Owner ")]
    [InlineData("abcdefghijklmnop")]
    [InlineData("Own\"er")]
    public void A_name_the_gamertag_rule_refuses_is_not_saved(string name)
    {
        File.WriteAllText(SettingsPath, "wwe_online_name = \"Owner\"\n");

        var error = Assert.Throws<LauncherException>(() => new SettingsFile(SettingsPath, Schema).Set("wwe_online_name", name));

        Assert.Equal(TextRules.Error(TextRules.Gamertag, name), error.Message);
        Assert.Equal("wwe_online_name = \"Owner\"\n", File.ReadAllText(SettingsPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("fullscreen = true\n")]
    [InlineData("wwe_online_name = \"1Up\"\n")]  // hand-edited: a name the rule refuses
    public void Online_play_is_not_turned_on_without_a_valid_saved_name(string text)
    {
        File.WriteAllText(SettingsPath, text);

        var error = Assert.Throws<LauncherException>(() => new SettingsFile(SettingsPath, Schema).Set("wwe_online", "true"));

        Assert.Equal("Choose a player name before turning online play on.", error.Message);
        Assert.Equal(text, File.ReadAllText(SettingsPath));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("wwe_online_name = \"1Up\"\n", false)]
    [InlineData("wwe_online_name = \"Owner\"\n", true)]
    public void Online_play_can_be_turned_on_once_a_valid_name_is_saved(string text, bool can)
    {
        File.WriteAllText(SettingsPath, text);

        Assert.Equal(can, new SettingsFile(SettingsPath, Schema).CanTurnOnlineOn());
    }

    [Fact]
    public void Online_play_is_turned_off_with_or_without_a_name()
    {
        File.WriteAllText(SettingsPath, "wwe_online = true\n");

        new SettingsFile(SettingsPath, Schema).Set("wwe_online", "false");

        Assert.Equal("wwe_online = false\n", File.ReadAllText(SettingsPath));
    }

    [Theory]
    [InlineData("wwe_online = true\n", true)]                                // hand-edited: on, no name
    [InlineData("wwe_online = true\nwwe_online_name = \"1Up\"\n", true)]     // on, a refused name
    [InlineData("wwe_online = true\nwwe_online_name = \"Owner\"\n", false)]
    [InlineData("wwe_online_name = \"\"\n", false)]                           // off: no name needed
    public void Online_play_on_without_a_valid_name_needs_one(string text, bool needsName)
    {
        File.WriteAllText(SettingsPath, text);

        var values = new SettingsFile(SettingsPath, Schema).Read();

        Assert.Equal(needsName, SettingsFile.NeedsPlayerName(values));
    }

    [Fact]
    public void Reset_to_defaults_turns_online_play_off_and_keeps_the_player_name()
    {
        File.WriteAllText(SettingsPath, "wwe_online = true\nwwe_online_name = \"Owner\"\n");

        new SettingsFile(SettingsPath, Schema).ResetToDefaults();

        Assert.Equal("wwe_online = false\nwwe_online_name = \"Owner\"\nfullscreen = false\nwwe_native_renderer = false\n" +
                     "wwe_native_resolution_scale = 1\nwwe_online_server = \"https://allstars.komplexaci.cz\"\n",
                     File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void A_game_server_is_saved_and_the_next_start_uses_it()
    {
        File.WriteAllText(SettingsPath, "fullscreen = true\n");
        var file = new SettingsFile(SettingsPath, Schema);

        file.Set("wwe_online_server", "192.168.1.5:36000");

        Assert.Equal("fullscreen = true\nwwe_online_server = \"192.168.1.5:36000\"\n", File.ReadAllText(SettingsPath));
        Assert.Equal("192.168.1.5:36000", file.Read()["wwe_online_server"]);
    }

    [Theory]
    [InlineData("my server")]
    [InlineData("ftp://example.com")]
    [InlineData("")]
    public void A_game_server_the_rule_refuses_is_not_saved(string server)
    {
        File.WriteAllText(SettingsPath, "wwe_online_server = \"127.0.0.1\"\n");

        var error = Assert.Throws<LauncherException>(() => new SettingsFile(SettingsPath, Schema).Set("wwe_online_server", server));

        Assert.Equal(TextRules.Error(TextRules.ServerAddress, server), error.Message);
        Assert.Equal("wwe_online_server = \"127.0.0.1\"\n", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void The_default_action_puts_the_owners_server_back_and_keeps_every_other_line()
    {
        File.WriteAllText(SettingsPath, "fullscreen = true\r\nwwe_online_server = \"127.0.0.1\"\r\nwwe_online = true\r\n");
        var file = new SettingsFile(SettingsPath, Schema);

        file.SetDefault("wwe_online_server");

        Assert.Equal("fullscreen = true\r\nwwe_online_server = \"https://allstars.komplexaci.cz\"\r\nwwe_online = true\r\n",
            File.ReadAllText(SettingsPath));
        Assert.Equal("https://allstars.komplexaci.cz", file.Read()["wwe_online_server"]);
    }

    [Fact]
    public void A_text_value_longer_than_its_row_allows_is_refused()
    {
        File.WriteAllText(SettingsPath, "");
        var server = new string('a', 254);

        var error = Assert.Throws<LauncherException>(() => new SettingsFile(SettingsPath, Schema).Set("wwe_online_server", server));

        Assert.Equal("Game server has at most 253 characters.", error.Message);
        Assert.Equal("", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Reset_to_defaults_writes_the_shown_settings_defaults_and_keeps_every_other_line()
    {
        File.WriteAllText(SettingsPath,
            "audio_mute = true\nfullscreen = true\nwwe_native_renderer = true\nwwe_native_resolution_scale = 2\n");

        new SettingsFile(SettingsPath, Schema).ResetToDefaults();

        Assert.Equal(
            "audio_mute = true\nfullscreen = false\nwwe_native_renderer = false\nwwe_native_resolution_scale = 1\n" +
            "wwe_online = false\nwwe_online_server = \"https://allstars.komplexaci.cz\"\n",
            File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void The_settings_file_is_the_one_the_game_reads_from_the_user_data_folder()
    {
        // The game reads <user_data_root>/wweallstars.settings.toml (src/settings/config_file.h
        // SettingsPath), so replacing the game folder never wipes the player's settings.
        var layout = new PortableLayout(_root, gameExecutable: Path.Combine(_root, "elsewhere", "wweallstars.exe"));

        Assert.Equal(Path.Combine(_root, "userdata", "wweallstars.settings.toml"), layout.SettingsFile);
    }

    [Fact]
    public void A_settings_file_that_cannot_be_written_is_a_plain_error()
    {
        var file = new SettingsFile(Path.Combine(_root, "missing-folder", "wweallstars.settings.toml"), Schema);

        Assert.Throws<LauncherException>(() => file.Set("fullscreen", "true"));
    }

    [Fact]
    public void Native_only_settings_are_unavailable_with_a_note_while_Emulated_is_chosen()
    {
        var emulated = new Dictionary<string, string> { ["wwe_native_renderer"] = "false" };
        var native = new Dictionary<string, string> { ["wwe_native_renderer"] = "true" };
        var resolution = Schema.Find("wwe_native_resolution_scale")!;
        var fullscreen = Schema.Find("fullscreen")!;

        Assert.NotNull(SettingsFile.UnavailableNote(resolution, emulated));
        Assert.Null(SettingsFile.UnavailableNote(resolution, native));
        Assert.Null(SettingsFile.UnavailableNote(fullscreen, emulated));
    }
}
