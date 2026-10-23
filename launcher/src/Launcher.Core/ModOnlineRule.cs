namespace WweAllStars.Launcher.Core;

/// <summary>
/// Cosmetic or gameplay, and the online rule (spec, "Cosmetic or gameplay", "Online rule";
/// CONTEXT.md, "Cosmetic mod"). With online play on, only cosmetic mods load, so a mod never
/// causes a desync.
/// </summary>
public static class ModOnlineRule
{
    /// <summary>
    /// The gameplay list: the package families holding scripts or gameplay data. A trailing
    /// <c>*</c> matches any rest of the short name, a <c>#</c> matches one digit, and every
    /// other character matches itself. The one place the list lives: RE adds families here.
    /// Ticket 03 found the hit volumes, character data and damage sets in
    /// <c>CombinedGlobalCharacters</c>; character attire (<c>CHR_*</c>) holds none, and only the
    /// arena packages with game logic or collision are gameplay (docs/research/modding.md,
    /// "Character and arena packages: cosmetic or gameplay?").
    /// </summary>
    public static readonly IReadOnlyList<string> GameplayPackageFamilies =
    [
        "Core", "Engine", "Game", "Game_*", "GameCharacterData*", "GameOnline", "AI*", "Scripts_*", "Aimed_*",
        "OnlineSubsystemLive",
        "CombinedGlobalCharacters", "CombinedStartup_*", "CHR_Physics", "Triggers_*", "Trigs_*", "AnimFSM_*",
        "AnimSet_*", "Env_Collision",
        "Arena_###", "Arena_###_M", "Arena_###_Ring", "Arena_###_Level", "Arena_Apron", "Arena_Global",
    ];

    /// <summary>Whether a package (by its short name, e.g. <c>Engine.xxx</c>) is on the gameplay list.</summary>
    public static bool IsGameplayPackage(string shortName)
    {
        var name = shortName.EndsWith(".xxx", StringComparison.OrdinalIgnoreCase) ? shortName[..^4] : shortName;
        return GameplayPackageFamilies.Any(family => Matches(name, family));
    }

    private static bool Matches(string name, string family)
    {
        var prefix = family.EndsWith('*');
        var pattern = prefix ? family[..^1] : family;
        if (prefix ? name.Length < pattern.Length : name.Length != pattern.Length) return false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var ok = pattern[i] == '#'
                ? char.IsAsciiDigit(name[i])
                : char.ToUpperInvariant(name[i]) == char.ToUpperInvariant(pattern[i]);
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>
    /// A mod's kind: cosmetic only when its manifest says so and it touches nothing on the
    /// gameplay list (a <c>config/</c> edit, a <c>content/</c> part or a gameplay-list package). The reason is set when
    /// a mod declared cosmetic is forced to gameplay. <paramref name="dlcPackages"/> are the packages
    /// an installed official pack also ships: they go into the combined content, which is offline only.
    /// </summary>
    internal static (ModKind Kind, string? ForcedReason) Classify(
        ModManifest? manifest, IReadOnlyList<ModPackage> packages, bool editsConfig, bool addsContent = false,
        IReadOnlyList<ModPackage>? dlcPackages = null)
    {
        if (manifest?.Kind != ModKind.Cosmetic) return (ModKind.Gameplay, null);
        var touches = new List<string>();
        if (editsConfig) touches.Add("it changes config");
        if (addsContent) touches.Add("it adds content (new wrestlers), which is offline only");
        if (dlcPackages is { Count: > 0 })
            touches.Add($"it replaces {string.Join(", ", dlcPackages.Select(package => package.Name))}, which an installed DLC pack ships; that loads offline only");
        var gameplay = packages.Select(package => package.Name).Where(IsGameplayPackage).ToList();
        if (gameplay.Count > 0)
            touches.Add($"it replaces {string.Join(", ", gameplay)}, " +
                (gameplay.Count == 1 ? "a script or gameplay-data package" : "script or gameplay-data packages"));
        return touches.Count == 0
            ? (ModKind.Cosmetic, null)
            : (ModKind.Gameplay, "Declared cosmetic, but " + string.Join(" and ", touches) + ".");
    }

    /// <summary>The Play area's line with online play on; empty when no mod is left out.</summary>
    public static string OffForOnlineLine(int count) => count switch
    {
        0 => "",
        1 => "1 gameplay mod off for online play: only cosmetic mods load online.",
        _ => $"{count} gameplay mods off for online play: only cosmetic mods load online.",
    };

    /// <summary>
    /// A mod's kind on the Mods page; a mod declared cosmetic but forced to gameplay says why.
    /// With online play on, a gameplay mod's gold "Off for online play" line gives the rule, so
    /// this line only names the kind.
    /// </summary>
    public static string KindLine(ModInfo mod, bool online) => mod.Kind == ModKind.Cosmetic
        ? "Cosmetic: also loads in online play."
        : mod.ForcedGameplayReason is { } reason ? $"Gameplay. {reason}"
        : mod.IsOffForOnline(online) ? "Gameplay." : "Gameplay: left out of online play.";
}
