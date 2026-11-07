# Builds the next commit of the public history (ADR-0023) on the local branch "public": the
# tree of -From (default HEAD) without the internal paths below, authored with the GitHub
# noreply address, its parent the previous public commit. The private history, the working
# tree and the index are not touched. Push it with: git push <remote> public:main
#
#   pwsh ./scripts/publish-tree.ps1 -Message "Release 1.0.0"
param(
    [Parameter(Mandatory)][string]$Message,
    [string]$From = 'HEAD'
)
$ErrorActionPreference = 'Stop'

# Kept in the private history only: the launch research (Reddit analysis of the owner's account)
# and the file hash lists of the title update and DLC.
$internal = @('docs/research/github-and-reddit-launch.md', 'docs/research/package-hashes')
$identity = 'shaneomac1337'
$email = 'shaneomac1337@users.noreply.github.com'

function Invoke-Git([string[]]$Arguments) {
    $output = & git @Arguments
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed (exit $LASTEXITCODE)" }
    $output
}

$index = [IO.Path]::GetTempFileName()
$saved = @{}
foreach ($name in 'GIT_INDEX_FILE', 'GIT_AUTHOR_NAME', 'GIT_AUTHOR_EMAIL', 'GIT_AUTHOR_DATE',
        'GIT_COMMITTER_NAME', 'GIT_COMMITTER_EMAIL', 'GIT_COMMITTER_DATE') {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name)
}
try {
    Remove-Item -LiteralPath $index
    $env:GIT_INDEX_FILE = $index
    Invoke-Git @('read-tree', $From) | Out-Null
    Invoke-Git (@('rm', '-r', '--cached', '--quiet', '--ignore-unmatch', '--') + $internal) | Out-Null
    $tree = Invoke-Git @('write-tree')

    $parent = & git rev-parse --verify --quiet refs/heads/public
    $arguments = @('commit-tree', $tree, '-m', $Message)
    if ($LASTEXITCODE -eq 0 -and $parent) {
        if ((Invoke-Git @('rev-parse', "$parent^{tree}")) -eq $tree) {
            throw "Nothing to publish: the public branch already has this tree."
        }
        $arguments += @('-p', $parent)
    }
    $env:GIT_AUTHOR_NAME = $identity
    $env:GIT_AUTHOR_EMAIL = $email
    $env:GIT_COMMITTER_NAME = $identity
    $env:GIT_COMMITTER_EMAIL = $email
    # In UTC, so the commit doesn't carry the local time zone.
    $now = [DateTimeOffset]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ss+0000")
    $env:GIT_AUTHOR_DATE = $now
    $env:GIT_COMMITTER_DATE = $now
    $commit = Invoke-Git $arguments
    Invoke-Git @('update-ref', 'refs/heads/public', $commit) | Out-Null
    Write-Output "public -> $commit"
}
finally {
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    Remove-Item -LiteralPath $index -ErrorAction SilentlyContinue
}
