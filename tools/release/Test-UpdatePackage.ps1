[CmdletBinding(DefaultParameterSetName = 'Remote')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Remote')] [Uri] $ManifestUri,
    [Parameter(Mandatory = $true, ParameterSetName = 'Local')] [string] $ManifestPath,
    [Parameter(Mandatory = $true)] [string] $DownloadDirectory
)

. (Join-Path $PSScriptRoot 'Release.Common.ps1')

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
    if ((Get-Item -LiteralPath $partialPath).Length -ne [long]$manifest.package.sizeBytes) { throw 'Downloaded package length does not match release metadata.' }
    $actualHash = (Get-FileHash -LiteralPath $partialPath -Algorithm SHA256).Hash
    if ($actualHash -ne [string]$manifest.package.sha256) { throw 'Downloaded package SHA-256 does not match signed release metadata.' }

    $signTool = Get-WindowsSdkTool -Name 'signtool.exe'
    & $signTool verify /pa /v $partialPath | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Downloaded MSIX signature verification failed. signtool exit code: $LASTEXITCODE" }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($partialPath)
    try {
        $manifestEntry = $archive.GetEntry('AppxManifest.xml')
        if ($null -eq $manifestEntry) { throw 'Downloaded MSIX does not contain AppxManifest.xml.' }
        $reader = [System.IO.StreamReader]::new($manifestEntry.Open())
        try { [xml]$packageManifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally { $archive.Dispose() }
    $identity = $packageManifest.Package.Identity
    if ($identity.Publisher -ne [string]$manifest.publisher) { throw 'Downloaded package manifest Publisher does not match update metadata.' }
    if ($identity.Name -ne [string]$manifest.identityName) { throw 'Downloaded package identity does not match update metadata.' }
    if ($identity.Version -ne [string]$manifest.packageVersion) { throw 'Downloaded package version does not match update metadata.' }
    if ($identity.ProcessorArchitecture -ne [string]$manifest.architecture) { throw 'Downloaded package architecture does not match update metadata.' }
    Move-Item -LiteralPath $partialPath -Destination $finalPath -Force
}
catch {
    if (Test-Path -LiteralPath $partialPath) { Remove-Item -LiteralPath $partialPath -Force }
    throw
}

Write-Host "Verified update package: $finalPath"
Write-Host 'Installation is intentionally not automatic. Review the version, then explicitly open the verified MSIX or App Installer entry.'
