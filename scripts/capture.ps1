# Capture a session the owner plays by hand: PresentMon records every present of the game (or
# Xenia Canary), the Overwolf Vulkan layers stay out of it, and Ctrl+Shift+M marks a moment.
#   pwsh scripts/capture.ps1                      Native, enhancements at their defaults
#   pwsh scripts/capture.ps1 -Target canary       the same route in Xenia Canary
#   pwsh scripts/capture.ps1 -Emulated -Notes 'FreeSync off, 60 Hz'
#   pwsh scripts/capture.ps1 -FreshInstall        first-run capture: caches reset to right after setup
#   pwsh scripts/capture.ps1 -Variant v3o2         the same with a build-flag variant (build.ps1 -Variant)
#   pwsh scripts/capture.ps1 -ResetOnly           the same reset, then list the cache and stop
# -FreshInstall empties the cache folder (shader pack, top-ups, pipeline store, driver cache)
# and the pipeline store beside the game, then runs the launcher's headless setup steps
# (--prepare-shaders, then --prepare-pipelines, as setup does since zero-stutter 07) into a
# staging root and moves what they wrote into the cache folder. The pipeline step starts the
# game headless on the GPU (its prepare mode).
# It needs the launcher (build.ps1 -Action Launcher).
# Everything lands in analysis/capture-<target>-<time>/: presentmon.csv, runtime.log, marks.txt
# (same format as SVR2011's mark.ps1), display.json and, for the game on Native, gpu_timer.csv
# (Native's GPU timer). display.json records the Graphics preset, its settings and whether the
# launcher was open, so the smoothness report says what the capture measured. PresentMon needs admin rights, so
# Windows asks once per capture. Close the game with the window's X when done.
param(
    [ValidateSet('game', 'canary')][string]$Target = 'game',
    # The Emulated renderer; -Classic is its former name (ADR-0020).
    [Alias('Classic')][switch]$Emulated,
    [string]$Notes = '',
    [string[]]$ExtraArguments = @(),
    [switch]$FreshInstall,
    [switch]$ResetOnly,
    # A build-flag variant from build.ps1 -Variant (ticket 11), e.g. v3o2.
    [ValidatePattern('^[a-z0-9]*$')][string]$Variant = '',
    # Internal: the elevated half, which runs PresentMon until process -WatchId exits.
    [int]$WatchId = 0,
    [string]$Csv = ''
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$presentMon = Get-ChildItem "$projectRoot/.tools/presentmon" -Filter 'PresentMon-*-x64.exe' |
    Sort-Object Name | Select-Object -Last 1 -ExpandProperty FullName
if (!$presentMon) { throw 'PresentMon is missing from .tools/presentmon.' }
$session = 'WweCapture'

# PresentMon's --terminate_on_proc_exit does not fire for these processes, so the elevated half
# waits for the game itself and then stops the trace session, which makes PresentMon flush and exit.
if ($WatchId) {
    $watched = Get-Process -Id $WatchId -ErrorAction SilentlyContinue
    $exe = "$($watched.ProcessName).exe"
    $recorder = Start-Process -FilePath $presentMon -PassThru -WindowStyle Minimized -ArgumentList @(
        '--process_name', $exe, '--output_file', "`"$Csv`"", '--date_time', '--v2_metrics',
        '--stop_existing_session', '--session_name', $session, '--no_console_stats')
    if ($watched) { $watched.WaitForExit() }
    & $presentMon --terminate_existing_session --session_name $session | Out-Null
    if (!$recorder.WaitForExit(10000)) { $recorder | Stop-Process -Force }
    exit 0
}
$gameExe = "$projectRoot/out/build/win-amd64-relwithdebinfo$(if ($Variant) { "-$Variant" })-vulkan/wweallstars.exe"
$cacheRoot = "$projectRoot/cache/vulkan"

# The cache as setup leaves it: the launcher's headless setup steps write into an empty staging
# root, and only what they wrote ends up in the cache folder.
function Reset-ToFreshInstall {
    if ($Target -ne 'game') { throw '-FreshInstall and -ResetOnly apply to the game, not Canary.' }
    $launcher = "$projectRoot/out/launcher/publish/WWEAllStarsLauncher.exe"
    $compiler = "$projectRoot/.tools/xenosrecomp/thirdparty/dxc-bin/bin/x64/dxcompiler.dll"
    $shaderCommon = "$projectRoot/.tools/xenosrecomp/XenosRecomp/shader_common.h"
    foreach ($path in $launcher, $compiler, $shaderCommon) {
        if (!(Test-Path -LiteralPath $path)) { throw "Build first: $path" }
    }
    $stage = "$projectRoot/out/fresh-setup"
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    New-Item -ItemType Directory -Force "$stage/xenosrecomp" | Out-Null
    # The release layout: dxcompiler.dll and shader_common.h in xenosrecomp/ under the root.
    Copy-Item $compiler, $shaderCommon -Destination "$stage/xenosrecomp"

    # The pipeline store is <cache_root>/native-pipelines; builds before zero-stutter 02 kept it
    # beside the game, and a store left there is cleared too.
    foreach ($folder in $cacheRoot, "$(Split-Path $gameExe -Parent)/cache/native-pipelines") {
        if (Test-Path -LiteralPath $folder) { Get-ChildItem -LiteralPath $folder -Force | Remove-Item -Recurse -Force }
    }
    New-Item -ItemType Directory -Force $cacheRoot | Out-Null

    # The launcher is a GUI exe, so PowerShell would not wait for it or see its exit code.
    $setup = Start-Process -FilePath $launcher -NoNewWindow -Wait -PassThru -ArgumentList @(
        '--root', "`"$stage`"", '--game-data', "`"$projectRoot/assets`"", '--game', "`"$gameExe`"",
        '--prepare-shaders', '--prepare-pipelines')
    if ($setup.ExitCode -ne 0) { throw "The launcher's setup steps failed (exit $($setup.ExitCode))." }
    Get-ChildItem -LiteralPath "$stage/cache" -Force | Move-Item -Destination $cacheRoot
    Remove-Item -LiteralPath $stage -Recurse -Force
    Write-Output "Cache reset to a fresh install: $cacheRoot"
    Get-ChildItem -LiteralPath $cacheRoot -Recurse -Force |
        ForEach-Object { '  {0} ({1:N0} bytes)' -f $_.FullName.Substring($cacheRoot.Length + 1), $_.Length }
}
if ($FreshInstall -or $ResetOnly) { Reset-ToFreshInstall }
if ($ResetOnly) { return }

$run = Join-Path $projectRoot ("analysis/capture-$Target-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force $run | Out-Null

$info = [System.Diagnostics.ProcessStartInfo]::new()
$info.UseShellExecute = $false
if ($Target -eq 'canary') {
    $info.FileName = "$projectRoot/.tools/xenia-canary/xenia_canary.exe"
    $info.WorkingDirectory = Split-Path $info.FileName -Parent
    $iso = Get-ChildItem $projectRoot -Filter '*.iso' | Select-Object -First 1 -ExpandProperty FullName
    if (!$iso) { throw 'No ISO in the project root.' }
    $arguments = @("--log_file=$run/runtime.log", '--log_level=2', '--gpu=vulkan',
        '--discord=false', $iso)
} else {
    $info.FileName = $gameExe
    if (!(Test-Path $info.FileName)) { throw "Build first: $($info.FileName)" }
    $info.WorkingDirectory = Split-Path $info.FileName -Parent
    $arguments = @('--gpu_plugin=xenos', '--vulkan_device=-1',
        "--game_data_root=$projectRoot/assets", "--update_data_root=$projectRoot/updatedata", "--user_data_root=$projectRoot/userdata/vulkan",
        "--cache_root=$cacheRoot",
        "--log_file=$run/runtime.log", '--log_level=info', '--log_flush_interval=1',
        "--present_pacing_log=$run/present_pacing.csv", '--frame_event_log=true',
        '--display_vblank_log=true', '--wwe_tick_log=true', '--log_file_opens=.bik')
    # The PC SETTINGS page's file decides the renderer when it names one, else the
    # game's default (Native, ADR-0020); a command-line flag would pin it (in-game
    # settings ticket 11).
    $settings = "$projectRoot/userdata/vulkan/wweallstars.settings.toml"  # in --user_data_root
    $chosen = if (Test-Path $settings) {
        Select-String -Path $settings -Pattern '^\s*wwe_native_renderer\s*=\s*(true|false)' |
            Select-Object -First 1 | ForEach-Object { $_.Matches[0].Groups[1].Value }
    }
    if ($Emulated) { $arguments += '--wwe_native_renderer=false' }
    # display.json keeps the value 'classic' for Emulated, as older captures wrote it.
    $renderer = if ($Emulated -or $chosen -eq 'false') { 'classic' } else { 'native' }
    if ($renderer -eq 'native') { $arguments += "--wwe_native_gpu_timer_csv=$run/gpu_timer.csv" }
}
foreach ($argument in $ExtraArguments) {
    if ($argument -notmatch '^--[a-z0-9_]+=') { throw "Invalid runtime argument: $argument" }
    $arguments += $argument
}
foreach ($argument in $arguments) { $info.ArgumentList.Add($argument) }
# Overwolf's implicit Vulkan layers hook every present; these variables keep them out.
$info.Environment['DISABLE_VULKAN_OW_OVERLAY_LAYER'] = '1'
$info.Environment['DISABLE_VULKAN_OW_OBS_CAPTURE'] = '1'

# The Graphics preset and its settings as this start uses them: a launch argument outranks the
# settings file, which outranks the default. The members, presets and defaults come from the
# game's own schema export, the one definition (src/settings/registry.h).
$graphics = $null
if ($Target -eq 'game') {
    $schema = (& $gameExe --print-settings-schema | Out-String) | ConvertFrom-Json
    $group = $schema.settings | Where-Object { $_.members } | Select-Object -First 1
    if ($group) {
        $saved = @{}
        if (Test-Path $settings) {
            foreach ($line in Get-Content $settings) {
                if ($line -match '^\s*([a-z0-9_]+)\s*=\s*"?([^"#]*?)"?\s*(#.*)?$') { $saved[$Matches[1]] = $Matches[2] }
            }
        }
        $values = [ordered]@{}
        foreach ($member in $group.members) {
            $given = $arguments | Where-Object { $_ -like "--$member=*" } | Select-Object -Last 1
            $values[$member] = if ($given) { $given.Split('=', 2)[1] }
                elseif ($saved.ContainsKey($member)) { $saved[$member] }
                else { ($schema.settings | Where-Object key -eq $member).default }
        }
        # Config values are written with a '.' decimal whatever the system's locale.
        function Test-Same([string]$a, [string]$b) {
            $x = 0.0; $y = 0.0
            $float = [Globalization.NumberStyles]::Float; $invariant = [Globalization.CultureInfo]::InvariantCulture
            ($a -eq $b) -or ([double]::TryParse($a, $float, $invariant, [ref]$x) -and
                [double]::TryParse($b, $float, $invariant, [ref]$y) -and $x -eq $y)
        }
        $preset = 'custom'
        foreach ($candidate in $group.presets) {
            $all = $true
            for ($m = 0; $m -lt $group.members.Count; $m++) {
                $all = $all -and (Test-Same $values[$group.members[$m]] $candidate.values[$m])
            }
            if ($all) { $preset = $candidate.value; break }
        }
        $graphics = @{ preset = $preset; settings = $values }
    }
}
$launcherOpen = [bool](Get-Process -Name 'WWEAllStarsLauncher' -ErrorAction SilentlyContinue)

$display = Get-CimInstance Win32_VideoController | Where-Object CurrentRefreshRate |
    Select-Object Name, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate
$record = [ordered]@{
    target = $Target; renderer = if ($Target -eq 'canary') { 'canary' } else { $renderer }
    started = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff'); notes = $Notes
    display = @($display); arguments = $arguments; launcher_open = $launcherOpen
}
if ($graphics) { $record.preset = $graphics.preset; $record.settings = $graphics.settings }
$record | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $run 'display.json')

$process = [System.Diagnostics.Process]::Start($info)
$exe = Split-Path $info.FileName -Leaf
$csv = Join-Path $run 'presentmon.csv'
$recorder = Start-Process -FilePath (Get-Process -Id $PID).Path -Verb RunAs -WindowStyle Hidden -PassThru `
    -ArgumentList @('-NoProfile', '-File', "`"$PSCommandPath`"", '-WatchId', $process.Id, '-Csv', "`"$csv`"")

Add-Type @'
using System.Runtime.InteropServices;
public static class CaptureKeys { [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key); }
'@
function Test-Down([int]$key) { ([CaptureKeys]::GetAsyncKeyState($key) -band 0x8000) -ne 0 }
$marks = Join-Path $run 'marks.txt'
Write-Output "Capturing $exe into $run"
Write-Output 'Press Ctrl+Shift+M to mark a moment. Close the game with its window X to finish.'
$count = 0; $held = $false
while (!$process.HasExited) {
    $down = (Test-Down 0x11) -and (Test-Down 0x10) -and (Test-Down 0x4D)
    if ($down -and !$held) {
        $count++
        $line = "$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff'))`tmark $count"
        Add-Content -LiteralPath $marks -Value $line
        [console]::Beep(880, 80)
        Write-Output "Marked: $line"
    }
    $held = $down
    Start-Sleep -Milliseconds 15
}
[void]$recorder.WaitForExit(20000)
$rows = 0
if (Test-Path $csv) {
    $reader = [System.IO.StreamReader]::new([System.IO.FileStream]::new($csv, 'Open', 'Read', 'ReadWrite'))
    while ($null -ne $reader.ReadLine()) { $rows++ }
    $reader.Dispose()
    $rows--
}
Write-Output "Done: $rows presents, $count marks in $run"
