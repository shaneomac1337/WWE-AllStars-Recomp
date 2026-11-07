# Convert the shader containers captured with --wwe_shader_dump_dir into the
# native renderer's shader pack, which the game loads at startup from its cache
# folder (default: cache/vulkan, the cache_root the dev scripts pass). Every
# folder and file here holds game-derived data and stays local.
param([string]$InputDirectory = 'cache/guest-shaders',
      [string]$OutputPath = 'cache/vulkan/wweallstars.shaderpack')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Set-Location $projectRoot
# XenosRecomp lives inside the game exe (ADR-0029); it finds dxcompiler.dll beside itself.
$tool = 'out/build/win-amd64-relwithdebinfo-vulkan/wweallstars.exe'
if (!(Test-Path $tool)) { throw "Build the game first: $tool" }
if (!(Get-ChildItem $InputDirectory -Filter '*.bin' -ErrorAction SilentlyContinue)) {
    throw "No shader containers in $InputDirectory. Run the game once with --wwe_shader_dump_dir=$InputDirectory."
}
New-Item -ItemType Directory -Force cache/shader-native | Out-Null
# A GUI exe, so PowerShell would not wait for it or see its exit code otherwise.
$run = Start-Process -FilePath $tool -NoNewWindow -Wait -PassThru -RedirectStandardOutput cache/shader-native/run.log -RedirectStandardError cache/shader-native/run-errors.log -ArgumentList @(
    '--wwe_convert_shaders', "`"$InputDirectory`"", 'cache/shader-native/shader_cache.cpp', '.tools/xenosrecomp/XenosRecomp/shader_common.h', 'cache/shader-native/hlsl')
if ($run.ExitCode -ne 0) { throw "XenosRecomp failed; see cache/shader-native/run.log and run-errors.log" }
# The XenosRecomp patch the shaders were converted with; a build configured
# with another patch treats the pack as stale and runs on Emulated.
$converterId = (Get-FileHash -Algorithm SHA256 patches/xenosrecomp-wweallstars.patch).Hash.Substring(0, 16).ToLower()
$outputDirectory = Split-Path $OutputPath -Parent
if ($outputDirectory) { New-Item -ItemType Directory -Force $outputDirectory | Out-Null }
# The pack also holds the texture slots each shader reads, so draws bind only those.
& python "$PSScriptRoot/shader_pack.py" cache/shader-native/shader_cache.cpp cache/shader-native/hlsl $converterId $OutputPath
if ($LASTEXITCODE -ne 0) { throw "shader_pack.py failed" }
Write-Output "Shader pack written: $OutputPath"
