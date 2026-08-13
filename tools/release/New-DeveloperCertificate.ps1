[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $OutputPath,
    [Parameter(Mandatory = $true)] [SecureString] $Password,
    [Parameter()] [string] $Publisher = 'CN=Talah Harness Development'
)

. (Join-Path $PSScriptRoot 'Release.Common.ps1')

if ($env:OS -ne 'Windows_NT') {
    throw 'Developer certificate generation is only supported on Windows.'
}
$repositoryRoot = Get-RepositoryRoot
$fullOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
if ($fullOutputPath.StartsWith($repositoryRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Do not write developer private keys inside the repository. Choose a user-controlled directory outside the checkout.'
}
if (Test-Path -LiteralPath $fullOutputPath) {
    throw "Refusing to overwrite an existing certificate: $fullOutputPath"
}

$parent = Split-Path -Parent $fullOutputPath
if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
$certificate = New-SelfSignedCertificate -Type Custom -Subject $Publisher -KeyUsage DigitalSignature -FriendlyName 'Talah Harness local MSIX development signing' -CertStoreLocation 'Cert:\CurrentUser\My' -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3') -NotAfter (Get-Date).AddYears(1)
try {
    Export-PfxCertificate -Cert $certificate -FilePath $fullOutputPath -Password $Password -ChainOption EndEntityCertOnly | Out-Null
    $cerPath = [System.IO.Path]::ChangeExtension($fullOutputPath, '.cer')
    Export-Certificate -Cert $certificate -FilePath $cerPath -Type CERT | Out-Null
}
finally {
    Remove-Item -LiteralPath ("Cert:\CurrentUser\My\{0}" -f $certificate.Thumbprint) -Force
}

Write-Host "Created local development PFX: $fullOutputPath"
Write-Host "Created public certificate: $cerPath"
Write-Warning 'Trust the .cer only on development machines. Never commit or publish the PFX or its password.'
