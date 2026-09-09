param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$process = Start-Process -FilePath (Resolve-Path $Executable) -PassThru
try {
    if (-not $process.WaitForInputIdle(20000)) { throw 'WPF window did not become idle.' }
    $process.Refresh()
    if ($process.HasExited -or $process.MainWindowHandle -eq 0) { throw 'WPF window not found.' }
    if ($process.MainWindowTitle -notlike 'SafeFileSync*') { throw 'Unexpected WPF title.' }
    Write-Output "WPF launch smoke passed: $($process.MainWindowTitle)"
    $null = $process.CloseMainWindow()
    if (-not $process.WaitForExit(10000)) { throw 'WPF did not exit normally.' }
    if ($process.ExitCode -ne 0) { throw "WPF exited with $($process.ExitCode)." }
} finally {
    if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
}
