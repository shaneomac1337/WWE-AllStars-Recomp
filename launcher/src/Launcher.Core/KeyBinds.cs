namespace WweAllStars.Launcher.Core;

/// <summary>One controller input the keyboard drives: its <c>keybind_*</c> setting, its label and its default keys.</summary>
public sealed record KeyBindInput(string Key, string Label, string Default);

/// <summary>
/// The keyboard's bindings (keyboard spec, Keys page): the SDK's <c>keybind_*</c> string settings
/// (<c>.tools/rexglue-source/src/input/mnk/mnk_input_driver.cpp</c>). A value is a comma-separated
/// list of alternatives, each a key name from the SDK's table (<c>src/ui/keybinds.cpp</c>) with
/// optional <c>Shift+</c>, <c>Ctrl+</c> or <c>Alt+</c> prefixes, matched exactly: <c>Up</c> stays
/// silent while Shift is held.
/// </summary>
public static class KeyBinds
{
    /// <summary>
    /// Every input, in the page's order, with the port's defaults: the layout of
    /// <c>docs/research/keyboard-layout.md</c>, which <c>src/settings/port_defaults.h</c> gives the game.
    /// </summary>
    public static readonly IReadOnlyList<KeyBindInput> Inputs =
    [
        new("keybind_a", "A", "K"),
        new("keybind_b", "B", "I,Backspace"),
        new("keybind_x", "X", "J"),
        new("keybind_y", "Y", "U"),
        new("keybind_left_shoulder", "LB", "L"),
        new("keybind_right_shoulder", "RB", "Semicolon"),
        new("keybind_left_trigger", "LT", "F,E"),
        new("keybind_right_trigger", "RT", "Space"),
        new("keybind_lstick_up", "Left stick up", "W"),
        new("keybind_lstick_down", "Left stick down", "S"),
        new("keybind_lstick_left", "Left stick left", "A"),
        new("keybind_lstick_right", "Left stick right", "D"),
        new("keybind_lstick_press", "Left stick click", "C"),
        new("keybind_rstick_up", "Right stick up", "9"),
        new("keybind_rstick_down", "Right stick down", "0"),
        new("keybind_rstick_left", "Right stick left", "O"),
        new("keybind_rstick_right", "Right stick right", "P"),
        new("keybind_rstick_press", "Right stick click", "N"),
        new("keybind_dpad_up", "D-pad up", "Up"),
        new("keybind_dpad_down", "D-pad down", "Down"),
        new("keybind_dpad_left", "D-pad left", "Left"),
        new("keybind_dpad_right", "D-pad right", "Right"),
        new("keybind_start", "Start", "Tab,Return"),
        new("keybind_back", "Back", "Z"),
    ];

    /// <summary>The input with this setting key, or null.</summary>
    public static KeyBindInput? Find(string key) => Inputs.FirstOrDefault(i => i.Key == key);

    /// <summary>The alternatives of a value, trimmed, empty ones dropped.</summary>
    public static IReadOnlyList<string> Parse(string value) =>
        [.. value.Split(',').Select(token => token.Trim(' ')).Where(token => token.Length > 0)];

    /// <summary>The value the game reads for these alternatives.</summary>
    public static string Format(IEnumerable<string> tokens) => string.Join(",", tokens);

    /// <summary>A key with its held modifiers as one alternative, in the SDK's order (<c>Shift+Ctrl+Alt+</c>).</summary>
    public static string Token(string keyName, bool shift, bool ctrl, bool alt) =>
        (shift ? "Shift+" : "") + (ctrl ? "Ctrl+" : "") + (alt ? "Alt+" : "") + keyName;

    /// <summary>An alternative as the player reads it: <c>Shift+Up</c> as "Shift + Up", <c>Semicolon</c> as ";".</summary>
    public static string Display(string token)
    {
        var parts = token.Split('+');
        parts[^1] = KeyLabels.GetValueOrDefault(parts[^1], parts[^1]);
        return string.Join(" + ", parts);
    }

    /// <summary>A value as the player reads it: its alternatives joined with " or ", "None" when empty.</summary>
    public static string DisplayValue(string value) =>
        Parse(value) is { Count: > 0 } tokens ? string.Join(" or ", tokens.Select(Display)) : "None";

    /// <summary>
    /// Why the game can't give this alternative to the controller, or null when it can: a key the
    /// game or its overlays use with any modifiers (F4 PC SETTINGS, F3/F6/F7 overlays, F12 frame
    /// capture, ` console, Esc), or Alt+Enter, which switches fullscreen.
    /// </summary>
    public static string? Reserved(string token)
    {
        var key = token.Split('+')[^1];
        if (ReservedKeys.TryGetValue(key, out var use)) return $"{Display(key)} {use}, so it can't be bound.";
        // The game switches fullscreen on Enter with Alt held, whatever else is held.
        return key == "Return" && token.Split('+').Contains("Alt")
            ? "Alt + Enter switches fullscreen, so it can't be bound." : null;
    }

    private static readonly Dictionary<string, string> ReservedKeys = new()
    {
        ["F3"] = "opens the debug overlay", ["F4"] = "opens the PC SETTINGS page",
        ["F6"] = "opens the settings overlay", ["F7"] = "opens the achievements overlay",
        ["F12"] = "captures a frame", ["Backtick"] = "opens the console", ["Escape"] = "is the game's own key",
    };

    /// <summary>The other input that already has this alternative, or null.</summary>
    public static KeyBindInput? UsedBy(IReadOnlyDictionary<string, string> binds, string key, string token) =>
        Inputs.FirstOrDefault(i => i.Key != key && Parse(binds[i.Key]).Contains(token));

    /// <summary>
    /// The changed values when <paramref name="key"/> gets <paramref name="token"/>: in place of its
    /// keys, or added to them; with <paramref name="moveFrom"/>, also taken off that input.
    /// </summary>
    public static Dictionary<string, string> Bind(IReadOnlyDictionary<string, string> binds, string key,
                                                  string token, bool add, string? moveFrom = null)
    {
        var tokens = add ? Parse(binds[key]).Where(t => t != token).Append(token) : [token];
        var changed = new Dictionary<string, string> { [key] = Format(tokens) };
        if (moveFrom is not null) changed[moveFrom] = Format(Parse(binds[moveFrom]).Where(t => t != token));
        return changed;
    }

    private static readonly Dictionary<string, string> KeyLabels = new()
    {
        // The SDK's Plus is the = key (VK_OEM_PLUS).
        ["Quote"] = "Apostrophe", ["Plus"] = "Equals", ["LBracket"] = "Left bracket", ["RBracket"] = "Right bracket",
        ["Return"] = "Enter", ["PageUp"] = "Page Up", ["PageDown"] = "Page Down", ["CapsLock"] = "Caps Lock",
        ["Up"] = "Up arrow", ["Down"] = "Down arrow", ["Left"] = "Left arrow", ["Right"] = "Right arrow",
    };
}
