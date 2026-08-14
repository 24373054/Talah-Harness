[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $ArtifactDirectory,
    [Parameter(Mandatory = $true)] [string] $PackagePath,
    [Parameter(Mandatory = $true)] [string] $Publisher,
    [Parameter(Mandatory = $true)] [bool] $Signed,
    [Parameter()] [string] $UpdateBaseUri
)

. (Join-Path $PSScriptRoot 'Release.Common.ps1')

$repositoryRoot = Get-RepositoryRoot
$artifactPath = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
$package = (Resolve-Path -LiteralPath $PackagePath).Path
Assert-RepositoryChildPath -Path $artifactPath -RepositoryRoot $repositoryRoot | Out-Null
Assert-RepositoryChildPath -Path $package -RepositoryRoot $repositoryRoot | Out-Null

if ($UpdateBaseUri) {
    $uri = [Uri]$UpdateBaseUri
    if (-not $uri.IsAbsoluteUri -or $uri.Scheme -ne 'https') {
        throw 'UpdateBaseUri must be an absolute HTTPS URI.'
    }
    if (-not $Signed) {
        throw 'Update metadata with a remote URI may only be generated for a signed package.'
    }
    $baseUri = $UpdateBaseUri.TrimEnd('/')
}

$packageInfo = Get-Item -LiteralPath $package
$packageHash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
$packageUri = if ($UpdateBaseUri) { "$baseUri/$($packageInfo.Name)" } else { $null }

$updateMetadata = [ordered]@{
    schemaVersion = 1
    product = $script:ProductName
    releaseVersion = $script:ReleaseVersion
    packageVersion = $script:PackageVersion
    identityName = $script:PackageIdentityName
    publisher = $Publisher
    architecture = $script:Architecture
    minimumWindowsVersion = $script:MinimumWindowsVersion
    package = [ordered]@{
        fileName = $packageInfo.Name
        uri = $packageUri
        sha256 = $packageHash
        sizeBytes = $packageInfo.Length
        signed = $Signed
    }
}
$updatePath = Join-Path $artifactPath 'update-manifest.json'
[System.IO.File]::WriteAllText($updatePath, ($updateMetadata | ConvertTo-Json -Depth 8) + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

if ($UpdateBaseUri) {
    $template = Get-Content -LiteralPath (Join-Path $repositoryRoot 'installer\appinstaller\Talah-Harness.appinstaller.in') -Raw
    $appInstallerUri = "$baseUri/Talah-Harness.appinstaller"
    $content = $template.Replace('@@APPINSTALLER_URI@@', (ConvertTo-XmlAttributeValue $appInstallerUri))
    $content = $content.Replace('@@PACKAGE_URI@@', (ConvertTo-XmlAttributeValue $packageUri))
    $content = $content.Replace('@@PACKAGE_VERSION@@', $script:PackageVersion)
    $content = $content.Replace('@@IDENTITY_NAME@@', $script:PackageIdentityName)
    $content = $content.Replace('@@PUBLISHER@@', (ConvertTo-XmlAttributeValue $Publisher))
    [System.IO.File]::WriteAllText((Join-Path $artifactPath 'Talah-Harness.appinstaller'), $content, [System.Text.UTF8Encoding]::new($false))
}

$submodulePin = Get-GitValue -RepositoryRoot $repositoryRoot -Arguments @('ls-tree', 'HEAD', 'third_party/TLAH-Studio')
$commit = Get-GitValue -RepositoryRoot $repositoryRoot -Arguments @('rev-parse', 'HEAD')
$sourceDate = Get-SourceDateUtc -RepositoryRoot $repositoryRoot
$provenance = [ordered]@{
    schemaVersion = 1
    product = $script:ProductName
    version = $script:ReleaseVersion
    packageVersion = $script:PackageVersion
    sourceRepository = 'https://github.com/24373054/Talah-Harness'
    sourceCommit = $commit
    sourceDateUtc = $sourceDate.ToString('yyyy-MM-ddTHH:mm:ssZ', [System.Globalization.CultureInfo]::InvariantCulture)
    tlahStudioSubmodule = ($submodulePin -split '\s+')[2]
    runtimeIdentifier = $script:RuntimeIdentifier
    packageSha256 = $packageHash
    sbom = 'metadata/sbom.cdx.json'
    dependencyInventory = 'metadata/nuget-dependencies.json'
    rightsholderAuthorization = 'metadata/RIGHTSHOLDER_AUTHORIZATION.md'
    signing = if ($Signed) { 'Authenticode signed and timestamped' } else { 'UNSIGNED DEVELOPMENT ARTIFACT - NOT FOR PUBLIC DISTRIBUTION' }
}
$provenancePath = Join-Path $artifactPath 'provenance.json'
[System.IO.File]::WriteAllText($provenancePath, ($provenance | ConvertTo-Json -Depth 8) + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

$checksumPath = Join-Path $artifactPath 'SHA256SUMS'
$files = @(Get-ChildItem -LiteralPath $artifactPath -File -Recurse | Where-Object { $_.FullName -ne $checksumPath } | Sort-Object FullName)
$lines = foreach ($file in $files) {
    $relative = $file.FullName.Substring($artifactPath.Length).TrimStart('\', '/').Replace('\', '/')
    '{0}  {1}' -f (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $relative
}
[System.IO.File]::WriteAllText($checksumPath, (($lines -join "`n") + "`n"), [System.Text.UTF8Encoding]::new($false))

[pscustomobject]@{
    UpdateManifestPath = $updatePath
    ProvenancePath = $provenancePath
    ChecksumPath = $checksumPath
}
