[CmdletBinding(DefaultParameterSetName = 'Remote')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Remote')] [Uri] $ManifestUri,
    [Parameter(Mandatory = $true, ParameterSetName = 'Local')] [string] $ManifestPath,
    [Parameter(Mandatory = $true)] [string] $DownloadDirectory
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$downloadRoot = [System.IO.Path]::GetFullPath($DownloadDirectory)
if (-not (Test-Path -LiteralPath $downloadRoot)) { New-Item -ItemType Directory -Path $downloadRoot -Force | Out-Null }
if ((Get-Item -LiteralPath $downloadRoot).Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
    throw 'DownloadDirectory must not be a reparse point.'
}

if ($PSCmdlet.ParameterSetName -eq 'Remote') {
    if (-not $ManifestUri.IsAbsoluteUri -or $ManifestUri.Scheme -ne 'https') { throw 'ManifestUri must use HTTPS.' }
    $manifestTemp = Join-Path $downloadRoot 'update-manifest.json.partial'
    Invoke-WebRequest -Uri $ManifestUri -OutFile $manifestTemp -UseBasicParsing
    $manifestContent = Get-Content -LiteralPath $manifestTemp -Raw
    Remove-Item -LiteralPath $manifestTemp -Force
}
else {
    $manifestContent = Get-Content -LiteralPath (Resolve-Path -LiteralPath $ManifestPath).Path -Raw
}

$manifest = $manifestContent | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1) { throw "Unsupported update manifest schema: $($manifest.schemaVersion)" }
if (-not $manifest.package.signed) { throw 'Refusing an update manifest that does not declare a signed package.' }
$packageUri = [Uri]$manifest.package.uri
if (-not $packageUri.IsAbsoluteUri -or $packageUri.Scheme -ne 'https') { throw 'Update package URI must use HTTPS.' }
if ([string]$manifest.package.fileName -ne [System.IO.Path]::GetFileName([string]$manifest.package.fileName)) { throw 'Update package fileName is invalid.' }
if ([string]$manifest.package.sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'Update package SHA-256 is invalid.' }

$finalPath = Join-Path $downloadRoot ([string]$manifest.package.fileName)
$partialPath = $finalPath + '.partial'
if (Test-Path -LiteralPath $partialPath) { Remove-Item -LiteralPath $partialPath -Force }
try {
    Invoke-WebRequest -Uri $packageUri -OutFile $partialPath -UseBasicParsing
    $actualHash = (Get-FileHash -LiteralPath $partialPath -Algorithm SHA256).Hash
    if ($actualHash -ne [string]$manifest.package.sha256) { throw 'Downloaded package SHA-256 does not match signed release metadata.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $partialPath
    if ($signature.Status -ne 'Valid') { throw "Downloaded package Authenticode signature is not valid: $($signature.Status)" }
    if ($signature.SignerCertificate.Subject -ne [string]$manifest.publisher) { throw 'Downloaded package signer does not match the update manifest publisher.' }
    Move-Item -LiteralPath $partialPath -Destination $finalPath -Force
}
catch {
    if (Test-Path -LiteralPath $partialPath) { Remove-Item -LiteralPath $partialPath -Force }
    throw
}

Write-Host "Verified update package: $finalPath"
Write-Host 'Installation is intentionally not automatic. Review the version, then explicitly open the verified MSIX or App Installer entry.'
