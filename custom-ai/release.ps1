param(
    [Parameter(Mandatory)][ValidatePattern('^v[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$')][string]$Tag,
    [string]$GameDir = $env:SWYF_GAME_DIR,
    [switch]$BuildOnly,
    [string]$NotesFile,
    [switch]$Draft
)
$ErrorActionPreference = 'Stop'
$gitTag = "custom-ai-$Tag"
if ([string]::IsNullOrWhiteSpace($GameDir)) { throw 'Pass -GameDir or set SWYF_GAME_DIR to your game installation folder.' }
Push-Location $PSScriptRoot
$previousGameDir = $env:SWYF_GAME_DIR
try {
    foreach ($command in @('git', 'dotnet', 'node')) { Get-Command $command -ErrorAction Stop | Out-Null }
    if (Get-Process -Name 'Scam With Your Friends', 'SWYF.CustomAI.Panel' -ErrorAction SilentlyContinue) {
        throw 'Close the game and its settings panel before releasing.'
    }
    $head = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Create a commit before releasing.' }
    $status = git status --porcelain --untracked-files=all
    if ($LASTEXITCODE -ne 0) { throw 'Could not read Git status.' }
    if ($status) { throw 'Commit or otherwise account for local changes before releasing. The ZIP must match the tagged commit.' }
    if (!$NotesFile) { $NotesFile = Join-Path $PSScriptRoot "releases/$Tag.md" }
    if (!(Test-Path -LiteralPath $NotesFile -PathType Leaf)) { throw "Missing release notes: $NotesFile" }
    $version = ([xml](Get-Content Directory.Build.props -Raw)).Project.PropertyGroup.Version
    if ($Tag -ne "v$version") { throw "Tag must match Directory.Build.props Version ($version)." }
    # Reject accidental packaging of the temporary local-test companion.
    $clientSource = Get-Content src/Panel/ManifestDeXClient.cs -Raw
    if (!$clientSource.Contains('ServiceUrl="https://swyf-ai.manifestdex.com"') -or $clientSource.Contains('Membership.LocalTest') -or $clientSource.Contains('manifestdex-local-session')) { throw 'Release source does not use production membership endpoints/session isolation.' }
    if ((Get-Content src/Panel/index.html -Raw).Contains('Local test mode')) { throw 'Local test banner found in release source.' }

    if (!$BuildOnly) {
        Get-Command gh -ErrorAction Stop | Out-Null
        gh auth status
        if ($LASTEXITCODE -ne 0) { throw 'Sign in with gh auth login first.' }
        $origin = git remote get-url origin
        if ($LASTEXITCODE -ne 0) { throw 'Configure the origin GitHub remote first.' }
        $repository = gh repo view $origin --json nameWithOwner --jq .nameWithOwner
        if ($LASTEXITCODE -ne 0 -or !$repository) { throw 'Could not resolve the GitHub origin repository.' }
        # Never move an existing release tag, locally or remotely.
        git show-ref --verify --quiet "refs/tags/$gitTag"
        if ($LASTEXITCODE -eq 0) {
            $tagCommit = git rev-parse "$gitTag^{commit}"
            if ($LASTEXITCODE -ne 0 -or $tagCommit -ne $head) { throw 'The local tag points to another commit.' }
        } elseif ($LASTEXITCODE -ne 1) { throw 'Could not inspect the local tag.' }
        $remoteTag = @(git ls-remote origin "refs/tags/$gitTag" "refs/tags/$gitTag^{}")
        if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the remote tag.' }
        if ($remoteTag.Count) {
            $peeled = @($remoteTag | Where-Object { $_.EndsWith('^{}') })
            $target = if ($peeled.Count) { $peeled[0] } else { $remoteTag[0] }
            if (($target -split '\s+')[0] -ne $head) { throw 'The remote tag points to another commit.' }
        }
    }

    & ./build.ps1 -GameDir $GameDir
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    & "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File tests/prerequisites.test.ps1
    if ($LASTEXITCODE -ne 0) { throw 'Prerequisite setup tests failed; nothing was published.' }
    $env:SWYF_GAME_DIR = $GameDir
    dotnet run --project tests/Tests.csproj -c Release -- --installer-offline
    if ($LASTEXITCODE -ne 0) { throw 'Release tests failed; nothing was published.' }
    node tests/signin-ui.test.mjs
    if ($LASTEXITCODE -ne 0) { throw 'Sign-in UI tests failed; nothing was published.' }
    node tests/tts-ui.test.mjs
    if ($LASTEXITCODE -ne 0) { throw 'TTS UI tests failed; nothing was published.' }
    # Stop if files or HEAD changed while the build was running.
    $currentHead = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $currentHead -ne $head) { throw 'HEAD changed during the build.' }
    $status = git status --porcelain --untracked-files=all
    if ($LASTEXITCODE -ne 0 -or $status) { throw 'The working tree changed during the build.' }
    $asset = "SWYF-Custom-AI-$Tag-win-x64.zip"
    $path = Join-Path $PSScriptRoot "artifacts/$asset"
    Copy-Item -LiteralPath artifacts/SWYF-Custom-AI-win-x64.zip -Destination $path
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $asset" | Set-Content -LiteralPath "$path.sha256" -Encoding ascii
    if ($BuildOnly) { Write-Output "Build and tests passed: $path (no tag, push, or release created)"; return }

    git show-ref --verify --quiet "refs/tags/$gitTag"
    if ($LASTEXITCODE -eq 1) {
        git tag -a $gitTag -m "Release $gitTag" $head
        if ($LASTEXITCODE -ne 0) { throw 'Could not create the tag.' }
    } elseif ($LASTEXITCODE -ne 0) { throw 'Could not inspect the local tag.' }
    git push origin "refs/tags/${gitTag}:refs/tags/${gitTag}"
    if ($LASTEXITCODE -ne 0) { throw 'Tag push failed; no release was published.' }

    $release = gh release view $gitTag --repo $repository --json isDraft --jq .isDraft 2>$null
    if ($LASTEXITCODE -ne 0) {
        $flags = @()
        if ($Tag.Contains('-')) { $flags += '--prerelease' }
        gh release create $gitTag --repo $repository --verify-tag --draft --notes-file $NotesFile --title "SWYF Custom AI $Tag" @flags
        if ($LASTEXITCODE -ne 0) { throw 'Could not create the draft release. The tag was pushed; rerun this command to retry.' }
        $release = 'true'
    }
    gh release upload $gitTag $path "$path.sha256" --repo $repository --clobber
    if ($LASTEXITCODE -ne 0) { throw 'Asset upload failed. Rerun the same command to retry; a newly created release remains a draft.' }
    if ($release -eq 'true' -and !$Draft) {
        gh release edit $gitTag --repo $repository --draft=false --latest=false
        if ($LASTEXITCODE -ne 0) { throw 'Assets uploaded, but publishing the draft failed. Rerun to retry.' }
    }
    gh release view $gitTag --repo $repository --json url --jq .url
    if ($LASTEXITCODE -ne 0) { throw 'Release uploaded, but its URL could not be retrieved.' }
} finally {
    $env:SWYF_GAME_DIR = $previousGameDir
    Pop-Location
}
