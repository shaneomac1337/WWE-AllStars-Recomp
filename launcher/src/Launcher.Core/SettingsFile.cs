using System.Globalization;
using System.Text;

namespace WweAllStars.Launcher.Core;

/// <summary>
/// The game's PC SETTINGS file, <c>wweallstars.settings.toml</c>, which the in-game page (F4)
/// reads and writes too. The launcher changes only the keys it shows, one line at a time, and
/// keeps every other line, with the same atomic write as the game
/// (<c>src/settings/config_file.h</c>: a temporary file, then a rename).
/// </summary>
public sealed class SettingsFile(string path, SettingsSchema schema)
{
    public string FilePath { get; } = path;

    public SettingsSchema Schema { get; } = schema;

    /// <summary>
    /// Saves one shown setting at once. A setting the launcher does not show, or a value its
    /// choices do not name, is refused and leaves the file as it is. A group row (the Graphics
    /// preset) saves the members whose value differs from the preset, in one write; Custom
    /// is shown, never picked, and changes nothing.
    /// </summary>
    public void Set(string key, string value)
    {
        var setting = Shown(key);
        if (setting.IsText)
        {
            if (TextError(setting, value) is { } error) throw new LauncherException(error);
            Write(SetTomlKey(ReadText(), key, Literal(setting, value)));
            return;
        }
        // Online play never goes on without a name to sign in with (launcher-online spec).
        if (key == OnlineKey && value == "true" && Schema.Find(PlayerNameKey) is not null && !CanTurnOnlineOn())
            throw new LauncherException("Choose a player name before turning online play on.");
        var choice = setting.Choices.FirstOrDefault(c => SameValue(c.Value, value))
            ?? throw new LauncherException($"{setting.Label} cannot be set to '{value}'.");
        if (setting.IsGroup)
        {
            if (setting.Presets.FirstOrDefault(p => p.Value == choice.Value) is { } preset)
                WritePreset(setting, preset);
            return;
        }
        Write(SetTomlKey(ReadText(), key, Literal(setting, choice.Value)));
    }

    // The preset's members that differ from the file's values (every member with all), in one write.
    private void WritePreset(SchemaSetting group, SchemaPreset preset, bool all = false) =>
        Write(WithPreset(ReadText(), group, preset, all ? null : Read()));

    // The text with the preset's members set: those that differ from values, or all when values is null.
    private string WithPreset(string text, SchemaSetting group, SchemaPreset preset,
                              IReadOnlyDictionary<string, string>? values)
    {
        for (var m = 0; m < group.Members.Count; m++)
        {
            if (Schema.Find(group.Members[m]) is not { } member) continue;
            if (values is not null && values.TryGetValue(member.Key, out var now) && SameValue(now, preset.Values[m]))
                continue;
            text = SetTomlKey(text, member.Key, Literal(member, preset.Values[m]));
        }
        return text;
    }

    /// <summary>
    /// The first-setup rule the game shares (<c>src/settings/registry.h</c> IsFirstSetup): no
    /// member of a shown group row is saved. The launcher never puts a player setting in the
    /// launch options (<see cref="LaunchArguments"/>), so saved is the only way to have chosen.
    /// </summary>
    public bool IsFirstSetup()
    {
        var saved = SavedKeys();
        return !Schema.Startup.Where(s => s.IsGroup).SelectMany(g => g.Members).Any(saved.Contains);
    }

    /// <summary>
    /// At setup: writes every value of the <paramref name="suggested"/> preset (a preset value,
    /// <see cref="GpuList.SuggestionFor"/>) while <see cref="IsFirstSetup"/> holds. False, with
    /// the file unchanged, when the player has chosen or nothing usable is suggested; the game
    /// then decides at its first start from the GPU it opens.
    /// </summary>
    public bool ApplySuggestedPresetAtSetup(string suggested)
    {
        if (!IsFirstSetup()) return false;
        var group = Schema.Startup.FirstOrDefault(s => s.IsGroup);
        if (group?.Presets.FirstOrDefault(p => p.Value == suggested) is not { } preset) return false;
        WritePreset(group, preset, all: true);
        return true;
    }

    // The keys the file has a line for.
    private HashSet<string> SavedKeys() =>
        [.. ReadText().Split('\n').Select(line => LineKey(line.TrimEnd('\r'))).OfType<string>()];

