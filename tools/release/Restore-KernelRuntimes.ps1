[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $OutputDirectory
)

. (Join-Path $PSScriptRoot 'Release.Common.ps1')

$ErrorActionPreference = 'Stop'

$codexUrl = 'https://registry.npmjs.org/@openai/codex/-/codex-0.147.0-win32-x64.tgz'
$codexSha256 = '299d8603750caaffc24f218789d989f77cf157070bd42451d352f5578a800766'
$openCodeUrl = 'https://github.com/anomalyco/opencode/releases/download/v1.18.9/opencode-windows-x64.zip'
$openCodeSha256 = '1becf92ceb23edd7d951e7e3d8efcbe9c9808f5cc728f1b75277d5f951ada5c2'

$repositoryRoot = Get-RepositoryRoot
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
Assert-RepositoryChildPath -Path $outputPath -RepositoryRoot $repositoryRoot | Out-Null
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null

$cachePath = Join-Path $repositoryRoot 'build\release-tools\kernel-cache'
$downloads = Join-Path $cachePath 'downloads'
$codexDirectory = Join-Path $outputPath 'codex'
$openCodeDirectory = Join-Path $outputPath 'opencode'
New-Item -ItemType Directory -Path $downloads -Force | Out-Null
New-Item -ItemType Directory -Path $codexDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $openCodeDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $cachePath -Force | Out-Null

$codexArchive = Join-Path $downloads 'codex-0.147.0-win32-x64.tgz'
$openCodeArchive = Join-Path $downloads 'opencode-windows-x64.zip'

function Restore-VerifiedArchive {
    param(
        [Parameter(Mandatory = $true)] [string] $Url,
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [string] $ExpectedSha256,
        [Parameter(Mandatory = $true)] [string] $Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Write-Host "Downloading pinned $Description..."
        Invoke-NativeCommand -FilePath 'curl.exe' -ArgumentList @('-L', '--fail', '--retry', '3', '--output', $Path, $Url) -FailureMessage "Failed to download $Description."
    }

    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $ExpectedSha256) {
        throw "$Description SHA-256 mismatch. Expected $ExpectedSha256, actual $actual"
    }
    Write-Host "$Description SHA-256 verified: $actual"
}

Restore-VerifiedArchive -Url $codexUrl -Path $codexArchive -ExpectedSha256 $codexSha256 -Description 'Codex CLI 0.147.0 win32-x64 npm package'
Restore-VerifiedArchive -Url $openCodeUrl -Path $openCodeArchive -ExpectedSha256 $openCodeSha256 -Description 'OpenCode 1.18.9 windows-x64 release asset'

$codexExtract = Join-Path $downloads 'codex-extracted'
if (Test-Path -LiteralPath $codexExtract) { Remove-Item -LiteralPath $codexExtract -Recurse -Force }
New-Item -ItemType Directory -Path $codexExtract -Force | Out-Null
Invoke-NativeCommand -FilePath 'tar.exe' -ArgumentList @('-xf', $codexArchive, '-C', $codexExtract) -FailureMessage 'tar failed to extract the Codex npm package.'
$codexSource = Get-ChildItem -LiteralPath $codexExtract -Recurse -Filter 'codex.exe' -File |
    Where-Object { $_.FullName -match '[\\/]vendor[\\/]x86_64-pc-windows-msvc[\\/]bin[\\/]codex\.exe$' } |
    Select-Object -First 1
if ($null -eq $codexSource) { throw 'The pinned Codex npm package did not contain vendor/x86_64-pc-windows-msvc/bin/codex.exe.' }
$codexTarget = Join-Path $codexDirectory 'codex.exe'
Copy-Item -LiteralPath $codexSource.FullName -Destination $codexTarget -Force
$codexVersion = (& $codexTarget --version 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $codexVersion -notmatch '^codex-cli 0\.147\.0') {
    throw "Bundled Codex version check failed: '$codexVersion'"
}
Write-Host "Bundled Codex version: $codexVersion"

$openCodeExtract = Join-Path $downloads 'opencode-extracted'
if (Test-Path -LiteralPath $openCodeExtract) { Remove-Item -LiteralPath $openCodeExtract -Recurse -Force }
New-Item -ItemType Directory -Path $openCodeExtract -Force | Out-Null
Expand-Archive -LiteralPath $openCodeArchive -DestinationPath $openCodeExtract -Force
$openCodeSource = Join-Path $openCodeExtract 'opencode.exe'
if (-not (Test-Path -LiteralPath $openCodeSource -PathType Leaf)) { throw 'The pinned OpenCode archive did not contain opencode.exe.' }
$openCodeTarget = Join-Path $openCodeDirectory 'opencode.exe'
Copy-Item -LiteralPath $openCodeSource -Destination $openCodeTarget -Force
$openCodeVersion = (& $openCodeTarget --version 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $openCodeVersion -notmatch '^1\.18\.9') {
    throw "Bundled OpenCode version check failed: '$openCodeVersion'"
}
Write-Host "Bundled OpenCode version: $openCodeVersion"

[pscustomobject]@{
    CodexExecutable = $codexTarget
    OpenCodeExecutable = $openCodeTarget
    CodexArchiveSha256 = $codexSha256
    OpenCodeArchiveSha256 = $openCodeSha256
    CodexExecutableSha256 = (Get-FileHash -LiteralPath $codexTarget -Algorithm SHA256).Hash.ToLowerInvariant()
    OpenCodeExecutableSha256 = (Get-FileHash -LiteralPath $openCodeTarget -Algorithm SHA256).Hash.ToLowerInvariant()
}
