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
$src = Join-Path (Join-Path $fixture 'first') 'source'
$srcB = Join-Path (Join-Path $fixture 'second') 'source'
$dst = Join-Path $fixture 'destination'
$insideRecords = Join-Path $srcB 'AppData/Local'
$records = Join-Path $fixture 'records'
$emptyRecords = Join-Path $fixture 'empty-records'
New-Item -ItemType Directory -Path $src,$srcB,$dst,$insideRecords,$records,$emptyRecords | Out-Null
[IO.File]::WriteAllText((Join-Path $src 'example.txt'),'UI transfer source A')
[IO.File]::WriteAllText((Join-Path $srcB 'example.txt'),'UI transfer source B')
New-Item -ItemType Directory -Path (Join-Path $src 'nested') | Out-Null
[IO.File]::WriteAllText((Join-Path $src 'nested/child.txt'),'nested source')
$process = Start-Process -FilePath (Resolve-Path $Executable) -PassThru
try {
 $deadline = [DateTime]::UtcNow.AddSeconds(30)
 do { $process.Refresh(); if ($process.HasExited) { throw 'App exited during UI startup.' }; if ($process.MainWindowHandle -ne 0) { break }; Start-Sleep -Milliseconds 200 } while ([DateTime]::UtcNow -lt $deadline)
 if ($process.MainWindowHandle -eq 0) { throw 'No app window.' }
 $second = Start-Process -FilePath (Resolve-Path $Executable) -PassThru
 try {
  $end = [DateTime]::UtcNow.AddSeconds(20)
  do { $second.Refresh(); if ($second.MainWindowHandle -ne 0) { break }; Start-Sleep -Milliseconds 200 } while ([DateTime]::UtcNow -lt $end)
  if ($second.MainWindowTitle -ne 'SafeFileSync') { throw 'Second instance was not blocked with the expected dialog.' }
  $null = $second.CloseMainWindow()
  if (-not $second.WaitForExit(10000)) { throw 'Second instance did not exit.' }
  $process.Refresh(); if ($process.HasExited) { throw 'First instance unexpectedly exited.' }
  Write-Output 'Per-session single-instance guard passed.'
 } finally { if (-not $second.HasExited) { & taskkill /PID $second.Id /T /F | Out-Null } }
 $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
 function Find-Element([string]$name,[bool]$id=$false) {
  $property = [System.Windows.Automation.AutomationElement]::NameProperty
  if ($id) { $property = [System.Windows.Automation.AutomationElement]::AutomationIdProperty }
  $condition = New-Object System.Windows.Automation.PropertyCondition($property,$name)
  $end = [DateTime]::UtcNow.AddSeconds(10)
  do {
   $element = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
   if ($null -ne $element) { return $element }; Start-Sleep -Milliseconds 100
  } while ([DateTime]::UtcNow -lt $end)
  throw "UI element not found: $name"
 }
 function Invoke-Button([string]$name,[bool]$id=$false) { $element = Find-Element $name $id; $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
 function Set-Value([string]$name,[string]$value,[bool]$id=$false) { (Find-Element $name $id).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value) }
 function Read-Value([string]$name) { return (Find-Element $name $true).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
 function Wait-Status([string]$expected) {
  $started = [DateTime]::UtcNow
  $end = $started.AddSeconds(40)
  do {
   $status = (Find-Element 'JobStatus' $true).Current.Name
   if ($status.Contains($expected) -and (Find-Element '복사 시작').Current.IsEnabled -and [DateTime]::UtcNow -gt $started.AddMilliseconds(500)) { return }
   if (-not $status.Contains($expected) -and $status.Contains('완료하지 못') -and (Find-Element '복사 시작').Current.IsEnabled -and [DateTime]::UtcNow -gt $started.AddSeconds(1)) { throw $status }
   Start-Sleep -Milliseconds 200
  } while ([DateTime]::UtcNow -lt $end)
  throw "UI did not reach '$expected': $status"
 }
 function Assert-Code([string]$expected) {
  if ((Find-Element 'SupportCode' $true).Current.Name -cne $expected) { throw "Unexpected support code; expected $expected." }
  if (-not (Find-Element 'CopySupportCode' $true).Current.IsEnabled) { throw 'Code-only copy action is not enabled for an error.' }
 }
 function Assert-NoCode {
  $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'SupportCode')
  $codeElement = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
  if ($null -ne $codeElement -and $codeElement.Current.Name.Length -ne 0) { throw 'A stale support code remains after reset or successful work.' }
  $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'CopySupportCode')
  $copyElement = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
  if ($null -ne $copyElement -and $copyElement.Current.IsEnabled) { throw 'Code copy remained enabled without an active code.' }
 }
 function Invoke-ClipboardAccess([scriptblock]$operation) {
  $deadline = [DateTime]::UtcNow.AddSeconds(5)
  while ($true) {
   try { return (& $operation) }
   catch [System.Runtime.InteropServices.ExternalException] {
    if ([DateTime]::UtcNow -ge $deadline) { throw }
    Start-Sleep -Milliseconds 100
   }
  }
 }
 function Assert-CodeClipboard([string]$expected) {
  Invoke-ClipboardAccess { Set-Clipboard -Value 'Sfs UI clipboard sentinel' }
  Invoke-Button 'CopySupportCode' $true
  # InvokePattern returns before the app's clipboard write completes. Wait for its result first.
  $end = [DateTime]::UtcNow.AddSeconds(5)
  $completed = $false
  do {
   $hint = (Find-Element 'SupportHint' $true).Current.Name
   if ($hint.Contains('복사하지 못했습니다')) { throw 'The app reported that copying its support code failed.' }
   if ($hint.Contains('코드만 복사했습니다')) { $completed = $true; break }
   Start-Sleep -Milliseconds 100
  } while ([DateTime]::UtcNow -lt $end)
  if (-not $completed) { throw 'The app did not report completion of its code-only clipboard write.' }
  $copied = Invoke-ClipboardAccess { Get-Clipboard -Raw }
  if ($copied -cne $expected -or $copied -cnotmatch '^S(?:0[1-9]|1[0-6]|99)$') { throw 'Clipboard did not contain exactly the expected fixed support code.' }
  foreach ($privateValue in @($src,$srcB,$dst,$records,'example.txt','UI transfer source')) {
   if ($copied.Contains($privateValue)) { throw 'Clipboard included fixture data instead of a code only.' }
  }
 }
 Invoke-Button 'RemoveSource_1' $true
 Invoke-Button '복사 시작'; Wait-Status '원본 폴더를 1개 이상'
 Assert-Code 'S01'
 Invoke-Button 'AddSource' $true
 Set-Value 'SourcePath_1' $src $true
 Invoke-Button 'AddSource' $true
 Set-Value 'SourcePath_2' $srcB $true
 if ((Read-Value 'SourceName_1') -ne 'source' -or (Read-Value 'SourceName_2') -ne 'source') { throw 'Source folder names were not suggested from their paths.' }
 Set-Value 'SourceName_1' 'A' $true
 Set-Value 'SourceName_2' 'B' $true
 Set-Value '목적지 폴더 경로' $dst
 Set-Value '작업 기록 기준 폴더' $insideRecords
 foreach ($pair in @(@('SourceMapping_1',(Join-Path $dst 'A')),@('SourceMapping_2',(Join-Path $dst 'B')))) {
  if (-not (Find-Element $pair[0] $true).Current.Name.Contains($pair[1])) { throw 'Destination mapping preview does not match configured folder name.' }
 }
 Invoke-Button '복사 시작'; Wait-Status '기록 위치를'
 Assert-Code 'S03'; Assert-CodeClipboard 'S03'
 # A second rejected start must replace, rather than retain, the previous diagnostic.
 Set-Value 'SourceName_1' '' $true
 Invoke-Button '복사 시작'; Wait-Status '모든 원본 경로'
 Assert-Code 'S01'
 Set-Value 'SourceName_1' 'A' $true
 if (Test-Path (Join-Path $insideRecords 'SafeFileSync')) { throw 'Unsafe records were created within second source.' }
 if ((Test-Path (Join-Path $dst 'A')) -or (Test-Path (Join-Path $dst 'B'))) { throw 'Rejected job unexpectedly created a destination folder.' }
 $storageElement = Find-Element '작업 기록 기준 폴더'
 $storageElement.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($records)
 Assert-NoCode
 Invoke-Button '폴더 비교'; Wait-Status '비교 완료'
 Assert-NoCode
 if ((Test-Path (Join-Path $dst 'A')) -or (Test-Path (Join-Path $dst 'B'))) { throw 'Compare unexpectedly created a destination folder.' }
 Invoke-Button '복사 시작'; Wait-Status '작업: 완료'
 Assert-NoCode
 if ([IO.File]::ReadAllText((Join-Path $dst 'A/example.txt')) -ne 'UI transfer source A') { throw 'First source did not copy to its own destination folder.' }
 if ([IO.File]::ReadAllText((Join-Path $dst 'B/example.txt')) -ne 'UI transfer source B') { throw 'Second source did not copy to its own destination folder.' }
 if (Test-Path (Join-Path $dst 'example.txt')) { throw 'Multi-source copy unexpectedly used legacy direct placement.' }
 if ([IO.File]::ReadAllText((Join-Path $src 'example.txt')) -ne 'UI transfer source A' -or [IO.File]::ReadAllText((Join-Path $srcB 'example.txt')) -ne 'UI transfer source B') { throw 'UI modified a source.' }
 if ((Find-Element 'SourceCount' $true).Current.Name -ne '원본 폴더 2개') { throw 'Source count is incorrect.' }
 $summary = (Find-Element 'JobSummary' $true).Current.Name
 if (-not $summary.Contains('전체 검증 미실시')) { throw "Quick verification mislabeled: $summary" }
 $recordRoot = Join-Path $records 'SafeFileSync'
 if (@(Get-ChildItem $recordRoot -Filter '*.sqlite').Count -ne 2) { throw 'Comparison and copy jobs are not in selected records.' }
 if (@(Get-ChildItem $recordRoot -Filter '*.html').Count -ne 1) { throw 'Report is not in selected records.' }
 foreach ($choice in @($emptyRecords,$records)) {
  $storageElement.SetFocus()
  $storageElement.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($choice)
  (Find-Element 'SourcePath_1' $true).SetFocus()
  Start-Sleep -Milliseconds 200
  $selection = (Find-Element 'JobHistory' $true).GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
  if ($choice -eq $emptyRecords -and $selection.Length -ne 0) { throw 'Old history survived records change.' }
  if ($choice -eq $records -and $selection.Length -ne 1) { throw 'History was not restored from selected records.' }
 }
 Set-Value 'SourceName_1' 'Changed-A' $true
 Invoke-Button 'RemoveSource_2' $true
 Invoke-Button '선택 작업 재개 / 재검사·재시도'; Wait-Status '작업: 완료'
 Assert-NoCode
 if ((Read-Value 'SourcePath_1') -ne $src -or (Read-Value 'SourcePath_2') -ne $srcB -or (Read-Value 'SourceName_1') -ne 'A' -or (Read-Value 'SourceName_2') -ne 'B') { throw 'Resume did not restore every persisted source path and folder name.' }
 if (Test-Path (Join-Path $dst 'Changed-A')) { throw 'Resume used an edited folder name instead of the saved mapping.' }
 if (@(Get-ChildItem $recordRoot -Filter '*.sqlite').Count -ne 2) { throw 'Resume created a different job.' }
 if (Test-Path (Join-Path $insideRecords 'SafeFileSync')) { throw 'Records were written within a source.' }
 Write-Output "WPF multiple sources, aliases, separate same-named files, storage rejection, history restoration and resume passed. $summary"
 Start-Sleep -Milliseconds 300
 function Capture-Window([string]$name) {
 $rect = New-Object SfsWindowCapture+RECT
 if (-not [SfsWindowCapture]::GetWindowRect($process.MainWindowHandle,[ref]$rect)) { throw 'Could not get window bounds.' }
 $bitmap = New-Object System.Drawing.Bitmap(($rect.Right-$rect.Left),($rect.Bottom-$rect.Top))
 $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
 $dc = $graphics.GetHdc()
 try { if (-not [SfsWindowCapture]::PrintWindow($process.MainWindowHandle,$dc,2)) { throw 'Window capture failed.' } } finally { $graphics.ReleaseHdc($dc) }
 New-Item -ItemType Directory -Path artifacts/ui -Force | Out-Null
 $bitmap.Save((Join-Path (Get-Location) ('artifacts/ui/' + $name)),[System.Drawing.Imaging.ImageFormat]::Png)
 $graphics.Dispose(); $bitmap.Dispose()
 }
 if ((Find-Element 'Differences' $true).Current.BoundingRectangle.Height -lt 120) { throw 'Result table is too short to inspect at the actual window size.' }
 Capture-Window 'wpf-copy-result.png'
 $tab = Find-Element '양쪽 폴더'
 $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 foreach ($treeName in @('원본 폴더 내용','목적지 폴더 내용')) {
  $tree = Find-Element $treeName
  if ($tree.Current.BoundingRectangle.Height -lt 120) { throw "Folder tree is too short to inspect: $treeName" }
  foreach ($sourceName in @('A','B')) {
   $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,$sourceName)
   $sourceFolder = $tree.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
   if ($null -eq $sourceFolder) { throw "Source mapping $sourceName missing in $treeName" }
   $sourceFolder.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
  }
  $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,'nested')
  $folder = $tree.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
  if ($null -eq $folder) { throw "Nested folder missing in $treeName" }
  $folder.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
  $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,'child.txt')
  if ($null -eq $folder.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)) { throw "Child item missing in $treeName" }
 }
 if ([IO.File]::ReadAllText((Join-Path $dst 'A/nested/child.txt')) -ne 'nested source') { throw 'Nested UI copy content differs.' }
 Start-Sleep -Milliseconds 300
 Capture-Window 'wpf-folders.png'
 Write-Output 'Both source and destination folder trees passed UI inspection.'
 # Preserve policy should report a short conflict code without overwriting the destination.
 $conflictFile = Join-Path $dst 'A/example.txt'
 [IO.File]::WriteAllText($conflictFile,'destination conflict fixture with different length')
 Invoke-Button '복사 시작'; Wait-Status '작업: 확인 필요'
 Assert-Code 'S13'; Assert-CodeClipboard 'S13'
 if ([IO.File]::ReadAllText($conflictFile) -ne 'destination conflict fixture with different length') { throw 'Preserve policy overwrote a conflicting destination.' }
 (Find-Element '차이 및 검증 결과').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 if ((Find-Element 'Differences' $true).Current.BoundingRectangle.Height -lt 120) { throw 'Support code panel left too little room for result inspection.' }
 Capture-Window 'wpf-support-code.png'
 [IO.File]::WriteAllText($conflictFile,[IO.File]::ReadAllText((Join-Path $src 'example.txt')))
 [IO.File]::SetLastWriteTimeUtc($conflictFile,[IO.File]::GetLastWriteTimeUtc((Join-Path $src 'example.txt')))
 Invoke-Button '폴더 비교'; Wait-Status '비교 완료'
 Assert-NoCode
 Write-Output 'Support codes S01, S03 and S13, code-only clipboard, diagnostic reset and error-panel layout passed.'
 $null = $process.CloseMainWindow()
 if (-not $process.WaitForExit(10000)) { throw 'UI close failed.' }
} finally {
 if (-not $process.HasExited) { & taskkill /PID $process.Id /T /F | Out-Null }
 Remove-Item $fixture -Recurse -Force
}
