using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Tests;

/// <summary>Stands in for <c>CreateHardLinkW</c>: copies and records, or fails as a FAT drive would.</summary>
internal sealed class FakeLinker : IFileLinker
{
    public bool Works { get; set; } = true;
    public List<(string Source, string Link)> Links { get; } = [];

    public bool TryHardLink(string existingFile, string newLink)
    {
        if (!Works) return false;
        File.Copy(existingFile, newLink);
        Links.Add((existingFile, newLink));
        return true;
    }
}

/// <summary>Records what went to the Recycle Bin, and deletes it.</summary>
internal sealed class FakeRecycleBin : IRecycleBin
{
    public List<string> Recycled { get; } = [];

    public void Recycle(string folder)
    {
        Recycled.Add(folder);
        Directory.Delete(folder, recursive: true);
    }
}
