[CmdletBinding()]
param(
    [Parameter()] [string] $RepositoryRoot,
    [Parameter()] [string] $ArtifactDirectory
)

. (Join-Path $PSScriptRoot 'Release.Common.ps1')

if (-not $RepositoryRoot) { $RepositoryRoot = Get-RepositoryRoot }
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$failures = [System.Collections.Generic.List[string]]::new()

function Add-Failure([string] $Message) {
    $script:failures.Add($Message)
}

try {
    [xml]$appManifest = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'src\Talah.Harness.App\app.manifest') -Raw
    $assemblyIdentity = $appManifest.assembly.assemblyIdentity
    if ($assemblyIdentity.version -ne $script:PackageVersion) { Add-Failure "app.manifest version is '$($assemblyIdentity.version)', expected '$script:PackageVersion'." }
}
catch { Add-Failure "Unable to parse app.manifest: $($_.Exception.Message)" }

try {
    [xml]$buildProps = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'src\Directory.Build.props') -Raw
    $version = [string]$buildProps.Project.PropertyGroup.Version
    $fileVersion = [string]$buildProps.Project.PropertyGroup.FileVersion
    if ($version -ne $script:ReleaseVersion) { Add-Failure "Product version is '$version', expected '$script:ReleaseVersion'." }
    if ($fileVersion -ne $script:PackageVersion) { Add-Failure "File version is '$fileVersion', expected '$script:PackageVersion'." }
}
catch { Add-Failure "Unable to parse Directory.Build.props: $($_.Exception.Message)" }

try {
    [xml]$project = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'src\Talah.Harness.App\Talah.Harness.App.csproj') -Raw
    $propertyGroup = $project.Project.PropertyGroup
    if ([string]$propertyGroup.TargetPlatformMinVersion -ne $script:MinimumWindowsVersion) { Add-Failure 'The app project minimum Windows version is inconsistent.' }
    if ([string]$propertyGroup.RuntimeIdentifiers -ne $script:RuntimeIdentifier) { Add-Failure 'The app project runtime identifier is inconsistent.' }
    if ([string]$propertyGroup.Platforms -ne $script:Architecture) { Add-Failure 'The app project platform is inconsistent.' }
}
catch { Add-Failure "Unable to parse the app project: $($_.Exception.Message)" }

$solutionPath = Join-Path $RepositoryRoot 'Talah.Harness.sln'
$solutionContent = Get-Content -LiteralPath $solutionPath -Raw
$releaseDriver = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'tools\release\Build-Release.ps1') -Raw

$kernelRestore = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'tools\release\Restore-KernelRuntimes.ps1') -Raw
foreach ($expected in @('299d8603750caaffc24f218789d989f77cf157070bd42451d352f5578a800766', '1becf92ceb23edd7d951e7e3d8efcbe9c9808f5cc728f1b75277d5f951ada5c2')) {
    if (-not $kernelRestore.Contains($expected, [StringComparison]::OrdinalIgnoreCase)) {
        Add-Failure "Pinned kernel runtime provenance is missing: $expected"
    }
}
foreach ($expected in @('kernels\codex', 'kernels\opencode')) {
    if (-not $releaseDriver.Contains($expected, [StringComparison]::OrdinalIgnoreCase)) {
        Add-Failure "Kernel runtime staging is missing from the release driver: $expected"
    }
}
$controllerSource = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'src\Talah.Harness.App\Services\HarnessController.cs') -Raw
if (-not $controllerSource.Contains('"kernels"', [StringComparison]::OrdinalIgnoreCase)) {
    Add-Failure 'The desktop controller does not discover the bundled kernel runtimes.'
}

