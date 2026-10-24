using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Tests;

public sealed class SettingsSchemaTests
{
    [Fact]
    public void The_launcher_shows_only_the_settings_that_apply_when_the_game_starts()
    {
        var schema = SettingsSchema.Committed();

        // The spec's startup settings ("Settings"), with the GPU and the language.
        Assert.Equal(
            ["fullscreen", "vulkan_device", "user_language", "graphics_preset", "wwe_native_renderer",
             "wwe_native_resolution_scale", "wwe_native_anisotropy", "wwe_native_color_mode", "wwe_60fps_entrances", "wwe_120fps_matches", "wwe_unlock_everything",
             "wwe_short_loading_screens", "wwe_skip_startup_logos",
             "wwe_online",
             "wwe_online_name",
             "wwe_online_server", "wwe_online_upnp"],
            schema.Startup.Select(s => s.Key));
        // Their choices are filled from this PC and the player's disc.
        Assert.Equal(SettingsSchema.GpuChoices, schema.Find("vulkan_device")!.RuntimeChoices);
        Assert.Equal(SettingsSchema.LanguageChoices, schema.Find("user_language")!.RuntimeChoices);
        Assert.All(schema.Find("user_language")!.Choices,
            c => Assert.StartsWith("Game/Localization/Coalesced.", c.File));
        // Online play and UPnP: the ONLINE tab, Off until the player turns them on.
        foreach (var key in new[] { "wwe_online", "wwe_online_upnp" })
        {
            var online = schema.Find(key)!;
            Assert.Equal(("ONLINE", "false"), (online.Tab, online.Default));
            Assert.Equal(["Off", "On"], online.Choices.Select(c => c.Label));
        }
        // The developer switches stay off the player's settings.
        foreach (var key in new[] { "wwe_online_force_relay", "wwe_online_drop_direct", "wwe_online_ip", "wwe_net_trace" })
            Assert.Null(schema.Find(key));
        // Live settings stay on the in-game page.
        foreach (var key in new[] { "present_vsync", "vblank_follows_display", "wwe_native_cas", "audio_mute",
                     "wwe_vibration_mode" })
            Assert.False(Assert.IsType<SchemaSetting>(schema.Find(key)).AppliesAtStartup);
    }

    [Fact]
    public void The_faster_starts_settings_sit_together_on_GAMEPLAY_under_their_heading_and_are_off()
    {
        var schema = SettingsSchema.Committed();

        var rows = schema.Startup.Where(s => s.Heading == "Faster starts").ToList();
        Assert.Equal(["wwe_short_loading_screens", "wwe_skip_startup_logos"], rows.Select(s => s.Key));
        var first = schema.Startup.ToList().IndexOf(rows[0]);
        Assert.Equal(rows, schema.Startup.Skip(first).Take(rows.Count));
        Assert.All(rows, row =>
        {
            Assert.Equal(("GAMEPLAY", "bool", "false"), (row.Tab, row.Type, row.Default));
            Assert.Equal(["Off", "On"], row.Choices.Select(c => c.Label));
        });
        Assert.Contains("disclaimer still shows", schema.Find("wwe_skip_startup_logos")!.Help);
        // The wait cuts say they do nothing online; the boot-only logo skip works online too.
        Assert.EndsWith("Ignored in online matches.", schema.Find("wwe_short_loading_screens")!.Help);
        Assert.Equal("", schema.Find("wwe_unlock_everything")!.Heading);
    }

    [Fact]
    public void Shown_settings_the_game_also_changes_at_once_say_so_on_their_row()
    {
        var schema = SettingsSchema.Committed();

        // Live in game (live-settings 01-04): Display mode, Texture filtering and 60 fps entrances; the rest of the
        // shown settings take a restart there, so their help stays as the game wrote it.
        Assert.Equal(["fullscreen", "wwe_native_anisotropy", "wwe_60fps_entrances", "wwe_120fps_matches"],
            schema.Startup.Where(s => SettingsFile.RowHelp(s, "").EndsWith(SettingsFile.InGameLiveNote))
                .Select(s => s.Key));
        var texture = schema.Find("wwe_native_anisotropy")!;
        Assert.Equal($"{texture.Help} {SettingsFile.InGameLiveNote}", SettingsFile.RowHelp(texture, ""));
        var gpu = schema.Find("vulkan_device")!;
        Assert.Equal(gpu.Help, SettingsFile.RowHelp(gpu, ""));
    }

    [Fact]
    public void Every_shown_setting_has_a_type_and_a_default_among_its_choices()
    {
        // The export reads both from the cvar, so a startup setting must be defined in the game
        // exe itself or in the GPU plugin, which the export loads first.
        // A group row (the Graphics preset) names no cvar, so it has no type.
        foreach (var setting in SettingsSchema.Committed().Startup)
        {
            // A text row (the player name) has a value, not choices.
            if (setting.IsText)
            {
                Assert.Equal("string", setting.Type);
                continue;
            }
            if (!setting.IsGroup) Assert.Contains(setting.Type, new[] { "bool", "int", "double", "string" });
            Assert.Contains(setting.Choices, c => c.Value == setting.Default);
        }
    }

    [Fact]
    public void A_text_row_is_read_with_its_length_and_rule()
    {
        var schema = SettingsSchema.Parse("""
            {
              "format": 2,
              "settings": [
                {"key": "wwe_online_name", "label": "Player name", "tab": "ONLINE", "type": "string", "default": "", "applies_at_startup": true, "native_only": false, "next_start": true, "kind": "text", "max_length": 15, "rule": "gamertag", "choices": [], "help": "The name."},
                {"key": "wwe_online", "label": "Online play", "tab": "ONLINE", "type": "bool", "default": "false", "applies_at_startup": true, "native_only": false, "next_start": true, "choices": [{"value": "false", "label": "Off"}, {"value": "true", "label": "On"}], "help": "Online."}
              ]
            }
            """);

        var name = schema.Find("wwe_online_name")!;
        Assert.True(name.IsText);
        Assert.Equal((15, TextRules.Gamertag), (name.MaxLength, name.Rule));
        Assert.Empty(name.Choices);
        Assert.False(schema.Find("wwe_online")!.IsText);
    }

    [Fact]
    public void The_committed_schema_has_the_player_name_after_online_play()
    {
        var keys = SettingsSchema.Committed().Startup.Select(s => s.Key).ToList();

        Assert.Equal(keys.IndexOf("wwe_online") + 1, keys.IndexOf(SettingsFile.PlayerNameKey));
        Assert.Equal(TextRules.Gamertag, SettingsSchema.Committed().Find(SettingsFile.PlayerNameKey)!.Rule);
    }

    [Fact]
    public void The_committed_schema_has_the_game_server_with_the_owners_server_by_default()
    {
        var server = SettingsSchema.Committed().Find(SettingsFile.GameServerKey)!;

        Assert.Equal((TextRules.ServerAddress, "https://allstars.komplexaci.cz"), (server.Rule, server.Default));
    }

    [Fact]
    public void A_schema_in_another_format_is_a_plain_error()
    {
        // Format 2 brought text rows; a newer export needs a newer launcher.
        Assert.Throws<LauncherException>(() => SettingsSchema.Parse("""{"format": 3, "settings": []}"""));
        Assert.Throws<LauncherException>(() => SettingsSchema.Parse("""{"format": 1, "settings": []}"""));
        Assert.Throws<LauncherException>(() => SettingsSchema.Parse("not json"));
    }
}
