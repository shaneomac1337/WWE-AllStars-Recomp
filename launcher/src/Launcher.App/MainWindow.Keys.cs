using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher;

// The Keys page (keyboard spec): one row per controller input; clicking a row and pressing a
// key binds it.
public partial class MainWindow
{
    private readonly List<(KeyBindInput Input, Button Button)> _keyRows = [];
    // The row waiting for a key press, and whether the key is added to its keys; null when none.
    private (KeyBindInput Input, Button Button, bool Add)? _capturing;
    // A key another input already has, waiting for Move or Cancel.
    private (KeyBindInput Input, string Token, bool Add, KeyBindInput Other)? _pendingMove;

    private void WireKeys()
    {
        CloseKeysButton.Click += (_, _) => ShowSettings();
        ResetKeysButton.Click += (_, _) => OnResetKeys();
        MoveKeyButton.Click += (_, _) => FinishMove(move: true);
        KeepKeyButton.Click += (_, _) => FinishMove(move: false);
        // Tunnelling, so the pressed key reaches the capture before the focused button acts on it.
        AddHandler(KeyDownEvent, OnCaptureKeyDown, RoutingStrategies.Tunnel);
    }

    private void ShowKeys(Button opener)
    {
        try
        {
            BuildKeyRows();
        }
        catch (LauncherException error)
        {
            SettingsStatus.Text = error.Message;
            return;
        }
        KeysStatus.Text = "";
        ShowPanel(KeysView, opener);
    }

    private void BuildKeyRows()
    {
        _capturing = null;
        _pendingMove = null;
        KeysConflict.IsVisible = false;
        _keyRows.Clear();
        KeysList.Children.Clear();
        foreach (var input in KeyBinds.Inputs)
        {
            var button = new Button { Width = 250, HorizontalContentAlignment = HorizontalAlignment.Center };
            AutomationProperties.SetName(button, $"{input.Label} key");
            ToolTip.SetTip(button, "Click, then press the new key");
            var add = new Button { Content = "+", Width = 36, Padding = new(0), HorizontalContentAlignment = HorizontalAlignment.Center };
            add.Classes.Add("secondary");
            AutomationProperties.SetName(add, $"{input.Label}: add another key");
            ToolTip.SetTip(add, "Add another key");
            var row = (input, button);
            button.Click += (_, _) => StartCapture(row, add: false);
            add.Click += (_, _) => StartCapture(row, add: true);
            _keyRows.Add(row);
            var grid = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(0, 3) };
            grid.Children.Add(new TextBlock
            {
                Text = input.Label, FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var cell = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { button, add } };
            Grid.SetColumn(cell, 1);
            grid.Children.Add(cell);
            KeysList.Children.Add(grid);
        }
        ShowKeyValues();
    }

    // Each row's keys as saved.
    private void ShowKeyValues()
    {
        try
        {
            var values = _settings!.ReadKeyBinds();
            foreach (var (input, button) in _keyRows) button.Content = KeyBinds.DisplayValue(values[input.Key]);
        }
        catch (LauncherException error)
        {
            KeysStatus.Text = error.Message;
        }
    }

    private void StartCapture((KeyBindInput Input, Button Button) row, bool add)
    {
        CancelCapture();
        FinishMove(move: false);
        _capturing = (row.Input, row.Button, add);
        row.Button.Content = add ? "Press a key to add" : "Press a key";
        KeysStatus.Text = "Esc cancels.";
    }

    private void CancelCapture()
    {
        if (_capturing is null) return;
        _capturing = null;
        KeysStatus.Text = "";
        ShowKeyValues();
    }

