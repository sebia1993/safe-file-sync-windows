param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class SfsWindowCapture {
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
}
'@
$fixture = Join-Path $env:RUNNER_TEMP ('SfsUI-' + [guid]::NewGuid().ToString('N'))
$src = Join-Path $fixture 'source'
$dst = Join-Path $fixture 'destination'
New-Item -ItemType Directory -Path $src,$dst | Out-Null
[IO.File]::WriteAllText((Join-Path $src 'example.txt'),'UI transfer source')
$process = Start-Process -FilePath (Resolve-Path $Executable) -PassThru
try {
 $deadline = [DateTime]::UtcNow.AddSeconds(30)
 do { $process.Refresh(); if ($process.HasExited) { throw 'App exited during UI startup.' }; if ($process.MainWindowHandle -ne 0) { break }; Start-Sleep -Milliseconds 200 } while ([DateTime]::UtcNow -lt $deadline)
 if ($process.MainWindowHandle -eq 0) { throw 'No app window.' }
 $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
 function Find-Element([string]$name,[bool]$id=$false) {
  $property = [System.Windows.Automation.AutomationElement]::NameProperty
  if ($id) { $property = [System.Windows.Automation.AutomationElement]::AutomationIdProperty }
  $condition = New-Object System.Windows.Automation.PropertyCondition($property,$name)
  $element = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
  if ($null -eq $element) { throw "UI element not found: $name" }; return $element
 }
 foreach ($pair in @(@('원본 폴더 경로',$src),@('목적지 폴더 경로',$dst))) {
  $element = Find-Element $pair[0]
  $pattern = $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
  $pattern.SetValue($pair[1])
 }
 function Invoke-Button([string]$name) { $element = Find-Element $name; $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
 function Wait-Status([string]$expected) {
  $end = [DateTime]::UtcNow.AddSeconds(40)
  do {
   $status = (Find-Element 'JobStatus' $true).Current.Name
   if ($status.Contains($expected) -and (Find-Element '복사 시작').Current.IsEnabled) { return }
   if ($status.Contains('완료하지 못')) { throw $status }
   Start-Sleep -Milliseconds 200
  } while ([DateTime]::UtcNow -lt $end)
  throw "UI did not reach '$expected': $status"
 }
 Invoke-Button '폴더 비교'; Wait-Status '비교 완료'
 if (Test-Path (Join-Path $dst 'example.txt')) { throw 'Compare unexpectedly copied source.' }
 Invoke-Button '복사 시작'; Wait-Status '작업: 완료'
 if ([IO.File]::ReadAllText((Join-Path $dst 'example.txt')) -ne 'UI transfer source') { throw 'UI copy content differs.' }
 if ([IO.File]::ReadAllText((Join-Path $src 'example.txt')) -ne 'UI transfer source') { throw 'UI modified source.' }
 $summary = (Find-Element 'JobSummary' $true).Current.Name
 if (-not $summary.Contains('전체 검증 미실시')) { throw "Quick verification mislabeled: $summary" }
 Write-Output "WPF UI compare/copy flow passed. $summary"
 Start-Sleep -Milliseconds 300
 $rect = New-Object SfsWindowCapture+RECT
 if (-not [SfsWindowCapture]::GetWindowRect($process.MainWindowHandle,[ref]$rect)) { throw 'Could not get window bounds.' }
 $bitmap = New-Object System.Drawing.Bitmap(($rect.Right-$rect.Left),($rect.Bottom-$rect.Top))
 $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
 $dc = $graphics.GetHdc()
 try { if (-not [SfsWindowCapture]::PrintWindow($process.MainWindowHandle,$dc,2)) { throw 'Window capture failed.' } } finally { $graphics.ReleaseHdc($dc) }
 New-Item -ItemType Directory -Path artifacts/ui -Force | Out-Null
 $bitmap.Save((Join-Path (Get-Location) 'artifacts/ui/wpf-copy-result.png'),[System.Drawing.Imaging.ImageFormat]::Png)
 $graphics.Dispose(); $bitmap.Dispose()
 $null = $process.CloseMainWindow()
 if (-not $process.WaitForExit(10000)) { throw 'UI close failed.' }
} finally {
 if (-not $process.HasExited) { & taskkill /PID $process.Id /T /F | Out-Null }
 Remove-Item $fixture -Recurse -Force
}
