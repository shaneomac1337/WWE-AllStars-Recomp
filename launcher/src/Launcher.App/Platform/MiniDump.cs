using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WweAllStars.Launcher.Platform;

/// <summary>A small dump of the launcher (threads and stacks, no heap) for its crash report.</summary>
internal static partial class MiniDump
{
    private const int MiniDumpNormal = 0x0;
    private const int MiniDumpWithThreadInfo = 0x1000;

    /// <summary>Writes the dump to a temporary file and returns it, or null when it fails.</summary>
    public static string? TryWrite(string directory)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var path = Path.Combine(directory, $"launcher-{Environment.ProcessId}.dmp.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            using var process = Process.GetCurrentProcess();
            using (var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                if (!MiniDumpWriteDump(process.Handle, (uint)Environment.ProcessId, file.SafeFileHandle,
                        MiniDumpNormal | MiniDumpWithThreadInfo, 0, 0, 0))
                    throw new IOException("MiniDumpWriteDump failed");
            }
            return path;
        }
        catch (Exception)
        {
            try { File.Delete(path); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return null;
        }
    }

    [LibraryImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MiniDumpWriteDump(nint process, uint processId, SafeFileHandle file, int type,
        nint exceptionParam, nint userStreamParam, nint callbackParam);
}
