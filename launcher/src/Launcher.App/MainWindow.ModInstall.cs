using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher;

/// <summary>
/// The Mods page's install half (ticket 07): the drop zone, "Add mod…", and the update and
/// wrap prompts the mod library's inspect step calls for.
/// </summary>
public partial class MainWindow
{
    private TaskCompletionSource<bool>? _modAnswer;

    private void WireModInstall()
    {
        AddModButton.Click += async (_, _) => { if (await PickModFiles() is { Count: > 0 } paths) AddMods(paths); };
        AddModFolderButton.Click += async (_, _) =>
        {
            if (await PickFolder(new FolderPickerOpenOptions { Title = "Choose a mod folder", AllowMultiple = false }) is { } path)
                AddMods([path]);
        };
        ModsView.AddHandler(DragDrop.DragOverEvent, OnDragOverFileOrFolder);
        ModsView.AddHandler(DragDrop.DropEvent, (_, e) => { if (DroppedItems(e) is { Count: > 0 } paths) AddMods(paths); });
        ModPromptYesButton.Click += (_, _) => AnswerMod(true);
        ModPromptNoButton.Click += (_, _) => AnswerMod(false);
    }

    private async Task<IReadOnlyList<string>> PickModFiles() =>
        await PickFiles(new FilePickerOpenOptions
        {
            Title = "Choose mods (zip, 7z or rar files, or loose game files); several at once work",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Mod archives") { Patterns = ["*.zip", "*.7z", "*.rar"] },
                FilePickerFileTypes.All,
            ],
        });

    /// <summary>
    /// Inspects the inputs, asks before each update and wrap, installs, and says what happened.
    /// Play stays off meanwhile; what is installed is used at the next Play.
    /// </summary>
    private async void AddMods(IReadOnlyList<string> inputs)
    {
        if (!_jobs.TryStart(HomeJob.AddMod, out var cancel)) return;
        SetModsInstalling(true);
        ModsStatusText.Text = "Looking for mods…";
        var library = Mods;
        var updated = new List<string>();
        var added = new List<(string Id, string Name)>();
        var notes = new List<string>();
        try
        {
            using var intake = await Task.Run(() => library.Inspect(inputs, cancel), cancel);
            foreach (var mod in intake.Mods)
            {
                var name = mod.Manifest.Name;
                if (mod.Installed is { } installed && !await AskMod(
                        $"Update {name} from {ModVersionText(installed.Manifest?.Version)} to {ModVersionText(mod.Manifest.Version)}?",
                        "It keeps its on/off setting and its place in the load order.", "Update"))
                {
                    notes.Add($"{name} was not updated.");
                    continue;
                }
                var installedId = await Task.Run(() => library.Install(mod, cancel), cancel);
                if (mod.IsUpdate) updated.Add(name);
                else added.Add((installedId, name));
            }
            foreach (var wrap in intake.Wraps)
            {
                ModNameBox.Text = wrap.SuggestedName;
                ModCosmeticBox.IsChecked = false;
                if (!await AskMod($"Install {Path.GetFileName(wrap.Source)} as a mod?",
                        $"It has no mod.toml; the launcher writes one. Files: {string.Join(", ", wrap.Files)}. "
                        + "Only mods that change looks are used in online play.", "Install", wrapping: true))
                {
                    notes.Add($"{Path.GetFileName(wrap.Source)} was not installed.");
                    continue;
                }
                var name = (ModNameBox.Text ?? "").Trim();
                var cosmetic = ModCosmeticBox.IsChecked == true;
                var existing = library.Installed(name);
                if (existing is not null && !await AskMod(
                        $"Replace the installed {existing.Manifest?.Name ?? existing.Id} with {Path.GetFileName(wrap.Source)}?",
                        "It keeps its on/off setting and its place in the load order.", "Replace"))
                {
                    notes.Add($"{name} was not replaced.");
                    continue;
                }
                var id = await Task.Run(() => library.Wrap(wrap, name, cosmetic, cancel), cancel);
                if (existing is null) added.Add((id, id));
                else updated.Add(id);
            }
            notes.AddRange(intake.Refusals.Select(refusal => $"{Path.GetFileName(refusal.Source)}: {refusal.Reason}"));
        }
        catch (OperationCanceledException)
        {
            notes.Add("Adding mods was cancelled.");
        }
        catch (Exception error) when (error is LauncherException or IOException or UnauthorizedAccessException)
        {
            notes.Add(error.Message);
        }
        finally
        {
            HideModPrompt();
            _jobs.End(HomeJob.AddMod);
            SetModsInstalling(false);
            // A new mod starts off (spec): its toggle takes the focus, so the player sees where to turn it on.
            RefreshModsList(added.Count > 0 ? added[0].Id : null, "toggle");
        }
        var summary = InstallSummary(updated, [.. added.Select(mod => mod.Name)]);
        if (summary.Length > 0 && _game.IsRunning) summary += " They are used the next time you choose Play.";
        ModsStatusText.Text = string.Join(" ", new[] { summary }.Concat(notes).Where(text => text.Length > 0));
        foreach (var line in updated.Select(name => "updated " + name).Concat(added.Select(mod => "added " + mod.Name)).Concat(notes))
            App.Log.Write("Mods: " + line);
    }

    /// <summary>
    /// "Updated A. Installed B and C. They start off: turn them on to use them." An update keeps
    /// its on/off setting, so only new mods get the hint (ticket 19: the owner missed that a new
    /// mod starts off).
    /// </summary>
    private static string InstallSummary(IReadOnlyList<string> updated, IReadOnlyList<string> installed)
    {
        static string Names(IReadOnlyList<string> names) =>
            names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
        var parts = new List<string>();
        if (updated.Count > 0) parts.Add($"Updated {Names(updated)}.");
        if (installed.Count == 1) parts.Add($"Installed {installed[0]}. It starts off: turn it on to use it.");
        else if (installed.Count > 1) parts.Add($"Installed {Names(installed)}. They start off: turn them on to use them.");
        return string.Join(" ", parts);
    }

    private static string ModVersionText(string? version) => string.IsNullOrWhiteSpace(version) ? "no version" : version;

    /// <summary>Shows the prompt and waits for Install/Update or Skip; the wrap form adds the name and the looks-only box.</summary>
    private Task<bool> AskMod(string question, string detail, string yes, bool wrapping = false)
    {
        ModPromptText.Text = question;
        ModPromptDetail.Text = detail;
        ModPromptYesButton.Content = yes;
        ModNameBox.IsVisible = ModCosmeticBox.IsVisible = wrapping;
        ModPrompt.IsVisible = true;
        _modAnswer = new TaskCompletionSource<bool>();
        (wrapping ? (Control)ModNameBox : ModPromptYesButton).Focus(NavigationMethod.Tab);
        return _modAnswer.Task;
    }

    private void AnswerMod(bool yes)
    {
        if (yes && ModNameBox.IsVisible && ModLibrary.FolderNameFor(ModNameBox.Text ?? "") is null)
        {
            ModPromptDetail.Text = "Give the mod a name made of letters or digits.";
            ModNameBox.Focus(NavigationMethod.Tab);
            return;
        }
        var answer = _modAnswer;
        _modAnswer = null;
        ModPrompt.IsVisible = false;
        answer?.TrySetResult(yes);
    }

    private void HideModPrompt()
    {
        ModPrompt.IsVisible = false;
        _modAnswer?.TrySetResult(false);
        _modAnswer = null;
    }

    private void SetModsInstalling(bool running)
    {
        AddModButton.IsEnabled = AddModFolderButton.IsEnabled = !running;
        DragDrop.SetAllowDrop(ModsView, !running);
        UpdatePlayButton();
    }
}