if ($releaseDriver -notmatch "(?m)ArgumentList\s+@\('restore'.*'--maxcpucount:1'") {
    Add-Failure 'Release restore is not bounded to a single MSBuild node.'
}
foreach ($testProject in Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'tests') -Recurse -Filter '*.Tests.csproj') {
    $relativeTestProject = $testProject.FullName.Substring($RepositoryRoot.Length).TrimStart('\', '/').Replace('/', '\')
    if (-not $solutionContent.Contains('"' + $relativeTestProject + '"', [StringComparison]::OrdinalIgnoreCase)) {
        Add-Failure "Test project is not included in the release solution: $relativeTestProject"
    }
}
foreach ($tlahProject in @(
    @{ Path = 'third_party\TLAH-Studio\TLAHStudio.Core\TLAHStudio.Core.csproj'; Id = 'A1B2C3D4-E5F6-7890-ABCD-EF1234567890' },
    @{ Path = 'third_party\TLAH-Studio\TLAHStudio.Data\TLAHStudio.Data.csproj'; Id = 'B2C3D4E5-F6A7-8901-BCDE-F12345678901' }
)) {
    if (-not $solutionContent.Contains('"' + $tlahProject.Path + '"', [StringComparison]::OrdinalIgnoreCase)) {
        Add-Failure "TLAH release dependency is not included in the release solution: $($tlahProject.Path)"
    }
    $releaseMapping = "{$($tlahProject.Id)}.Release|Any CPU.ActiveCfg = Release|Any CPU"
    if (-not $solutionContent.Contains($releaseMapping, [StringComparison]::OrdinalIgnoreCase)) {
        Add-Failure "TLAH release dependency is not mapped to Release: $($tlahProject.Path)"
    }
}

$manifestTemplate = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'installer\msix\AppxManifest.xml.in') -Raw
foreach ($expected in @($script:PackageIdentityName, $script:MinimumWindowsVersion, 'ProcessorArchitecture="x64"', 'Talah.Harness.App.exe')) {
    if (-not $manifestTemplate.Contains($expected)) { Add-Failure "MSIX manifest template is missing '$expected'." }
}

$workflowFiles = @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot '.github\workflows') -Filter '*.yml' -ErrorAction SilentlyContinue)
foreach ($workflow in $workflowFiles) {
    $content = Get-Content -LiteralPath $workflow.FullName -Raw
    foreach ($match in [regex]::Matches($content, '(?m)^\s*uses:\s*([^\s#]+)')) {
        $reference = $match.Groups[1].Value
        if ($reference -notmatch '^[^@\s]+@[0-9a-f]{40}$') { Add-Failure "$($workflow.Name) has an action that is not pinned by a full commit SHA: $reference" }
    }
    if ($content -notmatch 'submodules:\s*recursive') { Add-Failure "$($workflow.Name) does not recursively check out submodules." }
    if ($workflow.Name -eq 'release.yml') {
        if ($content -match '(?m)^ {6}(SIGNING_PFX_BASE64|SIGNING_PASSWORD):') {
            Add-Failure 'release.yml exposes a signing secret at job scope instead of limiting it to a shell step.'
        }
        if ($content -match 'SIGNING_PFX_PATH.*GITHUB_ENV') {
            Add-Failure 'release.yml persists the signing-certificate path across steps.'
        }
        if ($content -notmatch '(?s)finally\s*\{.*Remove-Item\s+-LiteralPath\s+\$certificatePath') {
            Add-Failure 'release.yml does not remove temporary signing material in a finally block.'
        }
    }
}

$submoduleEntry = Get-GitValue -RepositoryRoot $RepositoryRoot -Arguments @('ls-tree', 'HEAD', 'third_party/TLAH-Studio')
if ($submoduleEntry -notmatch '^160000 commit [0-9a-f]{40}\s+third_party/TLAH-Studio$') {
    Add-Failure 'TLAH Studio is not pinned as a Git submodule in HEAD.'
}
$submoduleDiff = & git -c core.autocrlf=true -C $RepositoryRoot diff --submodule=diff --exit-code -- third_party/TLAH-Studio 2>&1
if ($LASTEXITCODE -ne 0) { Add-Failure "TLAH Studio submodule pin has semantic local changes: $submoduleDiff" }
$submodulePath = Join-Path $RepositoryRoot 'third_party\TLAH-Studio'
if (Test-Path -LiteralPath $submodulePath) {
    $expectedPin = ($submoduleEntry -split '\s+')[2]
    $actualPin = (& git -C $submodulePath rev-parse HEAD 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualPin -ne $expectedPin) { Add-Failure "TLAH Studio checkout is not at pinned commit $expectedPin." }
    $submoduleStatus = (& git -c core.autocrlf=true -C $submodulePath status --porcelain --untracked-files=normal 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $submoduleStatus) { Add-Failure "TLAH Studio checkout is not clean: $submoduleStatus" }
}

