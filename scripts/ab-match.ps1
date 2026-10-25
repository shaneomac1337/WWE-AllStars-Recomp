# A/B two or more game builds on the same CPU 1v1 (Hogan idle vs Cena; the CPU wins), rounds
# alternating builds, and print one table: game work per tick, present interval stdev and
# 5 s windows with a frame over 25 ms (scripts/compare_builds.py). Settings are each build's
# defaults with a fresh profile; the window is parked off-screen, display lock and vsync stay on.
#   pwsh scripts/ab-match.ps1 -Builds old=<dir>/game/wweallstars.exe,new=out/build/.../wweallstars.exe
# Running an older release, and what differs between builds: docs/agents/ab-match.md.
param([Parameter(Mandatory)][string[]]$Builds,
      [int]$Rounds = 3,
      # Wall time per run from the first input; the match ends about 4 min in.
      [int]$Seconds = 330,
      # Measured span, in seconds after the first tick summary: the match's last 150 s.
      [int]$Skip = 120, [int]$Span = 150,
      [string[]]$ExtraArguments = @('--window_width=2560', '--window_height=1440'),
      [string]$Out = 'analysis/ab-match')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$exes = [ordered]@{}
foreach ($b in $Builds) {
    $name, $path = $b -split '=', 2
    if (!$path -or !(Test-Path $path)) { throw "Give name=path/to/wweallstars.exe, not '$b'" }
    $exes[$name] = (Resolve-Path $path).Path
}
# The route without the UnlockAll cheat, which opens an Xbox LIVE dialog on some profiles.
$all = @(Get-Content scripts/input/exhibition-1v1.txt | Where-Object { $_ -and $_ -notmatch '^#' })
$from = $all.IndexOf('press LEFT'); $to = $all.IndexOf('press UP')
$route = @($all[0..($from - 1)]) + @('wait 1000') + @($all[($to + 1)..($all.Count - 1)])
$arguments = @('--present_pacing_stats=true', '--wwe_tick_log=true') + $ExtraArguments
$logs = @()
foreach ($round in 1..$Rounds) {
    foreach ($name in $exes.Keys) {
        $tag = "ab-$name-r$round"
        $user = "$Out/$tag/user"
        Remove-Item -Recurse -Force "$Out/$tag" -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force $user | Out-Null
        "=== $tag $(Get-Date -Format HH:mm:ss)"
        # session.ps1 throws (and closes the game) if the native renderer falls back.
        & ./scripts/session.ps1 start -Name $tag -UserData $user -Executable $exes[$name] -ExtraArguments $arguments | Out-Null
        & ./scripts/session.ps1 input -Name $tag $route | Out-Null
        $t = [Diagnostics.Stopwatch]::StartNew()
        while ($t.Elapsed.TotalSeconds -lt $Seconds) {
            Start-Sleep -Seconds 5
            if (!(Get-Process wweallstars -ErrorAction SilentlyContinue)) { throw "$tag exited by itself (closed by hand?)" }
        }
        & ./scripts/session.ps1 stop -Name $tag | Out-Null
        Start-Sleep -Seconds 5
        if (Get-Process wweallstars -ErrorAction SilentlyContinue) { throw "$tag did not exit; no more runs started" }
        $logs += "$name-r$round=analysis/session-$tag/runtime.log"
    }
}
python scripts/compare_builds.py @logs --skip $Skip --span $Span
