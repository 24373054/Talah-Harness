[CmdletBinding()]
param(
    [Parameter()] [ValidateSet('Release')] [string] $Configuration = 'Release',
    [Parameter()] [ValidateSet('x64')] [string] $Architecture = 'x64',
    [Parameter()] [string] $Publisher = 'CN=Talah Harness Development',
    [Parameter()] [string] $CertificatePath,
    [Parameter()] [SecureString] $CertificatePassword,
    [Parameter()] [string] $TimestampUrl = 'http://timestamp.digicert.com',
    [Parameter()] [string] $UpdateBaseUri,
    [Parameter()] [switch] $SkipTests
)

. (Join-Path $PSScriptRoot 'Release.Common.ps1')

$repositoryRoot = Get-RepositoryRoot
$solution = Join-Path $repositoryRoot 'Talah.Harness.sln'
$appProject = Join-Path $repositoryRoot 'src\Talah.Harness.App\Talah.Harness.App.csproj'
$releaseRoot = Reset-RepositoryDirectory -Path (Join-Path $repositoryRoot 'build\release') -RepositoryRoot $repositoryRoot
$stagingRoot = Reset-RepositoryDirectory -Path (Join-Path $repositoryRoot 'build\release-staging') -RepositoryRoot $repositoryRoot
$publishDirectory = Join-Path $stagingRoot 'publish\win-x64'
$packageDirectory = Join-Path $releaseRoot 'packages'
$metadataDirectory = Join-Path $releaseRoot 'metadata'
$kernelDirectory = Join-Path $stagingRoot 'kernels'
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $metadataDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $kernelDirectory -Force | Out-Null

& (Join-Path $PSScriptRoot 'Test-ReleaseConfiguration.ps1') -RepositoryRoot $repositoryRoot

Write-Host 'Restoring pinned .NET SDK solution inputs...'
Invoke-NativeCommand -FilePath 'dotnet' -ArgumentList @('restore', $solution, '--force-evaluate', '--disable-build-servers', '--maxcpucount:1', '-p:UseSharedCompilation=false', '-p:RuntimeIdentifier=win-x64') -FailureMessage 'dotnet restore failed.'

Write-Host 'Building Release/x64 with warnings as errors...'
Invoke-NativeCommand -FilePath 'dotnet' -ArgumentList @('build', $solution, '--configuration', $Configuration, '--no-restore', '--disable-build-servers', '--maxcpucount:1', '-p:UseSharedCompilation=false', '-p:TreatWarningsAsErrors=true', '-p:ContinuousIntegrationBuild=true') -FailureMessage 'dotnet build failed.'

if (-not $SkipTests) {
    Write-Host 'Running the release test suite...'
    Invoke-NativeCommand -FilePath 'dotnet' -ArgumentList @('test', $solution, '--configuration', $Configuration, '--no-build', '--disable-build-servers', '--maxcpucount:1', '--logger', 'trx', '--results-directory', (Join-Path $stagingRoot 'test-results')) -FailureMessage 'dotnet test failed.'
}

Write-Host 'Restoring and verifying pinned external kernel runtimes...'
$kernels = & (Join-Path $PSScriptRoot 'Restore-KernelRuntimes.ps1') -OutputDirectory $kernelDirectory

Write-Host 'Publishing the self-contained Windows x64 application...'
Invoke-NativeCommand -FilePath 'dotnet' -ArgumentList @('publish', $appProject, '--configuration', $Configuration, '--runtime', 'win-x64', '--self-contained', 'true', '--no-restore', '--disable-build-servers', '--maxcpucount:1', '--output', $publishDirectory, '-p:Platform=x64', '-p:UseSharedCompilation=false', '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:DebugType=None', '-p:DebugSymbols=false', '-p:ContinuousIntegrationBuild=true') -FailureMessage 'dotnet publish failed.'

