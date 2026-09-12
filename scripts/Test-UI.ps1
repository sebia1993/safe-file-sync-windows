param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or [Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
 throw 'This UI fixture may use the real default records folder only on an ephemeral GitHub-hosted Windows runner.'
}
$defaultParent = [IO.Path]::GetPathRoot([Environment]::SystemDirectory)
$defaultRecordRoot = Join-Path $defaultParent 'SafeFileSync'
function Assert-DefaultRecordsAbsent {
 try { $null = [IO.File]::GetAttributes($defaultRecordRoot) }
 catch {
  if ($_.Exception.GetBaseException() -is [IO.FileNotFoundException]) { return }
  throw
 }
 throw 'The default record path already exists. This fixture will not modify or delete it.'
}
# Prove absence before the fixture cleanup scope, including any existing file or reparse entry.
Assert-DefaultRecordsAbsent
if ([IO.Path]::GetPathRoot($env:RUNNER_TEMP) -eq $defaultParent) { throw 'The hosted fixture must use a different drive from the Windows system drive.' }
$mayCreateDefaultRecords = $false
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
New-Item -ItemType Directory -Path $src,$srcB,$dst | Out-Null
[IO.File]::WriteAllText((Join-Path $src 'example.txt'),'UI transfer source A')
[IO.File]::WriteAllText((Join-Path $srcB 'example.txt'),'UI transfer source B')
New-Item -ItemType Directory -Path (Join-Path $src 'nested') | Out-Null
[IO.File]::WriteAllText((Join-Path $src 'nested/child.txt'),'nested source')
$sourceSnapshot = @{}
foreach ($file in @(Get-ChildItem -LiteralPath @($src,$srcB) -File -Recurse)) {
 $sourceSnapshot[$file.FullName] = @((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash,$file.LastWriteTimeUtc.Ticks,$file.CreationTimeUtc.Ticks,$file.Attributes.ToString()) -join ':'
}
function Assert-SourcesUnchanged {
 $files = @(Get-ChildItem -LiteralPath @($src,$srcB) -File -Recurse)
 if ($files.Count -ne $sourceSnapshot.Count) { throw 'The UI changed the number of source files.' }
 foreach ($file in $files) {
  $value = @((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash,$file.LastWriteTimeUtc.Ticks,$file.CreationTimeUtc.Ticks,$file.Attributes.ToString()) -join ':'
  if (-not $sourceSnapshot.ContainsKey($file.FullName) -or $sourceSnapshot[$file.FullName] -cne $value) { throw 'The UI changed a source file or its metadata.' }
 }
}
function Wait-AppWindow([Diagnostics.Process]$app) {
 $deadline = [DateTime]::UtcNow.AddSeconds(30)
 do { $app.Refresh(); if ($app.HasExited) { throw 'App exited during UI startup.' }; if ($app.MainWindowHandle -ne 0) { return [System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle) }; Start-Sleep -Milliseconds 200 } while ([DateTime]::UtcNow -lt $deadline)
 throw 'No app window.'
}
$process = $null
try {
 $process = Start-Process -FilePath (Resolve-Path $Executable) -PassThru
 $window = Wait-AppWindow $process
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
 function Assert-RecordsUIHidden {
  $forbiddenIds = @('StoragePath','StorageActualPath','ResetStorageLocation')
  foreach ($element in $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)) {
   $name = $element.Current.Name
   if ($forbiddenIds -contains $element.Current.AutomationId -or $name.Contains('기록 위치') -or $name.Contains('작업 기록 기준 폴더') -or $name.Contains('실제 기록 폴더') -or $name.Contains('사용자 지정 위치') -or $name -eq '기본 위치' -or $name.Contains($defaultRecordRoot)) {
    throw 'The automation tree still exposes a record-location control, label, preview or internal record path.'
   }
  }
 }
 function Wait-Code([string]$expected) {
  $started = [DateTime]::UtcNow
  $end = $started.AddSeconds(40)
  do {
   $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'SupportCode')
   $element = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
   if ($null -ne $element -and $element.Current.Name -ceq $expected -and (Find-Element '복사 시작').Current.IsEnabled -and [DateTime]::UtcNow -gt $started.AddMilliseconds(500)) { return }
   Start-Sleep -Milliseconds 100
  } while ([DateTime]::UtcNow -lt $end)
  throw "The app did not reach the expected support code $expected."
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
  foreach ($privateValue in @($src,$srcB,$dst,$defaultRecordRoot,'example.txt','UI transfer source')) {
   if ($copied.Contains($privateValue)) { throw 'Clipboard included fixture data instead of a code only.' }
  }
 }
 Assert-RecordsUIHidden
 Assert-DefaultRecordsAbsent
 Write-Output 'Record-location controls are absent and startup/history loading did not create the default records folder.'
 Invoke-Button 'RemoveSource_1' $true
 Invoke-Button '복사 시작'; Wait-Code 'S01'
 Assert-Code 'S01'
 Invoke-Button 'AddSource' $true
 Set-Value 'SourcePath_1' $src $true
 Invoke-Button 'AddSource' $true
 Set-Value 'SourcePath_2' $srcB $true
 if ((Read-Value 'SourceName_1') -ne 'source' -or (Read-Value 'SourceName_2') -ne 'source') { throw 'Source folder names were not suggested from their paths.' }
 Set-Value 'SourceName_1' 'A' $true
 Set-Value 'SourceName_2' 'B' $true
 Set-Value '목적지 폴더 경로' $dst
 foreach ($pair in @(@('SourceMapping_1',(Join-Path $dst 'A')),@('SourceMapping_2',(Join-Path $dst 'B')))) {
  if (-not (Find-Element $pair[0] $true).Current.Name.Contains($pair[1])) { throw 'Destination mapping preview does not match configured folder name.' }
 }
 # The other-drive first source remains valid; a system-drive-root second source must fail before scanning or creating records.
 Set-Value 'SourcePath_2' $defaultParent $true
 Set-Value 'SourceName_2' 'B' $true
 Invoke-Button '복사 시작'; Wait-Code 'S03'
 Assert-Code 'S03'; Assert-CodeClipboard 'S03'
 Assert-RecordsUIHidden
 Assert-DefaultRecordsAbsent
 Assert-SourcesUnchanged
 # A second rejected start must replace, rather than retain, the previous diagnostic.
 Set-Value 'SourceName_1' '' $true
 Invoke-Button '복사 시작'; Wait-Code 'S01'
 Assert-Code 'S01'
 Set-Value 'SourceName_1' 'A' $true
 Assert-DefaultRecordsAbsent
 if (@(Get-ChildItem -LiteralPath $dst -Force).Count -ne 0) { throw 'Rejected job unexpectedly wrote to the destination.' }
 Set-Value 'SourcePath_2' $srcB $true
 Set-Value 'SourceName_2' 'B' $true
 $mayCreateDefaultRecords = $true
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
 $recordRoot = $defaultRecordRoot
 if (@(Get-ChildItem $recordRoot -Filter '*.sqlite').Count -ne 2) { throw 'Comparison and copy did not create exactly two jobs in the automatic default records.' }
 if (@(Get-ChildItem $recordRoot -Filter '*.html').Count -ne 1) { throw 'Copy did not create exactly one report in the automatic default records.' }
 Assert-SourcesUnchanged
 Assert-RecordsUIHidden
 # Close the first instance and load its default history automatically in a fresh process.
 $null = $process.CloseMainWindow()
 if (-not $process.WaitForExit(10000)) { throw 'The app did not close before history restart.' }
 $process.Dispose(); $process = $null
 $process = Start-Process -FilePath (Resolve-Path $Executable) -PassThru
 $window = Wait-AppWindow $process
 Assert-RecordsUIHidden
 Assert-NoCode
 $selection = (Find-Element 'JobHistory' $true).GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
 if ($selection.Length -ne 1) { throw 'A fresh app did not automatically load and select its default job history.' }
 Set-Value 'SourcePath_1' $srcB $true
 Set-Value 'SourceName_1' 'Changed-A' $true
 Invoke-Button '선택 작업 재개 / 재검사·재시도'; Wait-Status '작업: 완료'
 Assert-NoCode
 if ((Read-Value 'SourcePath_1') -ne $src -or (Read-Value 'SourcePath_2') -ne $srcB -or (Read-Value 'SourceName_1') -ne 'A' -or (Read-Value 'SourceName_2') -ne 'B') { throw 'Resume did not restore every persisted source path and folder name.' }
 if (Test-Path (Join-Path $dst 'Changed-A')) { throw 'Resume used an edited folder name instead of the saved mapping.' }
 if (@(Get-ChildItem $recordRoot -Filter '*.sqlite').Count -ne 2) { throw 'Resume created a different job.' }
 Assert-SourcesUnchanged
 Assert-RecordsUIHidden
 Write-Output "WPF multiple sources, aliases, separate same-named files, automatic records, safe overlap rejection, restarted history restoration and resume passed. $summary"
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
 Assert-SourcesUnchanged
 Assert-RecordsUIHidden
 Write-Output 'Support codes S01, S03 and S13, code-only clipboard, diagnostic reset and error-panel layout passed.'
 $null = $process.CloseMainWindow()
 if (-not $process.WaitForExit(10000)) { throw 'UI close failed.' }
} finally {
 if ($null -ne $process -and -not $process.HasExited) {
  & taskkill /PID $process.Id /T /F | Out-Null
  if (-not $process.WaitForExit(10000)) { throw 'The app is still running; owned fixtures were retained instead of cleaning active records.' }
 }
 # This hosted fixture proved the default path absent before any app launch; remove only its own newly created records after the app exits.
 if ($mayCreateDefaultRecords -and (Test-Path -LiteralPath $defaultRecordRoot)) { Remove-Item -LiteralPath $defaultRecordRoot -Recurse -Force }
 if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
