using System.Globalization;

namespace WweAllStars.Launcher.Core;

/// <summary>
/// Where the mods' shaders come from (spec, "Shaders"; ticket 09): the <c>packages/</c> and
/// <c>content/</c> folders of the mods turned on, scanned like the disc's, and one line per package for the record of
/// what the shader pack was last built from.
/// </summary>
public sealed record ModShaderSources(IReadOnlyList<string> Folders, IReadOnlyList<string> Packages);

/// <summary>The mods' shaders: whether the shader pack needs them added before this Play.</summary>
public sealed partial class ModLibrary
{
    // In the cache beside the pack it describes.
    private string ShaderRecordPath => Path.Combine(layout.CacheDirectory, "mod-shaders.txt");

    /// <summary>
    /// The mods turned on, whatever online play leaves out: a shader the pack holds and the
    /// game never asks for is harmless, so the pack covers every mod that may load.
    /// </summary>
    public ModShaderSources ShaderSources()
    {
        lock (_lock) return SourcesOf(Scan().Where(mod => mod.Enabled));
    }

    /// <summary>
    /// Records that the shader pack now holds <paramref name="sources"/>' shaders, so the next
    /// Play with the same mods prepares nothing.
    /// </summary>
    public void ShadersPrepared(ModShaderSources sources)
    {
        try
        {
            Directory.CreateDirectory(layout.CacheDirectory);
            var temporary = ShaderRecordPath + ".tmp";
            File.WriteAllLines(temporary, sources.Packages);
            File.Move(temporary, ShaderRecordPath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Without the record the next Play prepares the shaders again; nothing else is lost.
        }
    }

    /// <summary>
    /// The prepared folder with the shader flag: set when a package in use is one the pack
    /// was not built with. Turning a mod off sets nothing; its shaders stay in the pack, unused.
    /// </summary>
    private PreparedMods WithShaders(PreparedMods prepared)
    {
        var sources = SourcesOf(Scan().Where(mod => mod.Enabled));
        var recorded = File.Exists(ShaderRecordPath) ? File.ReadAllLines(ShaderRecordPath) : [];
        return prepared with
        {
            Shaders = sources,
            ShadersChanged = !sources.Packages.All(package => recorded.Contains(package, StringComparer.Ordinal)),
        };
    }

    /// <summary>
    /// The mods' <c>packages/</c> folders, and the <c>content/</c> folders of content mods with
    /// new packages: a content mod loads offline, so its packages' shaders belong in the pack too.
    /// </summary>
    private static ModShaderSources SourcesOf(IEnumerable<ModInfo> mods)
    {
        var folders = new List<string>();
        var packages = new List<string>();
        foreach (var mod in mods)
        {
            if (mod.Packages.Count > 0)
            {
                folders.Add(Path.Combine(mod.Folder, PackagesFolderName));
                packages.AddRange(mod.Packages.Select(package => package.Path));
            }
            var contentPackages = mod.Content.Where(file => file.Path.EndsWith(".xxx", StringComparison.OrdinalIgnoreCase)).ToList();
            if (contentPackages.Count > 0)
            {
                folders.Add(Path.Combine(mod.Folder, ContentFolderName));
                packages.AddRange(contentPackages.Select(file => file.Path));
            }
        }
        return new ModShaderSources(folders, [.. packages.Select(path =>
        {
            var info = new FileInfo(path);
            return string.Create(CultureInfo.InvariantCulture, $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
        })]);
    }
}
