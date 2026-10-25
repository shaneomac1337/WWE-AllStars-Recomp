# The repo-local .NET SDK, pinned by launcher/global.json (rollForward disabled).
# Sets the environment for it and returns dotnet.exe; throws when it is missing or not the pin.
function Use-PinnedDotnet([string]$ProjectRoot) {
    $dotnet = "$ProjectRoot/.tools/dotnet-sdk/dotnet.exe"
    if (!(Test-Path -LiteralPath $dotnet)) {
        # A git worktree has no .tools of its own: use the main checkout's.
        $commonDir = git -C $ProjectRoot rev-parse --path-format=absolute --git-common-dir 2>$null
        if ($LASTEXITCODE -eq 0 -and $commonDir) { $dotnet = "$(Split-Path $commonDir -Parent)/.tools/dotnet-sdk/dotnet.exe" }
    }
    $pinnedSdk = (Get-Content "$ProjectRoot/launcher/global.json" -Raw | ConvertFrom-Json).sdk.version
    if (!(Test-Path -LiteralPath $dotnet)) { throw "Missing build prerequisite: $dotnet (run scripts/bootstrap.ps1)" }
    $env:DOTNET_ROOT = Split-Path $dotnet -Parent
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_CLI_UI_LANGUAGE = 'en'
    # From launcher/, so the SDK resolves through global.json and fails when the pin is absent.
    Push-Location "$ProjectRoot/launcher"
    try { $sdkVersion = (& $dotnet --version 2>&1 | Out-String).Trim() } finally { Pop-Location }
    if ($sdkVersion -ne $pinnedSdk) { throw "Unexpected .NET SDK: '$sdkVersion' (launcher/global.json pins $pinnedSdk)" }
    $dotnet
}
