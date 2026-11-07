# Whether Windows Defender leaves a release alone once it has really run. An on-demand MpCmdRun
# scan is not enough: Defender passed a converter that way, then removed it about a minute after
# its first real run, and a file can also pass until something next reads it. This unpacks the zip,
# prepares the game's shaders with it, reads every exe and DLL every 15 s while it watches, and
# exits 1 when Defender flagged or removed one, or 2 when the shader step did not really run. A pass
# only says this build has no verdict yet. It never changes Defender's settings.
param(
    [Parameter(Mandatory)][string]$Zip,
    [ValidateRange(30, 3600)][int]$WaitSeconds = 600,
    # A full pack of the game's shaders is about 1.7 MB; a smaller one means the conversion stopped short.
    [long]$MinimumPackBytes = 1500000
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$work = "$projectRoot/out/av-test/zip-$(Get-Date -Format yyyyMMdd-HHmmss)"
$start = Get-Date
Expand-Archive -LiteralPath $Zip -DestinationPath $work
$root = (Get-ChildItem -LiteralPath $work -Directory | Select-Object -First 1).FullName
$files = Get-ChildItem -LiteralPath $root -Recurse -Include *.exe, *.dll
Write-Output "$($files.Count) exes and DLLs in $root"
if (Test-Path -LiteralPath "$root/xenosrecomp/XenosRecomp.exe") { Write-Output 'FAILED: the release still ships XenosRecomp.exe'; exit 1 }

function Test-Flagged {
    $detection = Get-MpThreatDetection -ErrorAction SilentlyContinue |
        Where-Object { $_.InitialDetectionTime -ge $start -and ($_.Resources -join ';') -like "*$(Split-Path $work -Leaf)*" }
    if ($detection) { return "$((Get-MpThreat -ThreatID @($detection)[0].ThreatID).ThreatName): $(@($detection)[0].Resources -join ';')" }
    foreach ($file in $files) {
        if (!(Test-Path -LiteralPath $file.FullName)) { return "$($file.Name) removed" }
        try { $null = Get-FileHash -LiteralPath $file.FullName } catch { return "$($file.Name) read refused: $($_.Exception.Message)" }
    }
    return $null
}

$output = & "$root/WWEAllStarsLauncher.exe" --root $root --game-data "$projectRoot/assets" --prepare-shaders 2>&1 | Select-Object -Last 1
Write-Output "prepare after $([int]((Get-Date) - $start).TotalSeconds) s: $output"
$pack = Get-ChildItem "$root/cache" -Recurse -Filter *.shaderpack -ErrorAction SilentlyContinue | Select-Object -First 1
if (!$pack -or $pack.Length -lt $MinimumPackBytes) {
    Write-Output "INVALID: shader pack is $(if ($pack) { $pack.Length } else { 'missing' }), the conversion did not really run; flagged: $(Test-Flagged)"
    exit 2
}
Write-Output "shader pack $($pack.Length) bytes, sha256 $((Get-FileHash -LiteralPath $pack.FullName).Hash)"
$flagged = Test-Flagged
$finish = $start.AddSeconds($WaitSeconds)
while (!$flagged -and (Get-Date) -lt $finish) {
    Start-Sleep 15
    $flagged = Test-Flagged
}
$seconds = [int]((Get-Date) - $start).TotalSeconds
if ($flagged) {
    Write-Output "FLAGGED after $seconds s: $flagged"
    exit 1
}
Write-Output "not flagged after $seconds s"