    /// <summary>
    /// The preset whose values all match <paramref name="values"/> (by member key), else
    /// <see cref="SchemaSetting.CustomChoice"/>.
    /// </summary>
    public static string DerivedPreset(SchemaSetting group, IReadOnlyDictionary<string, string> values) =>
        group.Presets.FirstOrDefault(p => group.Members.Select((key, m) =>
                values.TryGetValue(key, out var value) && SameValue(value, p.Values[m])).All(match => match))
            ?.Value ?? SchemaSetting.CustomChoice;

    /// <summary>
    /// A row's help line; a group row's names the preset suggested for the chosen GPU
    /// (<see cref="GpuList.SuggestionFor"/>), as the in-game page does
    /// (<c>src/settings/registry.h</c> WithSuggestedPreset), and a setting the game also
    /// changes at once ends with <see cref="InGameLiveNote"/>.
    /// </summary>
    public static string RowHelp(SchemaSetting setting, string suggested) =>
        setting.IsGroup && setting.Presets.FirstOrDefault(p => p.Value == suggested) is { } preset
            ? $"{setting.Help} Suggested for this GPU: {preset.Label}."
            : setting.NextStart ? setting.Help : $"{setting.Help} {InGameLiveNote}";

    /// <summary>Ends the help of a setting the in-game page changes with no restart.</summary>
    public const string InGameLiveNote = "In game (F4) it changes at once.";

    /// <summary>The settings the file holds values for: those shown, and the members of a shown group row.</summary>
    private IEnumerable<SchemaSetting> Saved =>
        Schema.Startup.Where(s => !s.IsGroup)
            .Concat(Schema.Startup.Where(s => s.IsGroup).SelectMany(g => g.Members)
                .Select(Schema.Find).OfType<SchemaSetting>().Where(s => !s.AppliesAtStartup))
            .DistinctBy(s => s.Key);

    /// <summary>
    /// The value of each shown setting, by key: as saved, or its default when the file has no
    /// line for it or saves a value its choices do not name. A group row's members are read too,
    /// shown or not, and the group row gets the preset they match.
    /// </summary>
    public IReadOnlyDictionary<string, string> Read()
    {
        var saved = SavedValues();
        var values = new Dictionary<string, string>();
        foreach (var setting in Saved)
        {
            // A text value is shown as saved, even one its rule refuses (a hand-edited file).
            if (setting.IsText)
            {
                values[setting.Key] = saved.GetValueOrDefault(setting.Key, setting.Default);
                continue;
            }
            var choice = saved.TryGetValue(setting.Key, out var value)
                ? setting.Choices.FirstOrDefault(c => SameValue(c.Value, value))
                : null;
            values[setting.Key] = choice?.Value ?? setting.Default;
        }
        foreach (var group in Schema.Startup.Where(s => s.IsGroup))
            values[group.Key] = DerivedPreset(group, values);
        return values;
    }

    /// <summary>
    /// Saves every shown setting's default in one write, and for a shown group row's members the
    /// <paramref name="suggested"/> preset (for the chosen GPU), or their defaults when none is
    /// suggested; other lines stay. The player name stays too: it is who the player is, not a
    /// preference, and online play goes back to Off.
    /// </summary>
    public void ResetToDefaults(string suggested = "")
    {
        var text = ReadText();
        foreach (var setting in Saved.Where(s => s.Key != PlayerNameKey))
            text = SetTomlKey(text, setting.Key, Literal(setting, setting.Default));
        foreach (var group in Schema.Startup.Where(s => s.IsGroup))
        {
            if (group.Presets.FirstOrDefault(p => p.Value == suggested) is { } preset)
                text = WithPreset(text, group, preset, values: null);
        }
        Write(text);
    }

    /// <summary>The Online play choice (Off/On).</summary>
    public const string OnlineKey = "wwe_online";

    /// <summary>The player's Online play setting in the file at <paramref name="path"/>; off when it can't be read.</summary>
    public static bool OnlinePlayOn(string path) => IsOn(path, OnlineKey);

    /// <summary>Skip startup logos, which the launcher applies at Play (<see cref="LauncherConfigEdit.SkipStartupLogos"/>).</summary>
    public const string SkipStartupLogosKey = "wwe_skip_startup_logos";

    /// <summary>The player's on/off setting <paramref name="key"/> in the file at <paramref name="path"/>; off when it can't be read.</summary>
    public static bool IsOn(string path, string key)
    {
        try
        {
            return new SettingsFile(path, SettingsSchema.Committed()).Read().GetValueOrDefault(key) == "true";
        }
        catch (LauncherException)
        {
            return false;
        }
    }

