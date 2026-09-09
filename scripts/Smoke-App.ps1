param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$process = Start-Process -FilePath (Resolve-Path $Executable) -PassThru
try {
    if (-not $process.WaitForInputIdle(20000)) { throw 'WPF window did not become idle.' }
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $process.Refresh()
        if ($process.HasExited) { throw "WPF exited before showing a window: $($process.ExitCode)" }
        if ($process.MainWindowHandle -ne 0) { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($process.MainWindowHandle -eq 0) { throw 'WPF window not found within 20 seconds.' }
    if ($process.MainWindowTitle -notlike 'SafeFileSync*') { throw 'Unexpected WPF title.' }
    Write-Output "WPF launch smoke passed: $($process.MainWindowTitle)"
    $null = $process.CloseMainWindow()
    if (-not $process.WaitForExit(10000)) { throw 'WPF did not exit normally.' }
    if ($process.ExitCode -ne 0) { throw "WPF exited with $($process.ExitCode)." }
} finally {
    if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
}
