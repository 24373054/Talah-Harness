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

function Set-ZipEntryTimestamps {
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [DateTime] $TimestampUtc
    )

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 22) { throw "ZIP file is too short: $Path" }

    $year = [Math]::Max(1980, [Math]::Min(2107, $TimestampUtc.Year))
    $dosDate = (($year - 1980) -shl 9) -bor ($TimestampUtc.Month -shl 5) -bor $TimestampUtc.Day
    $dosTime = ($TimestampUtc.Hour -shl 11) -bor ($TimestampUtc.Minute -shl 5) -bor [Math]::Floor($TimestampUtc.Second / 2)

    $minimumEocd = [Math]::Max(0, $bytes.Length - 65557)
    $eocd = -1
    for ($index = $bytes.Length - 22; $index -ge $minimumEocd; $index--) {
        if ([BitConverter]::ToUInt32($bytes, $index) -eq 0x06054b50) { $eocd = $index; break }
    }
    if ($eocd -lt 0) { throw "ZIP end-of-central-directory record was not found: $Path" }

    [uint64]$entryCount = [BitConverter]::ToUInt16($bytes, $eocd + 10)
    [uint64]$centralOffset = [BitConverter]::ToUInt32($bytes, $eocd + 16)
    if ($entryCount -eq [uint16]::MaxValue -or $centralOffset -eq [uint32]::MaxValue) {
        $locator = $eocd - 20
        if ($locator -lt 0 -or [BitConverter]::ToUInt32($bytes, $locator) -ne 0x07064b50) {
            throw "Zip64 locator was not found: $Path"
        }
        $zip64Eocd = [BitConverter]::ToUInt64($bytes, $locator + 8)
        if ($zip64Eocd + 56 -gt $bytes.Length -or [BitConverter]::ToUInt32($bytes, [int]$zip64Eocd) -ne 0x06064b50) {
            throw "Zip64 end-of-central-directory record is malformed: $Path"
        }
        $entryCount = [BitConverter]::ToUInt64($bytes, [int]$zip64Eocd + 32)
        $centralOffset = [BitConverter]::ToUInt64($bytes, [int]$zip64Eocd + 48)
    }
    if ($entryCount -gt [int]::MaxValue -or $centralOffset -gt [int]::MaxValue) {
        throw 'The package is too large for in-memory deterministic timestamp normalization.'
    }

    $central = [int64]$centralOffset
    for ($entry = 0; $entry -lt $entryCount; $entry++) {
        if ($central + 46 -gt $bytes.Length -or [BitConverter]::ToUInt32($bytes, [int]$central) -ne 0x02014b50) {
            throw "Malformed ZIP central-directory entry $entry in $Path"
        }
        [uint64]$localOffset = [BitConverter]::ToUInt32($bytes, [int]$central + 42)
        $nameLength = [BitConverter]::ToUInt16($bytes, [int]$central + 28)
        $extraLength = [BitConverter]::ToUInt16($bytes, [int]$central + 30)
        $commentLength = [BitConverter]::ToUInt16($bytes, [int]$central + 32)
        if ($localOffset -eq [uint32]::MaxValue) {
            $extra = [int]$central + 46 + $nameLength
            $extraEnd = $extra + $extraLength
            $zip64Data = -1
            while ($extra + 4 -le $extraEnd) {
                $headerId = [BitConverter]::ToUInt16($bytes, $extra)
                $dataSize = [BitConverter]::ToUInt16($bytes, $extra + 2)
                if ($extra + 4 + $dataSize -gt $extraEnd) { throw "Malformed ZIP extra field for entry $entry in $Path" }
                if ($headerId -eq 0x0001) { $zip64Data = $extra + 4; break }
                $extra += 4 + $dataSize
            }
            if ($zip64Data -lt 0) { throw "Zip64 local-header offset is absent for entry $entry in $Path" }
            $cursor = $zip64Data
            if ([BitConverter]::ToUInt32($bytes, [int]$central + 24) -eq [uint32]::MaxValue) { $cursor += 8 }
            if ([BitConverter]::ToUInt32($bytes, [int]$central + 20) -eq [uint32]::MaxValue) { $cursor += 8 }
            $localOffset = [BitConverter]::ToUInt64($bytes, $cursor)
        }
        if ($localOffset + 30 -gt $bytes.Length -or [BitConverter]::ToUInt32($bytes, [int]$localOffset) -ne 0x04034b50) {
            throw "Malformed ZIP local header for entry $entry in $Path"
        }

        [BitConverter]::GetBytes([uint16]$dosTime).CopyTo($bytes, [int]$central + 12)
        [BitConverter]::GetBytes([uint16]$dosDate).CopyTo($bytes, [int]$central + 14)
        [BitConverter]::GetBytes([uint16]$dosTime).CopyTo($bytes, [int]$localOffset + 10)
        [BitConverter]::GetBytes([uint16]$dosDate).CopyTo($bytes, [int]$localOffset + 12)

        $central += 46 + $nameLength + $extraLength + $commentLength
    }

    [System.IO.File]::WriteAllBytes($Path, $bytes)
}
