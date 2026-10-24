using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Tests;

/// <summary>The Graphics preset row (low-end-preset ticket 06): derived from the file, picked as several keys.</summary>
public sealed class GraphicsPresetTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wwe-preset-").FullName;
    private string SettingsPath => Path.Combine(_root, "wweallstars.settings.toml");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // The game's export shape: a startup group row over one startup member and one live member
    // (sharpening, which the launcher does not show on its own row).
    private const string SchemaJson = """
        {
          "format": 2,
          "settings": [
            {"key": "test_preset", "label": "Graphics preset", "tab": "GRAPHICS", "type": "", "default": "high", "applies_at_startup": true, "native_only": true, "next_start": true, "members": ["wwe_test_toggle", "wwe_native_cas"], "presets": [{"value": "low", "label": "Low", "values": ["false", "0"]}, {"value": "high", "label": "High", "values": ["true", "0.5"]}], "choices": [{"value": "low", "label": "Low"}, {"value": "high", "label": "High"}, {"value": "custom", "label": "Custom"}], "help": "Low or High."},
            {"key": "wwe_test_toggle", "label": "Test toggle", "tab": "GRAPHICS", "type": "bool", "default": "true", "applies_at_startup": true, "native_only": true, "next_start": true, "choices": [{"value": "true", "label": "On"}, {"value": "false", "label": "Off"}], "help": "SS."},
            {"key": "wwe_native_cas", "label": "Sharpening", "tab": "GRAPHICS", "type": "double", "default": "0.5", "applies_at_startup": false, "native_only": true, "next_start": true, "choices": [{"value": "0", "label": "Off"}, {"value": "0.5", "label": "Medium"}], "help": "CAS."}
          ]
        }
        """;

    private static readonly SettingsSchema Schema = SettingsSchema.Parse(SchemaJson);

    [Fact]
    public void The_schema_parses_the_group_fields_and_rows_without_them()
    {
        var group = Schema.Find("test_preset")!;

        Assert.True(group.IsGroup);
        Assert.Equal(["wwe_test_toggle", "wwe_native_cas"], group.Members);
        Assert.Equal(["low", "high"], group.Presets.Select(p => p.Value));
        Assert.Equal(["true", "0.5"], group.Presets[1].Values);
        Assert.False(Schema.Find("wwe_test_toggle")!.IsGroup);
        Assert.Empty(Schema.Find("wwe_test_toggle")!.Members);
    }

    [Fact]
    public void The_committed_schema_has_the_Graphics_preset_first_on_the_graphics_tab()
    {
        var schema = SettingsSchema.Committed();
        var preset = schema.Find("graphics_preset")!;

        Assert.True(preset.IsGroup);
        Assert.True(preset.AppliesAtStartup);
        Assert.Equal("high", preset.Default);
        Assert.Equal("graphics_preset", schema.Startup.First(s => s.Tab == "GRAPHICS").Key);
        Assert.Equal(["Low", "Medium", "High", "Ultra", "Custom"], preset.Choices.Select(c => c.Label));
        // Ultra over High (graphics-remaster spec): 2160p, Sharpening Low, 16x filtering.
        Assert.Equal(["3", "0.25", "5"], preset.Presets.Single(p => p.Value == "ultra").Values);
    }

    [Theory]
    [InlineData(null, "high")]                                                   // no file: the defaults
    [InlineData("wwe_test_toggle = false\nwwe_native_cas = 0.000000\n", "low")]  // the cvar's own spelling
    [InlineData("wwe_test_toggle = false\n", "custom")]                   // sharpening still Medium
    [InlineData("wwe_test_toggle = true\nwwe_native_cas = 0\n", "custom")]
    public void The_shown_preset_is_the_one_whose_values_all_match_else_Custom(string? text, string preset)
    {
        if (text is not null) File.WriteAllText(SettingsPath, text);

        Assert.Equal(preset, new SettingsFile(SettingsPath, Schema).Read()["test_preset"]);
    }

    [Fact]
    public void Picking_a_preset_writes_exactly_the_members_that_differ_including_a_live_one()
    {
        File.WriteAllText(SettingsPath, "# PC SETTINGS\nwwe_native_cas = 0\nfullscreen = true\n");
        var file = new SettingsFile(SettingsPath, Schema);

        file.Set("test_preset", "low");

        Assert.Equal("# PC SETTINGS\nwwe_native_cas = 0\nfullscreen = true\nwwe_test_toggle = false\n",
            File.ReadAllText(SettingsPath));
        Assert.Equal("low", file.Read()["test_preset"]);
    }

    [Fact]
    public void The_help_line_names_the_suggestion_for_the_chosen_gpu_as_the_game_does()
    {
        var group = Schema.Find("test_preset")!;

        Assert.Equal("Low or High. Suggested for this GPU: Low.", SettingsFile.RowHelp(group, "low"));
        Assert.Equal("Low or High.", SettingsFile.RowHelp(group, ""));
        Assert.Equal("SS.", SettingsFile.RowHelp(Schema.Find("wwe_test_toggle")!, "low"));
    }

    [Theory]
    [InlineData(null, true)]                                  // new install: no file yet
    [InlineData("fullscreen = true\n", true)]                 // other settings say nothing
    [InlineData("wwe_native_cas = 0.5\n", false)]             // a member saved: the player chose
    [InlineData("wwe_test_toggle = true\n", false)]
    public void First_setup_is_while_no_member_is_saved(string? text, bool first)
    {
        if (text is not null) File.WriteAllText(SettingsPath, text);

        Assert.Equal(first, new SettingsFile(SettingsPath, Schema).IsFirstSetup());
    }

    [Fact]
    public void Setup_writes_every_value_of_the_suggested_preset()
    {
        File.WriteAllText(SettingsPath, "fullscreen = true\n");
        var file = new SettingsFile(SettingsPath, Schema);

        Assert.True(file.ApplySuggestedPresetAtSetup("high"));

        // All members, even those equal to their default, so the file shows the choice.
        Assert.Equal("fullscreen = true\nwwe_test_toggle = true\nwwe_native_cas = 0.5\n",
            File.ReadAllText(SettingsPath));
        Assert.Equal("high", file.Read()["test_preset"]);
    }

    [Theory]
    [InlineData("wwe_test_toggle = true\n", "low")]  // a saved choice is never overwritten
    [InlineData(null, "")]                                   // no suggestion (no GPU listed): the game decides later
    [InlineData(null, "ultra")]                              // not a preset
    public void Setup_leaves_the_file_alone_when_the_player_chose_or_nothing_is_suggested(string? text, string suggested)
    {
        if (text is not null) File.WriteAllText(SettingsPath, text);

        Assert.False(new SettingsFile(SettingsPath, Schema).ApplySuggestedPresetAtSetup(suggested));

        Assert.Equal(text, File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath) : null);
    }

    [Fact]
    public void Reset_writes_the_suggested_preset_instead_of_the_defaults_for_the_members()
    {
        File.WriteAllText(SettingsPath, "wwe_test_toggle = true\nwwe_native_cas = 1\n");
        var file = new SettingsFile(SettingsPath, Schema);

        file.ResetToDefaults(suggested: "low");

        Assert.Equal("wwe_test_toggle = false\nwwe_native_cas = 0\n", File.ReadAllText(SettingsPath));
        Assert.Equal("low", file.Read()["test_preset"]);
    }

    [Fact]
    public void Custom_is_shown_but_never_picked()
    {
        File.WriteAllText(SettingsPath, "wwe_test_toggle = false\n");

        new SettingsFile(SettingsPath, Schema).Set("test_preset", "custom");

        Assert.Equal("wwe_test_toggle = false\n", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Reset_to_defaults_also_resets_a_member_the_launcher_does_not_show()
    {
        File.WriteAllText(SettingsPath, "wwe_test_toggle = false\nwwe_native_cas = 0\n");
        var file = new SettingsFile(SettingsPath, Schema);

        file.ResetToDefaults();

        Assert.Equal("wwe_test_toggle = true\nwwe_native_cas = 0.5\n", File.ReadAllText(SettingsPath));
        Assert.Equal("high", file.Read()["test_preset"]);
    }
}
