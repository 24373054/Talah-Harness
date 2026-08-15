[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $SolutionPath,
    [Parameter(Mandatory = $true)] [string] $OutputDirectory,
    [Parameter()] [object[]] $AdditionalComponents = @()
)

. (Join-Path $PSScriptRoot 'Release.Common.ps1')

$repositoryRoot = Get-RepositoryRoot
$solution = (Resolve-Path -LiteralPath $SolutionPath).Path
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
Assert-RepositoryChildPath -Path $outputPath -RepositoryRoot $repositoryRoot | Out-Null
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null

$toolPath = Join-Path $repositoryRoot 'build\release-tools\cyclonedx'
if (-not (Test-Path -LiteralPath (Join-Path $toolPath 'dotnet-CycloneDX.exe'))) {
    New-Item -ItemType Directory -Path $toolPath -Force | Out-Null
    Invoke-NativeCommand -FilePath 'dotnet' -ArgumentList @('tool', 'install', '--tool-path', $toolPath, 'CycloneDX', '--version', $script:CycloneDxToolVersion) -FailureMessage 'Unable to install the pinned CycloneDX tool.'
}

$cycloneDx = Join-Path $toolPath 'dotnet-CycloneDX.exe'
Invoke-NativeCommand -FilePath $cycloneDx -ArgumentList @($solution, '-o', $outputPath, '-F', 'Json', '-fn', 'sbom.cdx.json', '-t', '-ed', '-ilt', '-ns', '-dpr', '-sn', $script:ProductName, '-sv', $script:ReleaseVersion) -FailureMessage 'CycloneDX SBOM generation failed.'

$sbomPath = Join-Path $outputPath 'sbom.cdx.json'
if (-not (Test-Path -LiteralPath $sbomPath)) {
    throw "CycloneDX did not produce the expected SBOM: $sbomPath"
}

$sbom = Get-Content -LiteralPath $sbomPath -Raw | ConvertFrom-Json
$sourceDate = Get-SourceDateUtc -RepositoryRoot $repositoryRoot
if ($sbom.PSObject.Properties.Name -contains 'serialNumber') {
    $sbom.PSObject.Properties.Remove('serialNumber')
}
if ($sbom.PSObject.Properties.Name -contains 'metadata') {
    $sbom.metadata.timestamp = $sourceDate.ToString('yyyy-MM-ddTHH:mm:ssZ', [System.Globalization.CultureInfo]::InvariantCulture)
}
function Test-ComponentField {
    param([object] $Component, [string] $Name)
    if ($Component -is [System.Collections.IDictionary]) { return $Component.Contains($Name) }
    return $Component.PSObject.Properties.Name -contains $Name
}

foreach ($component in $AdditionalComponents) {
    $hashObject = [ordered]@{ alg = [string]$component.HashAlgorithm; content = [string]$component.HashValue }
    $entry = [ordered]@{
        type = [string]$component.Type
        name = [string]$component.Name
        version = [string]$component.Version
        'bom-ref' = [string]$component.Name + '@' + [string]$component.Version
        hashes = @($hashObject)
    }
    if ((Test-ComponentField -Component $component -Name 'Purl') -and $component.Purl) { $entry.purl = [string]$component.Purl }
    if ((Test-ComponentField -Component $component -Name 'Description') -and $component.Description) { $entry.description = [string]$component.Description }
    if ((Test-ComponentField -Component $component -Name 'License') -and $component.License) { $entry.licenses = @(@{ license = @{ name = [string]$component.License } }) }
    $sbom.components += $entry
}

$canonicalSbom = $sbom | ConvertTo-Json -Depth 100
[System.IO.File]::WriteAllText($sbomPath, $canonicalSbom + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

$inventoryPath = Join-Path $outputPath 'nuget-dependencies.json'
& dotnet list $solution package --include-transitive --format json | Set-Content -LiteralPath $inventoryPath -Encoding UTF8
if ($LASTEXITCODE -ne 0) {
    throw 'NuGet dependency inventory generation failed.'
}
$inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
$repositoryUriPrefix = $repositoryRoot.Replace('\', '/').TrimEnd('/') + '/'
foreach ($project in $inventory.projects) {
    $projectPath = ([string]$project.path).Replace('\', '/')
    if ($projectPath.StartsWith($repositoryUriPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        $project.path = $projectPath.Substring($repositoryUriPrefix.Length)
    }
}
[System.IO.File]::WriteAllText($inventoryPath, ($inventory | ConvertTo-Json -Depth 100) + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

Copy-Item -LiteralPath (Join-Path $repositoryRoot 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $outputPath 'THIRD-PARTY-NOTICES.md') -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination (Join-Path $outputPath 'LICENSE') -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\RIGHTSHOLDER_AUTHORIZATION.md') -Destination (Join-Path $outputPath 'RIGHTSHOLDER_AUTHORIZATION.md') -Force

[pscustomobject]@{
    SbomPath = $sbomPath
    InventoryPath = $inventoryPath
    Tool = 'CycloneDX'
    ToolVersion = $script:CycloneDxToolVersion
}
