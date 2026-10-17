using System.Globalization;
using System.Text.RegularExpressions;
using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Tests;

/// <summary>
/// The exit code and log lines the launcher reads are written by the game's C++ (src/): these
/// tests read that source, so a change on one side alone fails here.
/// </summary>
public sealed class GameContractTests
{
    private static string Source(string relativePath)
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var path = Path.Combine(folder.FullName, relativePath);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException($"{relativePath} not found above {AppContext.BaseDirectory}");
    }

    [Fact]
    public void The_GPU_failure_exit_code_is_the_games()
    {
        var match = Regex.Match(Source("src/native/gpu_failure.h"), @"kGpuFailureExitCode = 0x([0-9A-Fa-f]+);");

        Assert.True(match.Success);
        Assert.Equal(GameExitReport.GpuFailureExitCode, int.Parse(match.Groups[1].Value, NumberStyles.HexNumber));
    }

    [Fact]
    public void The_GPU_failure_line_the_game_logs_is_read_as_one()
    {
        // GpuFailureLine's prefix, as the game writes it.
        var prefix = Regex.Match(Source("src/native/gpu_failure.h"), "return \"(native renderer: gpu failure )\"").Groups[1].Value;
        var log = Path.GetTempFileName();
        try
        {
            File.WriteAllText(log, $"!> 00001234 {prefix}VK_ERROR_DEVICE_LOST (present); ending the session.\n");

            Assert.Equal(GameExitKind.GpuFailure, GameExitReport.FromExit(1, log).Kind);
        }
        finally { File.Delete(log); }
    }

    [Fact]
    public void The_crash_report_line_the_game_logs_is_linked()
    {
        var format = Regex.Match(Source("src/crash_report.cpp"), """REXLOG_ERROR\("(crash report: saved )\{\}""").Groups[1].Value;
        var log = Path.GetTempFileName();
        try
        {
            File.WriteAllText(log, $"!> 00001234 {format}C:\\wwe\\logs\\crash-20261017-120000.zip\n");

            Assert.Equal(@"C:\wwe\logs\crash-20261017-120000.zip", GameExitReport.FromExit(unchecked((int)0xC0000005), log).CrashReportPath);
        }
        finally { File.Delete(log); }
    }

    [Theory]
    [InlineData("kAbortCode", 0xE0575701u)]
    [InlineData("kPureCallCode", 0xE0575702u)]
    [InlineData("kInvalidParameterCode", 0xE0575703u)]
    public void The_codes_the_game_raises_for_CRT_exits_are_the_ones_the_launcher_names(string name, uint code)
    {
        var match = Regex.Match(Source("src/crash_report_plan.h"), name + @" = 0x([0-9A-Fa-f]+);");

        Assert.True(match.Success);
        Assert.Equal(code, uint.Parse(match.Groups[1].Value, NumberStyles.HexNumber));
    }
}
