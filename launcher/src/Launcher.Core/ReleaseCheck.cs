using System.Security.Cryptography;

namespace WweAllStars.Launcher.Core;

/// <summary>One staged file the release must not ship, or a file it lacks; paths use '/'.</summary>
public sealed record ReleaseProblem(string Path, string Reason);

/// <summary>
/// The packaging check (spec, "Packaging"): a staged release folder must hold no game-derived
/// file (disc files, Title Update 2's files, title update or DLC packages, shaders, art, the
/// tile icon) and every file a player needs.
/// <c>scripts/package.ps1</c> runs it through <c>launcher/tools/Launcher.ReleaseCheck</c>.
/// </summary>
public static class ReleaseCheck
{
    public static IReadOnlyList<ReleaseProblem> Run(string stagingDirectory, DiscManifest disc, TitleUpdateManifest update)
    {
        var root = Path.GetFullPath(stagingDirectory);
        var gameFiles = disc.Files.Concat(update.Files).ToList();
        var bySize = gameFiles.ToLookup(file => file.Size);
        var byName = gameFiles.ToLookup(file => file.Path[(file.Path.LastIndexOf('/') + 1)..], StringComparer.OrdinalIgnoreCase);
        var problems = new List<ReleaseProblem>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var path = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (GameDerived(path, file, bySize, byName) is { } reason) problems.Add(new ReleaseProblem(path, reason));
        }
        foreach (var path in RequiredFiles)
            if (!File.Exists(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar))))
                problems.Add(new ReleaseProblem(path, "missing"));
        return problems;
    }

    /// <summary>The release folder's own name for the project's licence, under <c>licenses</c>.</summary>
    public const string LicenseFile = "licenses/WWEAllStarsRecomp-LICENSE.txt";

    /// <summary>What a player needs from the zip (spec, "Folder layout"; ADR-0018 for the launcher's DLLs).</summary>
    public static IReadOnlyList<string> RequiredFiles { get; } =
    [
        "WWEAllStarsLauncher.exe", "libSkiaSharp.dll", "libHarfBuzzSharp.dll", "av_libglesv2.dll",
        "README.txt", LicenseFile,
        "game/" + PortableLayout.GameExecutableName,
        "xenosrecomp/" + XenosRecompConverter.CompilerName,
        "xenosrecomp/" + XenosRecompConverter.IncludeName,
    ];

    /// <summary>
    /// Folders a player's own install fills (spec, "Folder layout"), the developer's extracted
    /// disc (<c>assets</c>) and the SDK's game-art folder (<c>metadata</c>, cmake/no_game_art.cmake).
    /// </summary>
    private static readonly HashSet<string> PlayerDataFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "gamedata", "gamedata.partial", "updatedata", "updatedata.partial", "userdata", "cache", "logs", "assets", "metadata",
    };

    /// <summary>
    /// The only kinds of file a release ships: programs, their DLLs, text (README, licences)
    /// and XenosRecomp's <c>shader_common.h</c>. Anything else (shader packs and containers,
    /// images, disc files, settings) could be game-derived or a developer's own.
    /// </summary>
    private static readonly HashSet<string> ReleaseFileTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".txt", ".h",
    };

    private static string? GameDerived(string path, string file, ILookup<long, ManifestEntry> bySize,
        ILookup<string, ManifestEntry> byName)
    {
        var parts = path.Split('/');
        if (parts[..^1].FirstOrDefault(PlayerDataFolders.Contains) is { } folder)
            return $"in a '{folder}' folder, which holds game data, shaders or a player's files";
        if (byName[parts[^1]].FirstOrDefault() is { } named) return $"named like a game file ({named.Path})";
        if (StfsPackage.TryReadHeader(file) is not null)
            return "an Xbox 360 package (a title update or DLC): players add their own";
        if (!ReleaseFileTypes.Contains(Path.GetExtension(path)))
            return "not a program, DLL or text file: shaders, art, disc files and settings never ship";
        if (FromGame(file, bySize) is { } game) return $"a file from the disc or Title Update 2 ({game.Path})";
        return null;
    }

    // Content, not names: catches a game file however it was renamed.
    private static ManifestEntry? FromGame(string file, ILookup<long, ManifestEntry> bySize)
    {
        var candidates = bySize[new FileInfo(file).Length].ToList();
        if (candidates.Count == 0) return null;
        string hash;
        using (var stream = File.OpenRead(file)) hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        return candidates.FirstOrDefault(entry => entry.Sha256 == hash);
    }
}
