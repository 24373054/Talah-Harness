[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $ExecutablePath,
    [Parameter()] [ValidateRange(5, 120)] [int] $StartupTimeoutSeconds = 30,
    [Parameter()] [ValidateRange(2, 60)] [int] $ShutdownTimeoutSeconds = 10,
    [Parameter()] [string] $ExpectedWindowTitle = 'Talah Harness'
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$executable = (Resolve-Path -LiteralPath $ExecutablePath).Path
if ([System.IO.Path]::GetExtension($executable) -ne '.exe') {
    throw "Application smoke test requires an executable: $executable"
}

$process = $null
$windowObserved = $false
try {
    $process = Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable) -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($process.HasExited) {
            throw "Talah Harness exited before creating a main window. Exit code: $($process.ExitCode)"
        }
        $process.Refresh()
        if ($process.MainWindowHandle -ne [IntPtr]::Zero -and $process.MainWindowTitle -like "*$ExpectedWindowTitle*") {
            $windowObserved = $true
            break
        }
        Start-Sleep -Milliseconds 200
    }

    if (-not $windowObserved) {
        throw "Talah Harness did not expose a nonzero main-window handle with title '$ExpectedWindowTitle' within $StartupTimeoutSeconds seconds."
    }

    $handle = $process.MainWindowHandle
    $title = $process.MainWindowTitle
    if (-not $process.CloseMainWindow()) {
        throw 'Talah Harness main window was found, but a graceful close request could not be sent.'
    }
    if (-not $process.WaitForExit($ShutdownTimeoutSeconds * 1000)) {
        throw "Talah Harness did not exit within $ShutdownTimeoutSeconds seconds after a graceful close request."
    }
    if ($process.ExitCode -ne 0) {
        throw "Talah Harness returned exit code $($process.ExitCode) after graceful shutdown."
    }

    Write-Host "Application smoke test passed. MainWindowHandle=$handle; Title='$title'; ExitCode=0."
}
finally {
    if ($null -ne $process) {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            $process.WaitForExit(5000) | Out-Null
        }
        $process.Dispose()
    }
}
