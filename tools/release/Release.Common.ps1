Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$script:ProductName = 'Talah Harness'
$script:ReleaseVersion = '1.0.0'
$script:PackageVersion = '1.0.0.0'
$script:PackageIdentityName = 'BeijingKeEntropy.TalahHarness'
$script:Architecture = 'x64'
$script:RuntimeIdentifier = 'win-x64'
$script:MinimumWindowsVersion = '10.0.19041.0'
$script:DeveloperPublisher = 'CN=Talah Harness Development'
$script:CycloneDxToolVersion = '6.2.0'

function Get-RepositoryRoot {
    $root = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')
    return $root.Path
}

function Assert-RepositoryChildPath {
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot
    )

    $rootFull = [System.IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/')
    $pathFull = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $prefix = $rootFull + [System.IO.Path]::DirectorySeparatorChar
    if (-not $pathFull.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate outside the repository: $pathFull"
    }
    if ($pathFull -eq $rootFull) {
        throw 'Refusing to operate on the repository root.'
    }
    return $pathFull
}

function Reset-RepositoryDirectory {
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot
    )

    $safePath = Assert-RepositoryChildPath -Path $Path -RepositoryRoot $RepositoryRoot
    if (Test-Path -LiteralPath $safePath) {
        $item = Get-Item -LiteralPath $safePath -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to remove reparse point: $safePath"
        }
        Remove-Item -LiteralPath $safePath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $safePath -Force | Out-Null
    return $safePath
}

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory = $true)] [string] $FilePath,
        [Parameter()] [string[]] $ArgumentList = @(),
        [Parameter()] [string] $FailureMessage = 'Native command failed.'
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage Exit code: $LASTEXITCODE"
    }
}

function Get-WindowsSdkTool {
    param([Parameter(Mandatory = $true)] [ValidateSet('makeappx.exe', 'signtool.exe')] [string] $Name)

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (-not (Test-Path -LiteralPath $kitsRoot)) {
        throw "Windows SDK tool '$Name' is unavailable. Install the Windows 10/11 SDK with Desktop C++ tools. Expected root: $kitsRoot"
    }

    $matches = @(Get-ChildItem -LiteralPath $kitsRoot -Recurse -Filter $Name -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '[\\/]x64[\\/]' } |
        Sort-Object FullName -Descending)
    if ($matches.Count -eq 0) {
        throw "Windows SDK tool '$Name' is unavailable under $kitsRoot. Install the Windows 10/11 SDK App Certification Kit tools."
    }
    return $matches[0].FullName
}

function ConvertTo-XmlAttributeValue {
    param([Parameter(Mandatory = $true)] [string] $Value)
    return [System.Security.SecurityElement]::Escape($Value)
}

function Get-GitValue {
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [string[]] $Arguments
    )
    $output = & git -C $RepositoryRoot @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Git command failed: git $($Arguments -join ' ')"
    }
    return ($output | Out-String).Trim()
}

function Get-SourceDateUtc {
    param([Parameter(Mandatory = $true)] [string] $RepositoryRoot)
    $epochText = Get-GitValue -RepositoryRoot $RepositoryRoot -Arguments @('show', '-s', '--format=%ct', 'HEAD')
    $epoch = [long]::Parse($epochText, [System.Globalization.CultureInfo]::InvariantCulture)
    return [DateTimeOffset]::FromUnixTimeSeconds($epoch).UtcDateTime
}
