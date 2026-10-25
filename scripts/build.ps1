param(
    # Launcher publishes the Avalonia launcher (Native AOT) to out/launcher/publish;
    # LauncherTest runs its xUnit tests. Neither needs game data.
    # Server builds the game server (server/) to out/server; ServerTest runs its Go tests.
    # Image writes the TU2 guest image (base v5 + default.xexp) to analysis/xex/tu2.pe for the
    # analysis scripts.
    [ValidateSet('Configure', 'Build', 'Codegen', 'Test', 'Image', 'Launcher', 'LauncherTest', 'Server', 'ServerTest')][string]$Action = 'Build',
    [ValidateSet('D3D12', 'Vulkan')][string]$Renderer = 'Vulkan',
    [string]$VisualStudioRoot = 'C:/Program Files (x86)/Microsoft Visual Studio/18/BuildTools',
    [string]$WindowsSdkRoot = 'C:/Program Files (x86)/Windows Kits/10',
    [ValidateRange(1, 64)][int]$Jobs = 4,
    # Release drops the profiler and debug-only checks; RelWithDebInfo is what ships today.
    [ValidateSet('RelWithDebInfo', 'Release')][string]$Configuration = 'RelWithDebInfo',
    # Extra CMake cache entries for Configure, e.g. -CMakeArgs '-DWWE_D3D_SWEEP=ON'.
    [string[]]$CMakeArgs = @(),
    # A build-flag variant in its own build directory (<preset>-<Variant>[-vulkan]), e.g.
    # -Variant v3o2 -CMakeArgs '-DWWE_GAME_ARCH=x86-64-v3' (ticket 11).
    [ValidatePattern('^[a-z0-9]*$')][string]$Variant = ''
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Invoke-Hidden.ps1"
$projectRoot = Split-Path $PSScriptRoot -Parent
Set-Location $projectRoot
New-Item -ItemType Directory -Force analysis | Out-Null
if ($Action -in 'Launcher', 'LauncherTest') {
    . "$PSScriptRoot/Use-PinnedDotnet.ps1"
    $dotnet = Use-PinnedDotnet $projectRoot
    if ($Action -eq 'Launcher') {
        # Native AOT links with MSVC, which it finds through vswhere (not on PATH by default).
        $vswhere = "${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer"
        if (!(Test-Path -LiteralPath "$vswhere/vswhere.exe")) { throw "Missing build prerequisite: $vswhere/vswhere.exe" }
        $env:PATH = "$vswhere;$env:PATH"
        Invoke-Hidden $dotnet @('publish', 'src/Launcher.App/Launcher.App.csproj', '-c', 'Release', '-r', 'win-x64', '-p:DebugType=none',
            '-o', "$projectRoot/out/launcher/publish", "-maxcpucount:$Jobs") `
            -WorkingDirectory "$projectRoot/launcher" -LogPath "$projectRoot/analysis/build-launcher.log"
        Write-Output "Launcher: $projectRoot/out/launcher/publish/WWEAllStarsLauncher.exe"
    } else {
        # Opt-in real-data tests run whenever the disc is extracted (in a worktree, the main checkout's assets/).
        $assets = "$projectRoot/assets"
        if (!(Test-Path -LiteralPath $assets)) { $assets = "$($dotnet -replace '/\.tools/dotnet-sdk/dotnet\.exe$', '')/assets" }
        if ((Test-Path -LiteralPath "$assets/Game/CookedXenon") -and !$env:WWE_TEST_GAME_DATA) { $env:WWE_TEST_GAME_DATA = $assets }
        Invoke-Hidden $dotnet @('test', 'tests/Launcher.Tests/Launcher.Tests.csproj', '-c', 'Release', "-maxcpucount:$Jobs") `
            -WorkingDirectory "$projectRoot/launcher" -LogPath "$projectRoot/analysis/test-launcher.log"
        Get-Content "$projectRoot/analysis/test-launcher.log" | Select-String -Pattern '^(Passed|Failed)!' | ForEach-Object { $_.Line }
    }
    Write-Output "$Action completed. Logs: $projectRoot/analysis/"
    return
}
if ($Action -in 'Server', 'ServerTest') {
    # The pinned Go from scripts/bootstrap.ps1; server/go.mod names the same version.
    $go = "$projectRoot/.tools/go/bin/go.exe"
    if (!(Test-Path -LiteralPath $go)) { throw "Missing build prerequisite: $go (run scripts/bootstrap.ps1)" }
    $env:GOTOOLCHAIN = 'local'
    $goArguments = if ($Action -eq 'Server') {
        @('build', '-trimpath', '-o', "$projectRoot/out/server/allstars-server.exe", './cmd/allstars-server')
    } else { @('test', './...') }
    Invoke-Hidden $go $goArguments -WorkingDirectory "$projectRoot/server" -LogPath "$projectRoot/analysis/$($Action.ToLowerInvariant()).log"
    if ($Action -eq 'ServerTest') { Get-Content "$projectRoot/analysis/servertest.log" | Select-String -Pattern '^(ok|FAIL|---)' | ForEach-Object { $_.Line } }
    Write-Output "$Action completed. Logs: $projectRoot/analysis/"
    return
}
# The recomp is generated from base v5 plus Title Update 2 (ADR-0022). Codegen applies the
# patch because it sits next to the XEX.
$expectedXex = '521c2325d89470ff490efee257885a465360ffb64f9db2491eda6af7ab3a6892'
$expectedXexp = 'a6a4570439c1a26114199b829461e772a077638d11add2b6faeff31acb1ced6a'
if (!(Test-Path -LiteralPath 'assets/default.xex') -or (Get-FileHash 'assets/default.xex' -Algorithm SHA256).Hash -ne $expectedXex) {
    throw 'Unsupported executable revision. This manifest targets Title 54510866 / Media 60CEF505, version 5.'
}
if (!(Test-Path -LiteralPath 'assets/default.xexp')) {
    throw 'Missing assets/default.xexp. The build needs Title Update 2: copy default.xexp from your own TU2 package (tu00000005_00000000) next to assets/default.xex.'
}
if ((Get-FileHash 'assets/default.xexp' -Algorithm SHA256).Hash -ne $expectedXexp) {
    throw 'assets/default.xexp is not Title Update 2 for this disc (SHA-256 mismatch). Only TU2 (version 0x205, a6a45704...ed6a) is supported.'
}
$sdk = "$projectRoot/.tools/rexglue-0.10.0/win-amd64"
$llvm = "$projectRoot/.tools/llvm-21.1.8/bin"
$cmakeRoot = "$VisualStudioRoot/Common7/IDE/CommonExtensions/Microsoft/CMake"
$cmake = "$cmakeRoot/CMake/bin/cmake.exe"
$msvc = Get-ChildItem "$VisualStudioRoot/VC/Tools/MSVC" -Directory |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$windowsVersion = Get-ChildItem "$WindowsSdkRoot/Include" -Directory |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
foreach ($path in @("$sdk/bin/rexglue.exe", "$llvm/clang++.exe", $cmake,
    "$cmakeRoot/Ninja/ninja.exe", "$($msvc.FullName)/include/vector",
    "$WindowsSdkRoot/Include/$($windowsVersion.Name)/um/Windows.h")) {
    if (!(Test-Path -LiteralPath $path)) { throw "Missing build prerequisite: $path" }
}
$env:PATH = "$llvm;$cmakeRoot/Ninja;$($msvc.FullName)/bin/Hostx64/x64;$WindowsSdkRoot/bin/$($windowsVersion.Name)/x64;$env:PATH"
$env:INCLUDE = "$($msvc.FullName)/include;$WindowsSdkRoot/Include/$($windowsVersion.Name)/ucrt;$WindowsSdkRoot/Include/$($windowsVersion.Name)/shared;$WindowsSdkRoot/Include/$($windowsVersion.Name)/um;$WindowsSdkRoot/Include/$($windowsVersion.Name)/winrt"
$env:LIB = "$($msvc.FullName)/lib/x64;$WindowsSdkRoot/Lib/$($windowsVersion.Name)/ucrt/x64;$WindowsSdkRoot/Lib/$($windowsVersion.Name)/um/x64"
$preset = "win-amd64-$($Configuration.ToLowerInvariant())"
$buildDirectory = "$projectRoot/out/build/$preset"
if ($Variant) { $buildDirectory += "-$Variant" }
$configureArguments = @('--preset', $preset, "-DCMAKE_PREFIX_PATH=$sdk", '-DREXSDK_VERSION=0.10.0') + $CMakeArgs
$logSuffix = ''
if ($Renderer -eq 'Vulkan') {
    $source = "$projectRoot/.tools/rexglue-source"
    if (!(Test-Path "$source/CMakeLists.txt")) { throw 'Run scripts/prepare-vulkan.ps1 first.' }
    $revision = (Invoke-Hidden (Get-Command git).Source @('-C', $source, 'rev-parse', 'HEAD') | Out-String).Trim()
    if ($revision -ne 'f5337cdc947ff6d4c4196737e2c807a48f2a1fc2') { throw "Unexpected ReXGlue source revision: $revision" }
    & "$PSScriptRoot/apply-vulkan-patches.ps1"
    $buildDirectory += '-vulkan'
    $configureArguments += @('-B', $buildDirectory, "-DREXSDK_DIR=$source", '-DREXGLUE_USE_VULKAN=ON', '-DREXGLUE_USE_D3D12=OFF')
    $configureArguments += "-DPYTHON_EXECUTABLE=$((Get-Command python.exe -ErrorAction Stop).Source)"
    # Match the SDK's Windows x64 preset when compiling it from source.
    $configureArguments += @('-DCMAKE_C_FLAGS=-march=x86-64-v2', '-DCMAKE_CXX_FLAGS=-march=x86-64-v2')
    # FidelityFX headers enable the runtime's CAS sharpening and FSR 1 upscaling output filters.
    $fidelityfx = "$projectRoot/.tools/fidelityfx-sdk"
    if (Test-Path "$fidelityfx/sdk/include") { $configureArguments += "-DREXGLUE_FIDELITYFX_SOURCE_DIR=$fidelityfx" }
    # libcurl, rapidjson and miniupnpc for the online client (patches/rexglue-online.patch), from scripts/bootstrap.ps1.
    $configureArguments += "-DREXGLUE_CURL_SOURCE_DIR=$projectRoot/.tools/curl-a05f34973e6c4bb629d018f7cb51487be1c904d8"
    $configureArguments += "-DREXGLUE_RAPIDJSON_SOURCE_DIR=$projectRoot/.tools/rapidjson-7c73dd7de7c4f14379b781418c6e947ad464c818"
    $configureArguments += "-DREXGLUE_MINIUPNPC_SOURCE_DIR=$projectRoot/.tools/miniupnp-b5e5d2eb069822b7f00d56c8e61033b9d500e60c/miniupnpc"
    $logSuffix = '-vulkan'
}
if ($Action -eq 'Image') {
    # tools/xexdump links the prebuilt SDK and loads the XEX the way codegen does.
    $toolDirectory = "$projectRoot/out/build/xexdump"
    Invoke-Hidden $cmake @('-S', "$projectRoot/tools/xexdump", '-B', $toolDirectory, '-G', 'Ninja', '-DCMAKE_BUILD_TYPE=Release',
        '-DCMAKE_C_COMPILER=clang', '-DCMAKE_CXX_COMPILER=clang++', "-DCMAKE_PREFIX_PATH=$sdk", '-DREXSDK_VERSION=0.10.0') `
        -LogPath "$projectRoot/analysis/configure-xexdump.log"
    Invoke-Hidden $cmake @('--build', $toolDirectory) -LogPath "$projectRoot/analysis/build-xexdump.log"
    Copy-Item "$sdk/bin/*.dll" $toolDirectory
    New-Item -ItemType Directory -Force "$projectRoot/analysis/xex" | Out-Null
    Invoke-Hidden "$toolDirectory/xexdump.exe" @("$projectRoot/assets", "$projectRoot/analysis/xex/tu2.pe") -LogPath "$projectRoot/analysis/xexdump.log"
    Write-Output "TU2 image: $projectRoot/analysis/xex/tu2.pe"
} elseif ($Action -eq 'Codegen') {
    Invoke-Hidden "$sdk/bin/rexglue.exe" @('codegen', 'wweallstars_manifest.toml', '--ignore-stamp') -LogPath "$projectRoot/analysis/codegen-current.log"
} elseif ($Action -eq 'Configure') {
    Invoke-Hidden $cmake $configureArguments -LogPath "$projectRoot/analysis/configure$logSuffix.log"
} elseif ($Action -eq 'Test') {
    # The native renderer's offline tests (tests/native); needs the source-tree SDK.
    if ($Renderer -ne 'Vulkan') { throw 'The native renderer tests build with -Renderer Vulkan.' }
    Invoke-Hidden $cmake ($configureArguments + '-DWWE_BUILD_TESTS=ON') -LogPath "$projectRoot/analysis/configure$logSuffix.log"
    Invoke-Hidden $cmake @('--build', $buildDirectory, '--parallel', "$Jobs", '--target', 'wweallstars_native_tests') -LogPath "$projectRoot/analysis/build-tests.log"
    Push-Location $buildDirectory
    try {
        & "$buildDirectory/wweallstars_native_tests.exe"
        if ($LASTEXITCODE -ne 0) { throw "Native renderer tests failed (exit $LASTEXITCODE)" }
    } finally { Pop-Location }
} else {
    Invoke-Hidden $cmake @('--build', $buildDirectory, '--parallel', "$Jobs") -LogPath "$projectRoot/analysis/build$logSuffix.log"
    if ($Renderer -eq 'Vulkan') {
        # The SDK's POST_BUILD copy can be skipped when only a runtime DLL changes.
        . "$PSScriptRoot/Sync-VulkanRuntime.ps1"
        Sync-VulkanRuntime "$source/out/win-amd64" $buildDirectory -Configuration $Configuration
    }
    # Keys on the shipped list whose shaders are in neither the dev shader pack nor the top-ups
    # the game merges into it (a pack is game data, so only a local one is checked).
    $pack = "$projectRoot/cache/vulkan/wweallstars.shaderpack"
    if (Test-Path -LiteralPath $pack) {
        & python "$PSScriptRoot/pipeline_keys.py" check "$projectRoot/data/pipeline-keys.txt" $pack --top-ups "$projectRoot/cache/vulkan/shader-topup"
    } else {
        Write-Output "Pipeline key list not checked: no shader pack at $pack"
    }
}
Write-Output "$Action completed. Logs: $projectRoot/analysis/"
