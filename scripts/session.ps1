# Drive a background game session: muted, never focused, window parked
# off-screen. Input goes through the scripted pad (src/script_input.h).
#   pwsh scripts/session.ps1 start [-Name s1] [-UserData <dir>] [-UpdateData <dir>] [-ExtraArguments '--wwe_native=true']
#   pwsh scripts/session.ps1 input 'press A' 'wait 2000' 'press START'
#   pwsh scripts/session.ps1 shot <stem>      window capture -> <session>/<stem>.png
#   pwsh scripts/session.ps1 status | stop
# Files live in analysis/session-<Name>/ (input.txt, runtime.log, shots).
# Adapted from wwe-svr2011-pc's port/tools/session.ps1 (KaikoClanworth1, de85161),
# reused with the author's permission.
param(
    [Parameter(Position = 0)][ValidateSet('start', 'input', 'shot', 'status', 'stop')][string]$Action,
    [Parameter(Position = 1, ValueFromRemainingArguments = $true)][string[]]$Rest = @(),
    [string]$Name = 'session',
    [ValidateSet('info', 'debug', 'trace')][string]$LogLevel = 'info',
    [ValidateSet('fsi', 'rtv')][string]$RenderTargetPath = 'fsi',
    [string[]]$ExtraArguments = @(),
    # A throwaway copy keeps the owner's profile and saves out of a test run.
    [string]$UserData = '',
    # Another update folder (e.g. one a config mod was merged into) instead of updatedata.
    [string]$UpdateData = '',
    # Another game exe (e.g. a build variant, or a copy with a different hash).
    [string]$Executable = ''
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$executable = if ($Executable) { (Resolve-Path $Executable).Path } else { "$projectRoot/out/build/win-amd64-relwithdebinfo-vulkan/wweallstars.exe" }
$dir = Join-Path $projectRoot "analysis/session-$Name"
$state = Join-Path $dir 'session.json'
$inputFile = Join-Path $dir 'input.txt'

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class SessionWin {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
}
'@
[void][SessionWin]::SetProcessDPIAware()

# Off-screen without focus: SWP_NOSIZE|SWP_NOZORDER|SWP_NOACTIVATE, then
# SW_SHOWNOACTIVATE, which also restores a minimized window so it keeps rendering.
# Left of the whole virtual desktop (SM_XVIRTUALSCREEN), so a monitor left of the main one
# never shows it.
function Set-Parked([IntPtr]$h) {
    $r = [SessionWin+RECT]::new(); [void][SessionWin]::GetWindowRect($h, [ref]$r)
    $x = [SessionWin]::GetSystemMetrics(76) - ($r.R - $r.L) - 100
    [void][SessionWin]::SetWindowPos($h, [IntPtr]::Zero, $x, 0, 0, 0, 0x1 -bor 0x4 -bor 0x10)
    [void][SessionWin]::ShowWindow($h, 4)
}

# Close the window first so the game shuts down cleanly (its final log lines,
# such as the xma-stats summary, are written then); kill it if that hangs.
function Stop-Session {
    if (!(Test-Path $state)) { return }
    $s = Get-Content $state | ConvertFrom-Json
    $p = Get-Process -Id $s.pid -ErrorAction SilentlyContinue | Where-Object ProcessName -Like 'wweallstars*'
    if (!$p) { return }
    if ($p.CloseMainWindow() -and $p.WaitForExit(15000)) { return }
    $p | Stop-Process -Force
}

function Get-Session {
    if (!(Test-Path $state)) { throw "No session '$Name'. Run: session.ps1 start -Name $Name" }
    $s = Get-Content $state | ConvertFrom-Json
    $p = Get-Process -Id $s.pid -ErrorAction SilentlyContinue
    if (!$p -or $p.ProcessName -notlike 'wweallstars*') { throw "The game is not running (pid $($s.pid)). Log: $($s.log)" }
    return @{ proc = $p; log = $s.log }
}

switch ($Action) {
    'start' {
        if (!(Test-Path $executable)) { throw "Build first: $executable" }
        Stop-Session
        New-Item -ItemType Directory -Force $dir | Out-Null
        Set-Content $inputFile '' -NoNewline
        $log = Join-Path $dir 'runtime.log'
        Remove-Item $log -ErrorAction SilentlyContinue
        # The SDL window never takes focus, so the owner's desktop is left alone.
        $env:SDL_WINDOW_ACTIVATE_WHEN_SHOWN = '0'; $env:SDL_WINDOW_ACTIVATE_WHEN_RAISED = '0'
        $arguments = @('--gpu_plugin=xenos', '--vulkan_device=-1',
            "--render_target_path_vulkan=$RenderTargetPath",
            "--game_data_root=$projectRoot/assets", "--update_data_root=$(if ($UpdateData) { $UpdateData } else { "$projectRoot/updatedata" })", "--user_data_root=$(if ($UserData) { $UserData } else { "$projectRoot/userdata/vulkan" })",
            "--cache_root=$projectRoot/cache/vulkan",
            "--log_file=$log", "--log_level=$LogLevel", '--log_flush_interval=1',
            "--wwe_input_script=$inputFile", '--audio_mute=true', '--fullscreen=false')
        foreach ($argument in $ExtraArguments) {
            if ($argument -notmatch '^--[a-z0-9_]+=') { throw "Invalid runtime argument: $argument" }
            $arguments += $argument
        }
        # The game's default server is the owner's: a developer session goes online against
        # out/server on this PC unless it names a server.
        if (($ExtraArguments -contains '--wwe_online=true') -and !($ExtraArguments -match '^--wwe_online_server=')) {
            $arguments += '--wwe_online_server=127.0.0.1'
        }
        $quoted = $arguments | ForEach-Object { '"' + $_ + '"' }
        $p = Start-Process $executable -WorkingDirectory (Split-Path $executable -Parent) -ArgumentList $quoted `
            -PassThru -WindowStyle Minimized
        for ($t = 0; $t -lt 200 -and !$p.HasExited; $t++) {
            $p.Refresh(); if ($p.MainWindowHandle -ne 0) { break }; Start-Sleep -Milliseconds 100
        }
        if ($p.HasExited) { throw "The game exited at startup (code $($p.ExitCode)). Log: $log" }
        if ($p.MainWindowHandle -eq 0) {
            Write-Warning 'No game window after 20 s: it stays minimized and shots will be empty.'
        } else {
            Set-Parked $p.MainWindowHandle
        }
        @{ pid = $p.Id; log = $log; started = (Get-Date -Format o) } | ConvertTo-Json | Set-Content $state
        # A native run that fell back to Emulated shows a black window and measures the wrong
        # renderer: close it at once and keep the log (native_renderer.cpp logs either line).
        if (!($ExtraArguments -contains '--wwe_native_renderer=false')) {
            for ($t = 0; $t -lt 600 -and !$p.HasExited; $t++) {
                $line = Select-String -Path $log -Pattern 'native renderer: enabled|running on Emulated' -ErrorAction SilentlyContinue |
                    Select-Object -First 1
                if ($line) { break }
                Start-Sleep -Milliseconds 100
            }
            if ($line -and $line.Line -match 'running on Emulated') {
                Stop-Session
                throw "The native renderer fell back to Emulated; the game is closed. $($line.Line) Log: $log"
            }
            if (!$line) { Write-Warning "No native renderer line in 60 s. Log: $log" }
        }
        "started pid $($p.Id), session $dir"
    }
    'input' {
        $null = Get-Session
        if (!$Rest.Count) { throw 'Give at least one command, e.g. input "press A"' }
        Add-Content $inputFile ($Rest -join "`n")
        "queued: $($Rest -join ' | ')"
    }
    'shot' {
        $s = Get-Session
        $stem = if ($Rest.Count) { $Rest[0] } else { 'shot-' + (Get-Date -Format 'HHmmss') }
        $s.proc.Refresh()
        $h = $s.proc.MainWindowHandle
        if ($h -eq [IntPtr]::Zero) { throw 'Window not found' }
        # Minimized windows (Win+D, say) capture as a bare caption bar.
        if ([SessionWin]::IsIconic($h)) { Set-Parked $h; Start-Sleep -Milliseconds 500 }
        $r = [SessionWin+RECT]::new(); [void][SessionWin]::GetWindowRect($h, [ref]$r)
        $w = $r.R - $r.L; $hh = $r.B - $r.T
        $bmp = [System.Drawing.Bitmap]::new($w, $hh)
        $g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc()
        # PW_RENDERFULLCONTENT: the window renders itself, so off-screen works.
        [void][SessionWin]::PrintWindow($h, $dc, 2); $g.ReleaseHdc($dc)
        $file = Join-Path $dir "$stem.png"
        $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
        $file
    }
    'status' {
        $s = Get-Session
        "running pid $($s.proc.Id), cpu $([int]$s.proc.CPU)s, ws $([int]($s.proc.WorkingSet64 / 1MB)) MB"
        $r = [SessionWin+RECT]::new(); [void][SessionWin]::GetWindowRect($s.proc.MainWindowHandle, [ref]$r)
        $fg = 0; [void][SessionWin]::GetWindowThreadProcessId([SessionWin]::GetForegroundWindow(), [ref]$fg)
        "window at ($($r.L), $($r.T)); foreground process: $((Get-Process -Id $fg -ErrorAction SilentlyContinue).ProcessName)"
        Get-Content $s.log | Select-String -Pattern '\[(error|critical|FATAL)\]' | Select-Object -Last 8 | ForEach-Object { $_.Line }
    }
    'stop' {
        Stop-Session
        'stopped'
    }
}