Write-Host 'Staging pinned kernel runtimes into the published application...'
$publishedCodexDirectory = Join-Path $publishDirectory 'kernels\codex'
$publishedOpenCodeDirectory = Join-Path $publishDirectory 'kernels\opencode'
New-Item -ItemType Directory -Path $publishedCodexDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $publishedOpenCodeDirectory -Force | Out-Null
Copy-Item -LiteralPath $kernels.CodexExecutable -Destination (Join-Path $publishedCodexDirectory 'codex.exe') -Force
Copy-Item -LiteralPath $kernels.OpenCodeExecutable -Destination (Join-Path $publishedOpenCodeDirectory 'opencode.exe') -Force

Write-Host 'Smoke-testing published WinUI startup and graceful shutdown...'
& (Join-Path $PSScriptRoot 'Test-ApplicationLaunch.ps1') -ExecutablePath (Join-Path $publishDirectory 'Talah.Harness.App.exe')

Write-Host 'Generating the pinned CycloneDX SBOM and dependency inventory...'
$additionalSbomComponents = @(
    @{ Type = 'application'; Name = 'OpenAI Codex CLI'; Version = '0.147.0'; HashAlgorithm = 'SHA-256'; HashValue = $kernels.CodexExecutableSha256; License = 'Apache-2.0'; Description = 'Pinned win32-x64 codex.exe from the official @openai/codex npm package.' },
    @{ Type = 'application'; Name = 'OpenCode'; Version = '1.18.9'; HashAlgorithm = 'SHA-256'; HashValue = $kernels.OpenCodeExecutableSha256; License = 'MIT'; Description = 'Pinned windows-x64 opencode.exe from the official OpenCode GitHub release.' }
)
& (Join-Path $PSScriptRoot 'New-Sbom.ps1') -SolutionPath $solution -OutputDirectory $metadataDirectory -AdditionalComponents $additionalSbomComponents

Write-Host 'Building the MSIX package...'
$packageArguments = @{
    PublishDirectory = $publishDirectory
    OutputDirectory = $packageDirectory
    Publisher = $Publisher
    PackageVersion = $script:PackageVersion
    TimestampUrl = $TimestampUrl
}
if ($CertificatePath) {
    $packageArguments.CertificatePath = $CertificatePath
    $packageArguments.CertificatePassword = $CertificatePassword
}
$signingLevel = if (-not $CertificatePath) { 'unsigned-development' } elseif ($Publisher -eq $script:DeveloperPublisher) { 'self-signed-development' } else { 'production-authenticode' }
$package = & (Join-Path $PSScriptRoot 'New-MsixPackage.ps1') @packageArguments

if ($signingLevel -eq 'self-signed-development' -and $CertificatePath) {
    $certificateCerPath = [System.IO.Path]::ChangeExtension($CertificatePath, '.cer')
    if (Test-Path -LiteralPath $certificateCerPath -PathType Leaf) {
        Copy-Item -LiteralPath $certificateCerPath -Destination (Join-Path $releaseRoot 'Talah-Harness-1.0.0-self-signed.cer') -Force
    }
}

Write-Host 'Generating update metadata, provenance, and SHA-256 checksums...'
$metadataArguments = @{
    ArtifactDirectory = $releaseRoot
    PackagePath = $package.PackagePath
    Publisher = $package.Publisher
    Signed = [bool]$package.Signed
    SigningLevel = $signingLevel
}
if ($UpdateBaseUri) { $metadataArguments.UpdateBaseUri = $UpdateBaseUri }
& (Join-Path $PSScriptRoot 'New-ReleaseMetadata.ps1') @metadataArguments | Out-Null

& (Join-Path $PSScriptRoot 'Test-ReleaseConfiguration.ps1') -RepositoryRoot $repositoryRoot -ArtifactDirectory $releaseRoot

Write-Host ''
Write-Host "Release artifacts: $releaseRoot"
if (-not $package.Signed) {
    Write-Warning 'The package is an UNSIGNED DEVELOPMENT ARTIFACT. It is not a public release and cannot use the HTTPS update channel.'
}

[pscustomobject]@{
    ReleaseDirectory = $releaseRoot
    PackagePath = $package.PackagePath
    Signed = $package.Signed
    ChecksumPath = (Join-Path $releaseRoot 'SHA256SUMS')
    SbomPath = (Join-Path $metadataDirectory 'sbom.cdx.json')
}
