[CmdletBinding()]
param(
    [string]$MakensisPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$project = Join-Path $repoRoot 'UWPHook\UWPHook.csproj'
$publishDir = Join-Path $repoRoot 'UWPHook\bin\Release\net8.0-windows\win-x64\publish'
$artifactDir = Join-Path $repoRoot 'artifacts'

if (-not $MakensisPath) {
    $makensis = Get-Command makensis.exe -ErrorAction SilentlyContinue
    if ($makensis) {
        $MakensisPath = $makensis.Source
    } else {
        $candidates = @(
            (Join-Path ${env:ProgramFiles(x86)} 'NSIS\makensis.exe'),
            (Join-Path $env:ProgramFiles 'NSIS\makensis.exe')
        )
        $MakensisPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
}

if (-not $MakensisPath -or -not (Test-Path -LiteralPath $MakensisPath)) {
    throw 'NSIS was not found. Install it with: winget install --id NSIS.NSIS --exact'
}

New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null

dotnet publish $project --configuration Release --runtime win-x64 --self-contained true
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$publishedExe = Join-Path $publishDir 'UWPHook.exe'
if (-not (Test-Path -LiteralPath $publishedExe)) {
    throw "Published application was not found at $publishedExe"
}

$Version = (Get-Item -LiteralPath $publishedExe).VersionInfo.FileVersion
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "Published application has an invalid four-part file version: $Version"
}

$installer = Join-Path $artifactDir "UWPHook-$Version-win-x64-setup.exe"
& $MakensisPath "/DAPP_VERSION=$Version" "/DPUBLISH_DIR=$publishDir" "/DINSTALLER_OUT=$installer" (Join-Path $repoRoot 'UWPHook.nsi')
if ($LASTEXITCODE -ne 0) {
    throw "makensis failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path -LiteralPath $installer)) {
    throw "NSIS completed without producing $installer"
}

$file = Get-Item -LiteralPath $installer
$hash = Get-FileHash -LiteralPath $installer -Algorithm SHA256
Write-Output "Installer: $($file.FullName)"
Write-Output "Size: $($file.Length) bytes"
Write-Output "SHA256: $($hash.Hash)"
