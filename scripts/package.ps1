# Build the portable release zip in one command (spec, "Packaging"; ADR-0014, ADR-0018):
# builds the game (RelWithDebInfo, Vulkan), the launcher and XenosRecomp, stages
#   WWEAllStarsLauncher.exe + its native DLLs, game/, xenosrecomp/, README.txt, licenses/
# under out/release/<name>/, fails if any game-derived file is staged (Launcher.ReleaseCheck),
# then writes out/release/<name>.zip and <name>.zip.sha256. Symbols stay local in
# out/release/<name>-symbols/. v1 is unsigned. No game data enters the zip: players set up
# from their own disc.
param(
    [ValidateRange(1, 64)][int]$Jobs = 4,
    [string]$VisualStudioRoot = 'C:/Program Files (x86)/Microsoft Visual Studio/18/BuildTools',
    # Reuse the existing builds (after a manual build) instead of building again.
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Invoke-Hidden.ps1"
. "$PSScriptRoot/Use-PinnedDotnet.ps1"
$projectRoot = Split-Path $PSScriptRoot -Parent
# cmake/no_game_art.cmake's rule, here too so -SkipBuild cannot package an exe built with game art.
if (Test-Path -LiteralPath "$projectRoot/metadata/icons") {
    throw "$projectRoot/metadata/icons exists: it holds game art the build embeds in wweallstars.exe. Delete it and rebuild."
}

if (!$SkipBuild) {
    & "$PSScriptRoot/build.ps1" -Action Configure -VisualStudioRoot $VisualStudioRoot
    & "$PSScriptRoot/build.ps1" -Action Build -Jobs $Jobs -VisualStudioRoot $VisualStudioRoot
    & "$PSScriptRoot/build.ps1" -Action Launcher -Jobs $Jobs
    & "$PSScriptRoot/build-xenosrecomp.ps1" -Jobs $Jobs -VisualStudioRoot $VisualStudioRoot
}
Set-Location $projectRoot
$gameBuild = "$projectRoot/out/build/win-amd64-relwithdebinfo-vulkan"
$launcherBuild = "$projectRoot/out/launcher/publish"
$xenosRecompBuild = "$projectRoot/out/build/xenosrecomp/XenosRecomp"
# RelWithDebInfo runtime DLLs carry the SDK's "rd" suffix.
$gameFiles = @('wweallstars.exe', 'rexruntimerd.dll', 'rexgpu-xenosrd.dll', 'TracyClientrd.dll', 'pipeline-keys.txt')
$launcherFiles = @('WWEAllStarsLauncher.exe', 'libSkiaSharp.dll', 'libHarfBuzzSharp.dll', 'av_libglesv2.dll')
$xenosRecompFiles = @('XenosRecomp.exe', 'dxcompiler.dll', 'fmt.dll')
foreach ($path in @($gameFiles | ForEach-Object { "$gameBuild/$_" }) + @($launcherFiles | ForEach-Object { "$launcherBuild/$_" }) +
    @($xenosRecompFiles | ForEach-Object { "$xenosRecompBuild/$_" }) + "$projectRoot/.tools/xenosrecomp/XenosRecomp/shader_common.h") {
    if (!(Test-Path -LiteralPath $path)) { throw "Missing $path. Run without -SkipBuild." }
}

$version = ([xml](Get-Content "$projectRoot/launcher/Directory.Build.props" -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$git = (Get-Command git).Source
$commit = (Invoke-Hidden $git @('-C', $projectRoot, 'rev-parse', '--short', 'HEAD') | Out-String).Trim()
# A file that differs from HEAD only in line endings (core.autocrlf) is not a change.
$dirty = (Invoke-Hidden $git @('-C', $projectRoot, 'diff', 'HEAD', '--ignore-cr-at-eol') | Out-String).Trim()
$name = "WWEAllStarsRecomp-$version-$commit$(if ($dirty) { '-dirty' })-win-x64"
$releaseRoot = "$projectRoot/out/release"
$stage = "$releaseRoot/$name"
$symbols = "$releaseRoot/$name-symbols"
foreach ($path in $stage, $symbols, "$stage.zip", "$stage.zip.sha256") {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}
New-Item -ItemType Directory -Force "$stage/game", "$stage/xenosrecomp", "$stage/licenses", $symbols | Out-Null

# Never copy the build folder's wweallstars.toml: it holds this machine's paths and settings.
# (The PC SETTINGS file lives in the user data folder, not here.)
foreach ($file in $gameFiles) { Copy-Item -LiteralPath "$gameBuild/$file" -Destination "$stage/game" }
foreach ($file in $launcherFiles) { Copy-Item -LiteralPath "$launcherBuild/$file" -Destination $stage }
# The launcher runs XenosRecomp from xenosrecomp/ beside it, with shader_common.h in the same folder.
foreach ($file in $xenosRecompFiles) { Copy-Item -LiteralPath "$xenosRecompBuild/$file" -Destination "$stage/xenosrecomp" }
Copy-Item -LiteralPath "$projectRoot/.tools/xenosrecomp/XenosRecomp/shader_common.h" -Destination "$stage/xenosrecomp"

# App-local Visual C++ runtime for the game and XenosRecomp, so players need no installer.
$redist = Get-ChildItem "$VisualStudioRoot/VC/Redist/MSVC" -Directory |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } | Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Get-ChildItem "$($_.FullName)/x64" -Directory -Filter 'Microsoft.VC*.CRT' } | Select-Object -First 1
if (!$redist) { throw 'Visual C++ redistributable files not found under the Build Tools installation.' }
foreach ($folder in "$stage/game", "$stage/xenosrecomp") {
    foreach ($file in @('msvcp140.dll', 'msvcp140_1.dll', 'msvcp140_2.dll', 'msvcp140_atomic_wait.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')) {
        Copy-Item -LiteralPath "$($redist.FullName)/$file" -Destination $folder
    }
}

Copy-Item -LiteralPath "$projectRoot/launcher/release/README.txt" -Destination "$stage/README.txt"
$licenses = "$stage/licenses"
Copy-Item -LiteralPath "$projectRoot/LICENSE" -Destination "$licenses/WWEAllStarsRecomp-LICENSE.txt"
Copy-Item -LiteralPath "$projectRoot/launcher/release/RELEASE-NOTICES.txt" -Destination $licenses
Copy-Item -LiteralPath "$projectRoot/launcher/THIRD-PARTY-NOTICES.txt" -Destination "$licenses/launcher-THIRD-PARTY-NOTICES.txt"
Copy-Item -LiteralPath "$projectRoot/third_party/rexglue-LICENSE.txt" -Destination $licenses
$fidelityfx = "$projectRoot/.tools/fidelityfx-sdk"
# build.ps1 compiles the CAS and FSR 1 filters in whenever these headers exist.
if (Test-Path "$fidelityfx/sdk/include") { Copy-Item -LiteralPath "$fidelityfx/LICENSE.txt" -Destination "$licenses/FidelityFX-SDK-LICENSE.txt" }
Copy-Item -LiteralPath "$projectRoot/.tools/xenosrecomp/LICENSE.md" -Destination "$licenses/XenosRecomp-LICENSE.txt"
# Every licence file of the libraries the runtime and XenosRecomp build in, as .txt.
foreach ($component in @(
        @{ Prefix = 'rexglue'; Root = "$projectRoot/.tools/rexglue-source/thirdparty" },
        @{ Prefix = 'XenosRecomp'; Root = "$projectRoot/.tools/xenosrecomp/thirdparty" })) {
    foreach ($directory in Get-ChildItem $component.Root -Directory) {
        foreach ($license in Get-ChildItem $directory.FullName -File | Where-Object { $_.Name -match '^(LICEN[CS]E|COPYING)(\.[A-Za-z0-9.]+)?$' }) {
            $base = [IO.Path]::GetFileNameWithoutExtension($license.Name)
            $suffix = if ($license.Extension -match '^\.(txt|md)$' -or !$license.Extension) { '' } else { $license.Extension }
            Copy-Item -LiteralPath $license.FullName -Destination "$licenses/$($component.Prefix)-$($directory.Name)-$base$suffix.txt"
        }
    }
}

foreach ($file in $gameFiles) {
    $pdb = [IO.Path]::ChangeExtension("$gameBuild/$file", '.pdb')
    if (Test-Path -LiteralPath $pdb) { Copy-Item -LiteralPath $pdb -Destination $symbols }
}

# The packaging check: no game-derived file staged, every file a player needs present.
$dotnet = Use-PinnedDotnet $projectRoot
& $dotnet run --project "$projectRoot/launcher/tools/Launcher.ReleaseCheck/Launcher.ReleaseCheck.csproj" -c Release -- $stage
if ($LASTEXITCODE -ne 0) { throw "The staged release failed the packaging check (exit $LASTEXITCODE): $stage" }

$zip = "$stage.zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory((Resolve-Path $stage).Path, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
# sha256sum's format, so `sha256sum -c` and Get-FileHash both check it.
[IO.File]::WriteAllText("$zip.sha256", "$hash *$name.zip`n")
Write-Output "Release zip: $zip ($([math]::Round((Get-Item $zip).Length / 1MB)) MB)"
Write-Output "SHA-256: $hash"
Write-Output "Symbols: $symbols"
