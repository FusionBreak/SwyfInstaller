param([string]$GameDir = $env:SWYF_GAME_DIR)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($GameDir)) { throw 'Pass -GameDir or set SWYF_GAME_DIR to your game installation folder.' }
Push-Location $PSScriptRoot
try {
    dotnet build src/Bridge/Bridge.csproj -c Release "-p:GameDir=$GameDir" --nologo
    if ($LASTEXITCODE) { throw 'Bridge build failed.' }
    dotnet run --project tests/BridgeRouting/BridgeRouting.csproj -c Release "-p:GameDir=$GameDir"
    if ($LASTEXITCODE) { throw 'Bridge request routing check failed.' }
    dotnet publish src/Panel/Panel.csproj -c Release -r win-x64 --self-contained false -o dist/panel --nologo
    if ($LASTEXITCODE) { throw 'Panel build failed.' }
    dotnet publish tools/Installer/Installer.csproj -c Release -r win-x64 --self-contained false -o dist/installer --nologo
    if ($LASTEXITCODE) { throw 'Installer build failed.' }
    & ./tools/verify-bridge.ps1 -GameDir $GameDir
    New-Item -ItemType Directory -Force -Path dist/bridge | Out-Null
    Copy-Item -LiteralPath src/Bridge/bin/Release/net471/SWYF.CustomAI.Bridge.dll -Destination dist/bridge/SWYF.CustomAI.Bridge.dll
    Copy-Item -LiteralPath install.ps1,launch.ps1,../README.md -Destination dist
    Copy-Item -LiteralPath packaging/prerequisites.ps1 -Destination dist
    Write-Output 'Package ready: dist'
    # A fresh staging directory keeps local settings/backups and stale files out of the release ZIP.
    $releaseRoot = Join-Path $PSScriptRoot ('artifacts/release/' + [guid]::NewGuid().ToString('N'))
    $releasePackage = Join-Path $releaseRoot 'CustomAI/package'
    New-Item -ItemType Directory -Force -Path $releasePackage | Out-Null
    Copy-Item -LiteralPath packaging/prerequisites.ps1 -Destination $releasePackage
    foreach ($folder in @('bridge', 'panel', 'installer')) {
        $destination = Join-Path $releasePackage $folder
        New-Item -ItemType Directory -Force -Path $destination | Out-Null
        foreach ($file in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot "dist/$folder") -File) {
            if ($file.Extension -ne '.pdb') { Copy-Item -LiteralPath $file.FullName -Destination $destination }
        }
    }
    Copy-Item -LiteralPath ../README.md -Destination (Join-Path $releaseRoot 'CustomAI/README.md')
    foreach ($wrapper in Get-ChildItem -LiteralPath packaging -Filter '*.cmd') {
        $text = [IO.File]::ReadAllText($wrapper.FullName).Replace("`r`n", "`n").Replace("`n", "`r`n")
        [IO.File]::WriteAllText((Join-Path $releaseRoot $wrapper.Name), $text, [Text.UTF8Encoding]::new($false))
    }
    # Windows PowerShell 5.1 Compress-Archive writes backslash entry names; ZIP requires forward slashes.
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $zipPath = Join-Path $PSScriptRoot 'artifacts/SWYF-Custom-AI-win-x64.zip'
    New-Item -ItemType Directory -Force -Path (Split-Path $zipPath) | Out-Null
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    $zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $rootLength = (Get-Item -LiteralPath $releaseRoot).FullName.TrimEnd('\').Length + 1
        foreach ($file in Get-ChildItem -LiteralPath $releaseRoot -Recurse -File) {
            $entryName = $file.FullName.Substring($rootLength).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $entryName, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $zip.Dispose() }
    Write-Output 'Extract-to-game release: artifacts/SWYF-Custom-AI-win-x64.zip'
} finally { Pop-Location }
