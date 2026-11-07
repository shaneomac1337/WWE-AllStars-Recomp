function Sync-VulkanRuntime([string]$SourceDirectory, [string]$DestinationDirectory, [string]$Configuration = 'RelWithDebInfo') {
    # RelWithDebInfo builds carry an 'rd' suffix; Release builds do not. Tracy only when built with it.
    $names = if ($Configuration -eq 'Release') { @('rexruntime.dll', 'rexgpu-xenos.dll') }
        else { @('rexruntimerd.dll', 'rexgpu-xenosrd.dll') }
    $copies = @()
    foreach ($name in $names) {
        $source = Join-Path $SourceDirectory $name
        $destination = Join-Path $DestinationDirectory $name
        if (!(Test-Path -LiteralPath $source)) { throw "Missing built runtime: $source" }
        $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        if (!(Test-Path -LiteralPath $destination) -or (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $hash) {
            $copies += [pscustomobject]@{Source=$source;Destination=$destination;Hash=$hash}
        }
    }
    if ($copies.Count) {
        $gamePath = [IO.Path]::GetFullPath((Join-Path $DestinationDirectory 'wweallstars.exe'))
        if (Get-Process wweallstars -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $gamePath }) {
            throw 'Runtime built but not deployed: close the game normally, then rerun the build.'
        }
        foreach ($copy in $copies) {
            Copy-Item -LiteralPath $copy.Source -Destination $copy.Destination
            if ((Get-FileHash -LiteralPath $copy.Destination -Algorithm SHA256).Hash -ne $copy.Hash) {
                throw "Runtime copy verification failed: $($copy.Destination)"
            }
        }
    }
    # Keep symbols beside their matching DLLs for reliable local diagnostics.
    foreach ($name in $names) {
        $symbol = [IO.Path]::ChangeExtension($name, '.pdb')
        $source = Join-Path $SourceDirectory $symbol
        if (Test-Path -LiteralPath $source) {
            $destination = Join-Path $DestinationDirectory $symbol
            if (!(Test-Path -LiteralPath $destination) -or (Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) {
                Copy-Item -LiteralPath $source -Destination $destination
            }
        }
    }
    Write-Output "Vulkan runtime verified ($($copies.Count) DLLs updated)."
}
