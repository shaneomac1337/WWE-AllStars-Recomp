$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Invoke-Hidden.ps1"
$projectRoot = Split-Path $PSScriptRoot -Parent
$source = "$projectRoot/.tools/rexglue-source"
$git = (Get-Command git).Source
$revision = (Invoke-Hidden $git @('-C', $source, 'rev-parse', 'HEAD') | Out-String).Trim()
if ($revision -ne 'f5337cdc947ff6d4c4196737e2c807a48f2a1fc2') { throw "Unexpected SDK revision: $revision" }
# Applied in this order. Later patches may change lines earlier ones added, so
# a patch cannot be checked on its own once its successors are in; a stamp
# records the exact patch set applied to the tree instead.
$patches = @('rexglue-window-restore.patch', 'rexglue-async-pipelines.patch', 'rexglue-xmp-no-delay.patch',
    'rexglue-frame-pacing.patch', 'rexglue-resolve-readback.patch', 'rexglue-draw-census.patch',
    'rexglue-native-frame.patch', 'rexglue-native-shader-storage.patch', 'rexglue-ib-census.patch',
    'rexglue-xma-output-valid.patch', 'rexglue-xma-frame-drain.patch', 'rexglue-guest-tick-source.patch',
    'rexglue-smooth-presentation.patch', 'rexglue-in-game-settings.patch', 'rexglue-vibration.patch',
    'rexglue-device-extensions.patch', 'rexglue-present-wait.patch', 'rexglue-live-sharpening.patch',
    'rexglue-online.patch', 'rexglue-live-vsync.patch', 'rexglue-live-vblank.patch', 'rexglue-tu2-update.patch', 'rexglue-content.patch', 'rexglue-overlay-pacing.patch', 'rexglue-fullscreen-toggle.patch',
    'rexglue-guest-vblank-rate.patch', 'rexglue-gpu-loss-handler.patch', 'rexglue-net-waits.patch')
$hashes = foreach ($name in $patches) { "$name $((Get-FileHash "$projectRoot/patches/$name" -Algorithm SHA256).Hash)" }
$expected = $hashes -join "`n"
$stamp = "$source/.wwe-patches"
if ((Test-Path $stamp) -and (Get-Content $stamp -Raw).Trim() -eq $expected.Trim()) { return }
if (Test-Path $stamp) {
    throw "The SDK tree carries a different patch set ($stamp). Reset it (git -C .tools/rexglue-source checkout -- . and remove new files), then rerun."
}
foreach ($name in $patches) {
    $patch = "$projectRoot/patches/$name"
    $applied = $false
    try { $null = Invoke-Hidden $git @('-C', $source, 'apply', '--reverse', '--check', $patch); $applied = $true } catch { }
    if (!$applied) {
        $null = Invoke-Hidden $git @('-C', $source, 'apply', '--check', $patch)
        $null = Invoke-Hidden $git @('-C', $source, 'apply', $patch)
    }
}
Set-Content -Path $stamp -Value $expected -NoNewline
