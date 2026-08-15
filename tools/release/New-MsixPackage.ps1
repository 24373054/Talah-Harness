[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $PublishDirectory,
    [Parameter(Mandatory = $true)] [string] $OutputDirectory,
    [Parameter()] [string] $Publisher = 'CN=Talah Harness Development',
    [Parameter()] [string] $PackageVersion = '1.0.0.0',
    [Parameter()] [string] $CertificatePath,
    [Parameter()] [SecureString] $CertificatePassword,
    [Parameter()] [string] $TimestampUrl = 'http://timestamp.digicert.com'
)

. (Join-Path $PSScriptRoot 'Release.Common.ps1')

$repositoryRoot = Get-RepositoryRoot
$publishPath = (Resolve-Path -LiteralPath $PublishDirectory).Path
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
Assert-RepositoryChildPath -Path $publishPath -RepositoryRoot $repositoryRoot | Out-Null
Assert-RepositoryChildPath -Path $outputPath -RepositoryRoot $repositoryRoot | Out-Null

if (-not (Test-Path -LiteralPath (Join-Path $publishPath 'Talah.Harness.App.exe'))) {
    throw "Publish output does not contain Talah.Harness.App.exe: $publishPath"
}
if ($PackageVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "MSIX package version must have four numeric components: $PackageVersion"
}
if ([string]::IsNullOrWhiteSpace($Publisher) -or $Publisher -notmatch '(^|,)\s*(CN|O|OU|L|S|C)=') {
    throw 'Publisher must be an X.500 distinguished name that exactly matches the signing certificate subject.'
}
if ($CertificatePath -and $null -eq $CertificatePassword) {
    throw 'CertificatePassword is required when CertificatePath is supplied.'
}
if ($CertificatePath -and -not (Test-Path -LiteralPath $CertificatePath -PathType Leaf)) {
    throw "Signing certificate not found: $CertificatePath"
}

$stagingPath = Reset-RepositoryDirectory -Path (Join-Path $repositoryRoot 'build\release-staging\msix-payload') -RepositoryRoot $repositoryRoot
Copy-Item -Path (Join-Path $publishPath '*') -Destination $stagingPath -Recurse -Force

$manifestTemplate = Get-Content -LiteralPath (Join-Path $repositoryRoot 'installer\msix\AppxManifest.xml.in') -Raw
$manifest = $manifestTemplate.Replace('@@IDENTITY_NAME@@', (ConvertTo-XmlAttributeValue $script:PackageIdentityName))
$manifest = $manifest.Replace('@@PUBLISHER@@', (ConvertTo-XmlAttributeValue $Publisher))
$manifest = $manifest.Replace('@@PACKAGE_VERSION@@', (ConvertTo-XmlAttributeValue $PackageVersion))
$manifestPath = Join-Path $stagingPath 'AppxManifest.xml'
[System.IO.File]::WriteAllText($manifestPath, $manifest, [System.Text.UTF8Encoding]::new($false))

$sourceDate = Get-SourceDateUtc -RepositoryRoot $repositoryRoot
Get-ChildItem -LiteralPath $stagingPath -Recurse -Force | ForEach-Object {
    $_.LastWriteTimeUtc = $sourceDate
}

New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$signed = -not [string]::IsNullOrWhiteSpace($CertificatePath)
$suffix = if ($signed) { '' } else { '_unsigned-development' }
$packageName = "Talah-Harness_${PackageVersion}_x64${suffix}.msix"
$packagePath = Join-Path $outputPath $packageName
if (Test-Path -LiteralPath $packagePath) {
    Remove-Item -LiteralPath $packagePath -Force
}

$makeAppx = Get-WindowsSdkTool -Name 'makeappx.exe'
$makeAppxOutput = @(Invoke-NativeCommand -FilePath $makeAppx -ArgumentList @('pack', '/d', $stagingPath, '/p', $packagePath, '/o') -FailureMessage 'makeappx failed to build the MSIX package.')
$makeAppxOutput | Select-Object -Last 5 | Out-Host
Set-ZipEntryTimestamps -Path $packagePath -TimestampUtc $sourceDate

if ($signed) {
    $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
        (Resolve-Path -LiteralPath $CertificatePath).Path,
        $CertificatePassword,
        [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    $signerThumbprint = $null
    $selfSignedCertificate = $false
    try {
        if ($certificate.Subject -ne $Publisher) {
            throw "Signing certificate subject '$($certificate.Subject)' does not exactly match manifest Publisher '$Publisher'."
        }
        if (-not $certificate.HasPrivateKey) {
            throw 'Signing certificate does not contain a private key.'
        }
        $signerThumbprint = $certificate.Thumbprint
        $selfSignedCertificate = $certificate.Issuer -eq $certificate.Subject
    }
    finally {
        $certificate.Dispose()
    }

    $signTool = Get-WindowsSdkTool -Name 'signtool.exe'
    $passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($CertificatePassword)
    $plainPassword = $null
    try {
        $plainPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)
        & $signTool sign /fd SHA256 /f (Resolve-Path -LiteralPath $CertificatePath).Path /p $plainPassword /tr $TimestampUrl /td SHA256 $packagePath | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "signtool failed to sign the MSIX package. Exit code: $LASTEXITCODE"
        }
    }
    finally {
        $plainPassword = $null
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer)
    }

    $verifyOutput = @(& $signTool verify /pa /v $packagePath 2>&1)
    $verifyExitCode = $LASTEXITCODE
    $verifyText = $verifyOutput -join [Environment]::NewLine
    $signerObserved = $null -ne $signerThumbprint -and $verifyText.Contains($signerThumbprint, [System.StringComparison]::OrdinalIgnoreCase)
    $selfSignedDevelopmentSignature = $selfSignedCertificate -and $signerObserved
    if ($verifyExitCode -eq 0) {
        $verifyOutput | Select-Object -Last 5 | Out-Host
    }
    elseif ($selfSignedDevelopmentSignature) {
        Write-Warning 'The MSIX signature is present and cryptographically valid but its self-signed root is not trusted by this machine. This is an expected development-grade self-signed release; consumers must trust the published .cer certificate before installation.'
        $verifyOutput | Select-Object -Last 8 | Out-Host
    }
    else {
        $verifyOutput | Out-Host
        throw "Authenticode verification failed for the signed MSIX. Exit code: $verifyExitCode"
    }
}

[pscustomobject]@{
    PackagePath = $packagePath
    PackageFileName = $packageName
    Signed = $signed
    Publisher = $Publisher
    IdentityName = $script:PackageIdentityName
    PackageVersion = $PackageVersion
}
