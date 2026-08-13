[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $SolutionPath,
    [Parameter(Mandatory = $true)] [string] $OutputDirectory
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
Invoke-NativeCommand -FilePath $cycloneDx -ArgumentList @($solution, '-o', $outputPath, '-F', 'Json', '-f', 'sbom.cdx.json', '-rs', '-dpr') -FailureMessage 'CycloneDX SBOM generation failed.'

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
$canonicalSbom = $sbom | ConvertTo-Json -Depth 100
[System.IO.File]::WriteAllText($sbomPath, $canonicalSbom + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

$inventoryPath = Join-Path $outputPath 'nuget-dependencies.json'
& dotnet list $solution package --include-transitive --format json | Set-Content -LiteralPath $inventoryPath -Encoding UTF8
if ($LASTEXITCODE -ne 0) {
    throw 'NuGet dependency inventory generation failed.'
}

Copy-Item -LiteralPath (Join-Path $repositoryRoot 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $outputPath 'THIRD-PARTY-NOTICES.md') -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination (Join-Path $outputPath 'LICENSE') -Force

[pscustomobject]@{
    SbomPath = $sbomPath
    InventoryPath = $inventoryPath
    Tool = 'CycloneDX'
    ToolVersion = $script:CycloneDxToolVersion
}