$ownedRoots = @(
    (Join-Path $RepositoryRoot 'installer'),
    (Join-Path $RepositoryRoot 'tools\release'),
    (Join-Path $RepositoryRoot '.github'),
    (Join-Path $RepositoryRoot 'docs\RELEASE_ENGINEERING.md'),
    (Join-Path $RepositoryRoot 'docs\UPDATE_AND_RECOVERY.md')
)
$scanFiles = foreach ($root in $ownedRoots) {
    if (Test-Path -LiteralPath $root -PathType Leaf) { Get-Item -LiteralPath $root }
    elseif (Test-Path -LiteralPath $root) { Get-ChildItem -LiteralPath $root -File -Recurse }
}
$secretPatterns = @(
    '-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----',
    '\bAKIA[0-9A-Z]{16}\b',
    '\bgh[opurs]_[A-Za-z0-9]{30,}\b',
    '(?i)client_secret\s*[:=]\s*["''][^"'']{8,}["'']'
)
foreach ($file in $scanFiles) {
    $content = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($pattern in $secretPatterns) {
        if ($content -match $pattern) { Add-Failure "Potential committed secret in $($file.FullName) matching '$pattern'." }
    }
}

$productionScanFiles = @(
    Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'tools\release') -File -Recurse
    Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot '.github\workflows') -File -Recurse
)
foreach ($file in $productionScanFiles) {
    if ($file.FullName -eq $MyInvocation.MyCommand.Path) { continue }
    $content = Get-Content -LiteralPath $file.FullName -Raw
    if ($content -match '(?i)\b(TODO|FIXME|CHANGEME)\b|example\.invalid') {
        Add-Failure "Forbidden production placeholder in $($file.FullName)."
    }
}

if ($ArtifactDirectory) {
    $artifacts = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
    Assert-RepositoryChildPath -Path $artifacts -RepositoryRoot $RepositoryRoot | Out-Null
    foreach ($required in @('SHA256SUMS', 'provenance.json', 'update-manifest.json', 'RELEASE-NOTES.md', 'INSTALL-SELFSIGNED.md', 'metadata\sbom.cdx.json', 'metadata\nuget-dependencies.json', 'metadata\THIRD-PARTY-NOTICES.md', 'metadata\RIGHTSHOLDER_AUTHORIZATION.md')) {
        if (-not (Test-Path -LiteralPath (Join-Path $artifacts $required) -PathType Leaf)) { Add-Failure "Release artifact is missing: $required" }
    }
    $update = Get-Content -LiteralPath (Join-Path $artifacts 'update-manifest.json') -Raw | ConvertFrom-Json
    if ($update.packageVersion -ne $script:PackageVersion -or $update.identityName -ne $script:PackageIdentityName -or $update.architecture -ne $script:Architecture) {
        Add-Failure 'Update metadata identity/version/architecture is inconsistent.'
    }
    foreach ($artifactFile in Get-ChildItem -LiteralPath $artifacts -File -Recurse) {
        if ($artifactFile.Extension -in @('.json', '.xml', '.appinstaller', '.md') -or $artifactFile.Name -eq 'SHA256SUMS') {
            $artifactContent = Get-Content -LiteralPath $artifactFile.FullName -Raw
            if ($artifactContent -match '@@[A-Z0-9_]+@@|(?i)\b(TODO|FIXME|CHANGEME)\b|example\.invalid') {
                Add-Failure "Forbidden placeholder in release artifact: $($artifactFile.FullName)"
            }
        }
    }
    $sumLines = Get-Content -LiteralPath (Join-Path $artifacts 'SHA256SUMS')
    foreach ($line in $sumLines) {
        if ($line -notmatch '^([0-9a-f]{64})  (.+)$') { Add-Failure "Malformed SHA256SUMS line: $line"; continue }
        $target = Join-Path $artifacts $Matches[2].Replace('/', '\')
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { Add-Failure "Checksum target is absent: $($Matches[2])"; continue }
        $actual = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $Matches[1]) { Add-Failure "Checksum mismatch: $($Matches[2])" }
    }
}

if ($failures.Count -gt 0) {
    throw ($failures -join [Environment]::NewLine)
}

Write-Host 'Release configuration validation passed.'
