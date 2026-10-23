# Online play checkpoint 1c, the sync test (.scratch/online-play/issues/09-sync-test.md).
# Two copies on this PC record a Player Match with SyncSim's replay log:
#   copy A (127.0.0.2) is the owner's, on the real pad: host a Player Match (Xbox LIVE >
#     Player Match > Create Match), take a slot at once, then pick a wrestler and play;
#   copy B (127.0.0.3) is scripted: once A's match is on the server it searches, joins,
#     takes a slot, picks a wrestler and fights with random input.
#   pwsh scripts/sync-test.ps1 [-Name match1] [-Executable <exe>] [-BWrestler 1]
#     [-Server https://allstars.komplexaci.cz]   both copies through the VPS (ticket 15)
#     [-ForceRelay]                               every peer packet through the relay (16)
#     [-DropDirect]                               direct peer traffic dropped: fallback (17)
#     [-TickLog]                                  both copies log the engine tick (--wwe_tick_log)
#   pwsh scripts/sync-test.ps1 -Collect [-Name match1]   after closing both windows
# Each station writes GAME:\Station<n>.log (the game data folder); -Collect moves them to
# analysis/sync-test-<Name>/. Close the game with the window's X, not Task Manager.
param(
    [string]$Name = 'match1',
    [string]$Executable = '',
    # How many places right of the default cursor B's wrestler is (different each match).
    [int]$BWrestler = 1,
    # Minutes of random fighting queued for B.
    [int]$FightMinutes = 20,
    # Developer only: A is scripted too (hosts a default Create Match), for a dry run.
    [switch]$ScriptedHost,
    # The game server's URL; empty runs out/server on this PC. The VPS's log is read over SSH.
    [string]$Server = '',
    [string]$ServerSsh = $env:ALLSTARS_DEPLOY_TARGET,   # user@host, for the server's logs
    [switch]$ForceRelay,
    [switch]$DropDirect,
    [switch]$TickLog,
    [switch]$Collect
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$dir = Join-Path $projectRoot "analysis/sync-test-$Name"
$session = "$PSScriptRoot/session.ps1"

if ($Collect) {
    # The copies hold their recordings open until they exit.
    & $session stop -Name "sync-$Name-B"
    & $session stop -Name "sync-$Name-A"
    Get-Process allstars-server -ErrorAction SilentlyContinue | Stop-Process
    foreach ($station in Get-ChildItem "$projectRoot/assets" -Filter 'Station*.log') {
        Move-Item $station.FullName (Join-Path $dir $station.Name) -Force
        "moved $($station.Name) to $dir"
    }
    return
}

New-Item -ItemType Directory -Force $dir | Out-Null
if (Get-ChildItem "$projectRoot/assets" -Filter 'Station*.log') {
    throw "assets/ still has Station*.log from an earlier run: collect them first (-Collect -Name <that run>)."
}
if (!$Server) {
    foreach ($needed in "$projectRoot/out/server/allstars-server.exe", "$projectRoot/out/server/allstars-server.json") {
        if (!(Test-Path $needed)) { throw "Missing ${needed}: run build.ps1 -Action Server, then allstars-server init <game>/Game/Config/Xenon/Cooked/Coalesced.ini in out/server." }
    }
}
$exe = if ($Executable) { (Resolve-Path $Executable).Path } else { "$projectRoot/out/build/win-amd64-relwithdebinfo-vulkan/wweallstars.exe" }

# Throwaway profiles, so the owner's saves stay out of the test.
foreach ($copy in 'A', 'B') {
    $ud = Join-Path $dir "userdata-$copy"
    if (!(Test-Path $ud)) { Copy-Item -Recurse "$projectRoot/userdata/vulkan" $ud }
}

# The game server.
$serverLog = Join-Path $dir 'server.log'
if ($Server) {
    $since = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ', [Globalization.CultureInfo]::InvariantCulture)
} else {
    Get-Process allstars-server -ErrorAction SilentlyContinue | Stop-Process
    Start-Process "$projectRoot/out/server/allstars-server.exe" -ArgumentList 'serve' -WorkingDirectory "$projectRoot/out/server" `
        -RedirectStandardError $serverLog -RedirectStandardOutput (Join-Path $dir 'server.out') -WindowStyle Hidden
    Start-Sleep -Seconds 1
}
# How many server log lines since this run match the pattern.
function Get-ServerLogCount([string]$Pattern) {
    if ($Server) {
        $lines = @(ssh -o BatchMode=yes -o ConnectTimeout=15 $ServerSsh "cd /opt/allstars && docker compose logs --no-log-prefix --since $since" 2>$null)
        Set-Content -LiteralPath $serverLog -Value $lines
    }
    if (!(Test-Path -LiteralPath $serverLog)) { return 0 }
    @(Select-String -LiteralPath $serverLog -Pattern $Pattern).Count
}

# The game's default server is the owner's: a local run names the loopback one.
$record = @("--wwe_online_server=$(if ($Server) { $Server } else { '127.0.0.1' })") + @(if ($ForceRelay) { '--wwe_online_force_relay=true' }) +
    @(if ($DropDirect) { '--wwe_online_drop_direct=true' }) + @(if ($TickLog) { '--wwe_tick_log=true' }) + @('--wwe_online=true', '--cl=-NETZRECORD', '--allow_game_relative_writes=true', '--wwe_net_trace=true')
$menu = @('wait 8000', 'press START', 'wait 5000', 'press START', 'wait 5000', 'press A', 'wait 6000', 'press DOWN',
    'wait 1000', 'press A', 'wait 12000', 'press A', 'wait 5000')

# Copy A.
if ($ScriptedHost) {
    & $session start -Name "sync-$Name-A" -Executable $exe -UserData (Join-Path $dir 'userdata-A') `
        -ExtraArguments (@('--wwe_online_ip=127.0.0.2', '--wwe_online_name=Alpha') + $record)
    & $session input -Name "sync-$Name-A" @($menu + @('press DOWN', 'wait 1000', 'press A', 'wait 6000', 'press A', 'wait 10000', 'press A', 'wait 3000'))
} else {
    $arguments = @('--gpu_plugin=xenos', '--vulkan_device=-1', "--game_data_root=$projectRoot/assets", "--update_data_root=$projectRoot/updatedata",
        "--user_data_root=$(Join-Path $dir 'userdata-A')", "--cache_root=$projectRoot/cache/vulkan",
        "--log_file=$(Join-Path $dir 'A.log')", '--fullscreen=false', '--wwe_online_ip=127.0.0.2',
        '--wwe_online_name=Owner') + $record
    Start-Process $exe -WorkingDirectory (Split-Path $exe -Parent) -ArgumentList ($arguments | ForEach-Object { '"' + $_ + '"' }) | Out-Null
    'Copy A is starting. Host a Player Match: Xbox LIVE > PLAYER MATCH > CREATE MATCH, pick the'
    'arena, accept, and take your slot (A) right away, before copy B arrives.'
}

# Copy B waits for A's match.
'Waiting for A to create its match...'
while ((Get-ServerLogCount 'session .* created by') -lt 1) { Start-Sleep -Seconds 2 }
& $session start -Name "sync-$Name-B" -Executable $exe -UserData (Join-Path $dir 'userdata-B') `
    -ExtraArguments (@('--wwe_online_ip=127.0.0.3', '--wwe_online_name=Bot') + $record)
$pick = @('press A', 'wait 4000') + (@('press RIGHT', 'wait 600') * $BWrestler) + @('press A', 'wait 3000', 'press A', 'wait 3000')
& $session input -Name "sync-$Name-B" @($menu + @('press DOWN', 'wait 800', 'press DOWN', 'wait 1000', 'press A', 'wait 10000',
    'press A', 'wait 15000') + $pick)
if ($ScriptedHost) {
    # The host's own join is the first; B's is the second.
    while ((Get-ServerLogCount 'session .*: .* joined') -lt 2) { Start-Sleep -Seconds 2 }
    & $session input -Name "sync-$Name-A" @('wait 20000', 'press A', 'wait 3000', 'press A', 'wait 3000', 'press A')
} else {
    'Copy B is joining (about a minute). When character select opens, pick your wrestler and play the match to the end.'
}

# Random fighting for B, seeded from the match name so a rerun queues the same input
# (string.GetHashCode is randomized per process).
$seed = [BitConverter]::ToInt32([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Name)), 0)
$random = [Random]::new($seed)
# No B, BACK or START: they leave the lobby or pause.
$moves = @('press X', 'press A', 'press Y', 'press RB', 'press LB', 'press X', 'press A')
$fight = @('wait 8000')
$end = 0
while ($end -lt $FightMinutes * 60000) {
    $x = $random.Next(-32768, 32768); $y = $random.Next(-32768, 32768); $t = $random.Next(200, 1200)
    $fight += "stick L $x $y $t"
    $fight += $moves[$random.Next($moves.Count)]
    $wait = $random.Next(100, 700)
    $fight += "wait $wait"
    $end += $t + $wait + 120
}
# Fight only once the match runs (SyncSim up), so nothing is pressed in the lobby meanwhile.
'Waiting for the match to start...'
$bLog = Join-Path $projectRoot "analysis/session-sync-$Name-B/runtime.log"
while (!(Select-String -Path $bLog -Pattern 'SyncSimBringUp' -Quiet -ErrorAction SilentlyContinue)) { Start-Sleep -Seconds 2 }
& $session input -Name "sync-$Name-B" $fight | Out-Null
"When the match is over, close copy A with its window's X, then run: pwsh scripts/sync-test.ps1 -Collect -Name $Name"
