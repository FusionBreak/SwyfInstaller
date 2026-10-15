# Release script for SwyfInstaller.
# Builds, tests, packages the setup exe (+ .sha256), tags and publishes a GitHub Release.
# Usage:  powershell -ExecutionPolicy Bypass -File release.ps1 -Tag v1.0.0
#         powershell -ExecutionPolicy Bypass -File release.ps1 -Tag v1.0.0 -BuildOnly
# Inno Setup 6/7 is detected automatically; override with -IsccPath or ISCC_PATH.
param(
    [string]$Tag = "v1.0.0",
    [switch]$BuildOnly,
    [string]$IsccPath = $env:ISCC_PATH
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$version = $Tag.TrimStart('v')

function Find-InnoCompiler {
    if (![string]::IsNullOrWhiteSpace($IsccPath)) {
        if (!(Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
            throw "Inno Setup compiler not found at '$IsccPath'. Check -IsccPath or ISCC_PATH."
        }
        return (Resolve-Path -LiteralPath $IsccPath).Path
    }
    $command = Get-Command ISCC.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return $command.Source }
    foreach ($major in @(7, 6)) {
        foreach ($base in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, (Join-Path $env:LOCALAPPDATA 'Programs'))) {
            if ([string]::IsNullOrWhiteSpace($base)) { continue }
            $candidate = Join-Path $base "Inno Setup $major\ISCC.exe"
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
        }
    }
    throw "Inno Setup 6 or 7 not found. Install it from https://jrsoftware.org/isinfo.php or pass -IsccPath."
}

function Step($msg) { Write-Host ""; Write-Host "=== $msg ===" -ForegroundColor Cyan }
function Exec($cmd) {
    Write-Host "> $cmd"
    $code = 0
    Invoke-Expression $cmd
    $code = $LASTEXITCODE
    if ($code -ne 0) { throw "Command failed with exit code ${code}: $cmd" }
}

Step "Checking working tree"
$dirty = (git -C $root status --porcelain) -join "`n"
if ($dirty.Trim() -ne "") { throw "Working tree is not clean. Commit everything first." }

Step "Finding Inno Setup compiler"
$iscc = Find-InnoCompiler
Write-Host $iscc

Step "Building solution"
Exec "dotnet build `"$root\SwyfInstaller.sln`" -c Release --nologo -v q"

Step "Running selftest"
Exec "dotnet `"$root\SwyfInstaller\bin\Release\net10.0-windows\SwyfInstaller.dll`" selftest"

Step "Publishing executables"
Exec "dotnet publish `"$root\SwyfInstaller\SwyfInstaller.csproj`" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o `"$root\SwyfInstaller\publish`" --nologo -v q"
Exec "dotnet publish `"$root\SwyfInstaller.Gui\SwyfInstaller.Gui.csproj`" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o `"$root\SwyfInstaller.Gui\publish`" --nologo -v q"
Copy-Item "$root\SwyfInstaller\publish\SwyfInstaller.exe" "$root\SwyfInstaller.Gui\publish\" -Force

Step "Building setup"
Push-Location $root
try {
    & $iscc "/DAppVersion=$version" "installer.iss"
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed." }
}
finally { Pop-Location }
$setupName = "SwyfInstaller-Setup-v$version-win-x64.exe"
$setupPath = Join-Path $root "artifacts\$setupName"
if (!(Test-Path $setupPath)) { throw "Setup output not found: $setupPath" }
$hash = (Get-FileHash $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $setupName" | Set-Content "$setupPath.sha256" -NoNewline
Write-Host "${setupName}: $hash"
$assets = @($setupName)
$artifacts = Split-Path $setupPath -Parent

if ($BuildOnly) { Write-Host ""; Write-Host "Build-only done: $artifacts"; return }

Step "Tagging $Tag"
$existing = ""
try { $existing = git -C $root rev-parse $Tag 2>$null } catch { }
if ($existing.Trim() -ne "") { throw "Tag $Tag already exists. Delete it or pick a new tag." }
Exec "git -C `"$root`" tag -a $Tag -m `"SwyfInstaller $Tag`""
Exec "git -C `"$root`" push origin $Tag"

Step "Publishing GitHub Release $Tag"
$env:GIT_TERMINAL_PROMPT = "0"
$env:GCM_INTERACTIVE = "never"
$fill = "protocol=https`nhost=github.com`n`n" | git credential fill
$token = ($fill -split "`r?`n" | Where-Object { $_ -like 'password=*' } | Select-Object -First 1) -replace '^password=', ''
if ([string]::IsNullOrWhiteSpace($token)) { throw "No stored GitHub credentials found. Sign in once (e.g. git push) and retry." }
$headers = @{
    Authorization           = "Bearer $token"
    Accept                  = "application/vnd.github+json"
    "X-GitHub-Api-Version"  = "2022-11-28"
}
$notes = @"
Installer app + backend for SWYF Custom AI (mod source lives in custom-ai/).

- Guided 3-step UI: find the game, install/manage the mod, press F8 in-game.
- The app calls the backend directly: no commands or flags needed.
- Downloads the latest mod release and checksum-verifies it before touching the game.
- Update removes old package files, keeps settings, sign-in and backups.
- The app updates itself: it checks this repo on startup and installs
  new releases automatically (checksum-verified).

Run the setup to install (Start Menu + Programs list, no admin needed).
"@
$notesFile = Join-Path $root "releases\$Tag.md"
if (Test-Path -LiteralPath $notesFile -PathType Leaf) {
    $notes = Get-Content -LiteralPath $notesFile -Raw
}
$releaseBody = @{
    tag_name   = $Tag
    name       = "SwyfInstaller $Tag"
    body       = $notes
    draft      = $false
    prerelease = $false
} | ConvertTo-Json
$release = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/manifest-dex/SwyfInstaller/releases" `
    -Headers $headers -ContentType "application/json" -Body $releaseBody -TimeoutSec 60
$uploadBase = ($release.upload_url -split '\{')[0]
foreach ($name in $assets + @("$setupName.sha256")) {
    $path = Join-Path $artifacts $name
    Write-Host "Uploading $name ..."
    $uri = $uploadBase + "?name=" + [uri]::EscapeDataString($name)
    Invoke-RestMethod -Method Post -Uri $uri -Headers $headers `
        -ContentType "application/octet-stream" -InFile $path -TimeoutSec 900 | Out-Null
}
Write-Host ""
Write-Host "Released: $($release.html_url)" -ForegroundColor Green