    /// <summary>The player name other players see: a text row with the gamertag rule.</summary>
    public const string PlayerNameKey = "wwe_online_name";

    /// <summary>The game server: a text row with the server-address rule, the owner's server by default.</summary>
    public const string GameServerKey = "wwe_online_server";

    /// <summary>Saves one shown setting's default (the Game server's Default action); other lines stay.</summary>
    public void SetDefault(string key)
    {
        var setting = Shown(key);
        Write(SetTomlKey(ReadText(), key, Literal(setting, setting.Default)));
    }

    /// <summary>
    /// Each keyboard input's binding, by setting key (<see cref="KeyBinds.Inputs"/>): as saved, or
    /// the default when the file has no line for it.
    /// </summary>
    public IReadOnlyDictionary<string, string> ReadKeyBinds()
    {
        var saved = SavedValues();
        return KeyBinds.Inputs.ToDictionary(i => i.Key, i => saved.GetValueOrDefault(i.Key, i.Default));
    }

    /// <summary>Saves one keyboard input's binding (a <see cref="KeyBinds"/> value); other lines stay.</summary>
    public void SetKeyBind(string key, string value) => SetKeyBinds(new Dictionary<string, string> { [key] = value });

    /// <summary>Saves several keyboard bindings in one write (a key moved between inputs); other lines stay.</summary>
    public void SetKeyBinds(IReadOnlyDictionary<string, string> values)
    {
        if (values.Keys.FirstOrDefault(key => KeyBinds.Find(key) is null) is { } unknown)
            throw new LauncherException($"The launcher does not change the setting '{unknown}'.");
        var text = ReadText();
        foreach (var (key, value) in values)
            text = SetTomlKey(text, key, StringLiteral(value));
        Write(text);
    }

    /// <summary>Puts every keyboard binding back to its default by removing its lines; other lines stay.</summary>
    public void ResetKeyBinds()
    {
        var kept = ReadText().Split('\n').Where(line => LineKey(line.TrimEnd('\r')) is not { } key || KeyBinds.Find(key) is null);
        Write(string.Join('\n', kept));
    }

    // Each key's first saved value.
    private Dictionary<string, string> SavedValues()
    {
        var saved = new Dictionary<string, string>();
        foreach (var line in ReadText().Split('\n'))
        {
            var body = line.TrimEnd('\r');
            if (LineKey(body) is { } key && !saved.ContainsKey(key))
                saved[key] = LiteralValue(body[(body.IndexOf('=') + 1)..]);
        }
        return saved;
    }

    /// <summary>
    /// Online play is on but the name is missing or one the gamertag rule refuses (a hand-edited
    /// file): the game would sign in with the profile's name, so the launcher asks for one.
    /// </summary>
    public static bool NeedsPlayerName(IReadOnlyDictionary<string, string> values) =>
        values.GetValueOrDefault(OnlineKey) == "true" &&
        TextRules.Error(TextRules.Gamertag, values.GetValueOrDefault(PlayerNameKey, "")) is not null;

    /// <summary>The saved player name follows the gamertag rule, so Online play may be turned on.</summary>
    public bool CanTurnOnlineOn() =>
        !NeedsPlayerName(new Dictionary<string, string>(Read()) { [OnlineKey] = "true" });

    /// <summary>Why a text row refuses <paramref name="value"/>, or null when it is accepted.</summary>
    public static string? TextError(SchemaSetting setting, string value) =>
        TextRules.Error(setting.Rule, value) ??
        (value.Length > setting.MaxLength ? $"{setting.Label} has at most {setting.MaxLength} characters." : null);

    /// <summary>The key of the renderer choice that Native-only settings depend on.</summary>
    public const string RendererKey = "wwe_native_renderer";

    /// <summary>The player's answer to a renderer fallback's offer: the next start runs Emulated.</summary>
    public void SwitchToEmulated() => Set(RendererKey, "false");

    /// <summary>
    /// Why a setting cannot be changed with the current values (a Native-only setting while
    /// Emulated is chosen), or null when it can.
    /// </summary>
    public static string? UnavailableNote(SchemaSetting setting, IReadOnlyDictionary<string, string> values) =>
        setting.NativeOnly && values.TryGetValue(RendererKey, out var renderer) && renderer != "true"
            ? "Native renderer only: choose Native to use this."
            : null;

    private SchemaSetting Shown(string key)
    {
        var setting = Schema.Find(key);
        if (setting is null || !setting.AppliesAtStartup)
            throw new LauncherException($"The launcher does not change the setting '{key}'.");
        return setting;
    }