    private void OnCaptureKeyDown(object? sender, KeyEventArgs e)
    {
        if (_capturing is not { } row || !KeysView.IsVisible) return;
        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            CancelCapture();
            return;
        }
        // A modifier alone waits for the key it goes with.
        if (e.Key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin)
            return;
        if (SdkKeyName(e.PhysicalKey) is not { } name)
        {
            KeysStatus.Text = "The game can't use that key. Press another one, or Esc.";
            return;
        }
        var token = KeyBinds.Token(name, e.KeyModifiers.HasFlag(KeyModifiers.Shift),
            e.KeyModifiers.HasFlag(KeyModifiers.Control), e.KeyModifiers.HasFlag(KeyModifiers.Alt));
        if (KeyBinds.Reserved(token) is { } reserved)
        {
            KeysStatus.Text = reserved + " Press another one, or Esc.";
            return;
        }
        _capturing = null;
        try
        {
            var values = _settings!.ReadKeyBinds();
            if (KeyBinds.UsedBy(values, row.Input.Key, token) is { } other)
            {
                _pendingMove = (row.Input, token, row.Add, other);
                row.Button.Content = KeyBinds.Display(token) + "?";
                KeysStatus.Text = "";
                KeysConflictText.Text =
                    $"{KeyBinds.Display(token)} is already {other.Label}. Move it to {row.Input.Label}?";
                KeysConflict.IsVisible = true;
                MoveKeyButton.Focus(NavigationMethod.Tab);
                return;
            }
            _settings.SetKeyBinds(KeyBinds.Bind(values, row.Input.Key, token, row.Add));
            KeysStatus.Text = $"{row.Input.Label}: {KeyBinds.DisplayValue(_settings.ReadKeyBinds()[row.Input.Key])}.";
        }
        catch (LauncherException error)
        {
            KeysStatus.Text = error.Message;
        }
        ShowKeyValues();
    }

    // Move takes the key off the other input; Cancel leaves both as they were.
    private void FinishMove(bool move)
    {
        if (_pendingMove is not { } pending) return;
        _pendingMove = null;
        KeysConflict.IsVisible = false;
        KeysStatus.Text = "";
        try
        {
            if (move)
            {
                var values = _settings!.ReadKeyBinds();
                _settings.SetKeyBinds(KeyBinds.Bind(values, pending.Input.Key, pending.Token, pending.Add,
                    moveFrom: pending.Other.Key));
                KeysStatus.Text = $"{KeyBinds.Display(pending.Token)} moved to {pending.Input.Label}.";
            }
        }
        catch (LauncherException error)
        {
            KeysStatus.Text = error.Message;
        }
        ShowKeyValues();
    }

    private void OnResetKeys()
    {
        CancelCapture();
        FinishMove(move: false);
        try
        {
            _settings!.ResetKeyBinds();
            KeysStatus.Text = "Every key is back to its default.";
        }
        catch (LauncherException error)
        {
            KeysStatus.Text = error.Message;
        }
        ShowKeyValues();
    }

    /// <summary>
    /// The SDK's name (<c>src/ui/keybinds.cpp</c>) for the key at this position, or null for one it
    /// has none for. The game reads SDL scancodes (<c>window_sdl.cpp</c>), positions on a US
    /// keyboard, so the page records the position too: on AZERTY the key labelled A is Q.
    /// </summary>
    private static string? SdkKeyName(PhysicalKey key)
    {
        var name = key.ToString();
        if (name.Length == 1 && char.IsAsciiLetterUpper(name[0])) return name;
        if (name.StartsWith("Digit", StringComparison.Ordinal)) return name["Digit".Length..];
        if (name.Length is 2 or 3 && name[0] == 'F' && int.TryParse(name[1..], out var f) && f is >= 1 and <= 24) return name;
        if (name.Length == "NumPad0".Length && name.StartsWith("NumPad", StringComparison.Ordinal) && char.IsAsciiDigit(name[^1]))
            return "Numpad" + name[^1];
        return key switch
        {
            PhysicalKey.Backquote => "Backtick",
            PhysicalKey.Minus => "Minus",
            PhysicalKey.Equal => "Plus",
            PhysicalKey.Comma => "Comma",
            PhysicalKey.Period => "Period",
            PhysicalKey.Semicolon => "Semicolon",
            PhysicalKey.Slash => "Slash",
            PhysicalKey.Backslash => "Backslash",
            PhysicalKey.BracketLeft => "LBracket",
            PhysicalKey.BracketRight => "RBracket",
            PhysicalKey.Quote => "Quote",
            PhysicalKey.Enter or PhysicalKey.NumPadEnter => "Return",
            PhysicalKey.Space => "Space",
            PhysicalKey.Tab => "Tab",
            PhysicalKey.Backspace => "Backspace",
            PhysicalKey.Delete => "Delete",
            PhysicalKey.Insert => "Insert",
            PhysicalKey.Home => "Home",
            PhysicalKey.End => "End",
            PhysicalKey.PageUp => "PageUp",
            PhysicalKey.PageDown => "PageDown",
            PhysicalKey.ArrowLeft => "Left",
            PhysicalKey.ArrowRight => "Right",
            PhysicalKey.ArrowUp => "Up",
            PhysicalKey.ArrowDown => "Down",
            PhysicalKey.NumPadAdd => "NumpadPlus",
            PhysicalKey.NumPadSubtract => "NumpadMinus",
            PhysicalKey.NumPadMultiply => "NumpadStar",
            PhysicalKey.NumPadDivide => "NumpadSlash",
            PhysicalKey.PrintScreen => "PrintScreen",
            PhysicalKey.Pause => "Pause",
            PhysicalKey.CapsLock => "CapsLock",
            PhysicalKey.NumLock => "NumLock",
            PhysicalKey.ScrollLock => "ScrollLock",
            _ => null,
        };
    }
}
