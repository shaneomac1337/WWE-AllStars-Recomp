using System.Runtime.InteropServices;
using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Platform;

/// <summary>The hard links and Recycle Bin the mod library uses (modding spec, "Overlay folder", "Remove").</summary>
internal static class FilePlatform
{
    public static IFileLinker Linker { get; } = OperatingSystem.IsWindows() ? new WindowsFileLinker() : new NoLinks();

    public static IRecycleBin RecycleBin { get; } = OperatingSystem.IsWindows() ? new WindowsRecycleBin() : new PlainDelete();

    /// <summary>Where the launcher has no hard links yet (Linux: M6): the mod library copies.</summary>
    private sealed class NoLinks : IFileLinker
    {
        public bool TryHardLink(string existingFile, string newLink) => false;
    }

    private sealed class PlainDelete : IRecycleBin
    {
        public void Recycle(string folder) => Directory.Delete(folder, recursive: true);
    }
}

/// <summary>
/// <c>CreateHardLinkW</c>: no extra disk space, and no admin right, unlike a symbolic link. It
/// fails across drives and on FAT, and the mod library copies then.
/// </summary>
internal sealed partial class WindowsFileLinker : IFileLinker
{
    public bool TryHardLink(string existingFile, string newLink) => CreateHardLinkW(newLink, existingFile, 0);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkW(string fileName, string existingFileName, nint securityAttributes);
}

/// <summary>Moves a folder to the Recycle Bin with <c>SHFileOperationW</c>, silently, so Remove can be undone.</summary>
internal sealed partial class WindowsRecycleBin : IRecycleBin
{
    private const uint Delete = 3;
    private const ushort Silent = 0x4, NoConfirmation = 0x10, AllowUndo = 0x40, NoErrorUi = 0x400;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileOperation
    {
        public nint Window;
        public uint Function;
        public nint From;
        public nint To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public nint NameMappings;
        public nint ProgressTitle;
    }

    public void Recycle(string folder)
    {
        // The list of paths ends with an extra NUL.
        var from = Marshal.StringToHGlobalUni(Path.GetFullPath(folder) + '\0');
        try
        {
            var operation = new FileOperation
            {
                Function = Delete,
                From = from,
                Flags = Silent | NoConfirmation | AllowUndo | NoErrorUi,
            };
            var result = SHFileOperationW(ref operation);
            if (result != 0 || operation.AnyOperationsAborted != 0)
                throw new IOException($"{folder} could not be moved to the Recycle Bin (error {result}).");
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHFileOperationW(ref FileOperation operation);
}