    /// <summary>Equal as text, or as numbers (<c>0.5</c> and <c>0.500000</c>), as the in-game page compares.</summary>
    internal static bool SameValue(string a, string b) =>
        a == b || (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                   double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) && x == y);

    // The file's text, migrated once (MigrateText) the first time an old file is read.
    private string ReadText()
    {
        string text;
        try
        {
            text = File.Exists(FilePath) ? File.ReadAllText(FilePath) : "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException($"The settings could not be read from {FilePath}: {error.Message}", error);
        }
        var migrated = MigrateText(text);
        if (migrated != text) Write(migrated);
        return migrated;
    }

    /// <summary>
    /// Graphics remaster: the Supersampling setting is gone. On means 1440p unless a render
    /// resolution above 720p was chosen (it won then too); Off keeps 720p, written out because
    /// the default is 1440p now. As the game's <c>MigrateSettingsText</c>.
    /// </summary>
    internal static string MigrateText(string text)
    {
        const string supersampleKey = "wwe_native_supersample", scaleKey = "wwe_native_resolution_scale";
        string? supersample = null, scale = null;
        var kept = new StringBuilder();
        var start = 0;
        while (start < text.Length)
        {
            var newline = text.IndexOf('\n', start);
            var end = newline < 0 ? text.Length : newline + 1;
            var current = text[start..end];
            var body = current.TrimEnd('\r', '\n');
            var key = LineKey(body);
            if (key == supersampleKey && supersample is null)
                supersample = LiteralValue(body[(body.IndexOf('=') + 1)..]);
            else
                kept.Append(current);
            if (key == scaleKey && scale is null) scale = LiteralValue(body[(body.IndexOf('=') + 1)..]);
            start = end;
        }
        if (supersample is null) return text;
        var oldScale = Math.Max(int.TryParse(scale, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 1, 1);
        var newScale = supersample == "true" && oldScale == 1 ? 2 : oldScale;
        return SetTomlKey(kept.ToString(), scaleKey, newScale.ToString(CultureInfo.InvariantCulture));
    }

    private void Write(string text)
    {
        var temporary = FilePath + ".tmp";
        try
        {
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            throw new LauncherException($"The settings could not be saved to {FilePath}: {error.Message}", error);
        }
    }

    /// <summary>The value as a TOML literal: strings quoted and escaped, numbers and booleans bare.</summary>
    private static string Literal(SchemaSetting setting, string value)
    {
        return setting.Type == "string" ? StringLiteral(value) : value;
    }

    private static string StringLiteral(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>The value a TOML literal holds: a basic string unquoted, anything else up to a comment.</summary>
    private static string LiteralValue(string literal)
    {
        literal = literal.Trim(' ', '\t');
        if (!literal.StartsWith('"'))
        {
            var comment = literal.IndexOf('#');
            return (comment < 0 ? literal : literal[..comment]).TrimEnd(' ', '\t');
        }
        var value = new StringBuilder();
        for (var i = 1; i < literal.Length && literal[i] != '"'; i++)
        {
            if (literal[i] == '\\' && i + 1 < literal.Length) i++;
            value.Append(literal[i]);
        }
        return value.ToString();
    }

    /// <summary>
    /// The text with <paramref name="key"/> set to the TOML literal: its first line replaced
    /// (keeping the line end), or appended if the text has none. As the game's
    /// <c>SetTomlKey</c>.
    /// </summary>
    internal static string SetTomlKey(string text, string key, string literal)
    {
        var line = key + " = " + literal;
        var output = new StringBuilder();
        var found = false;
        var start = 0;
        while (start < text.Length)
        {
            var newline = text.IndexOf('\n', start);
            var end = newline < 0 ? text.Length : newline + 1;
            var current = text[start..end];
            var ending = current.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n"
                : current.EndsWith('\n') ? "\n" : "";
            if (!found && LineKey(current[..^ending.Length]) == key)
            {
                output.Append(line).Append(ending);
                found = true;
            }
            else
            {
                output.Append(current);
            }
            start = end;
        }
        if (!found)
        {
            if (output.Length > 0 && output[^1] != '\n') output.Append('\n');
            output.Append(line).Append('\n');
        }
        return output.ToString();
    }

    /// <summary>The key a <c>key = value</c> line sets, or null for any other line.</summary>
    private static string? LineKey(string body)
    {
        var equals = body.IndexOf('=');
        if (equals < 0) return null;
        var key = body[..equals].Trim(' ', '\t');
        return key.Length > 0 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? key : null;
    }
}
