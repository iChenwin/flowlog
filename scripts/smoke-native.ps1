param([switch]$Installed)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$results = Join-Path $project 'native/TestResults'
New-Item -ItemType Directory -Force -Path $results | Out-Null
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase
$fixture = Join-Path $results ('smoke-' + [guid]::NewGuid().ToString('N'))
$vault = Join-Path $fixture 'vault'
New-Item -ItemType Directory -Force -Path $vault | Out-Null
$exe = Join-Path $project 'native/out/FlowLog.Native.exe'
$install = Join-Path $fixture 'app'
$registration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\FlowLogNative'
if ($Installed) {
  if (Test-Path -LiteralPath $registration) { throw 'An existing native install is registered; use a clean test account for installer smoke.' }
  $setup = Join-Path $project 'native/packages/FlowLog-Native-0.2.0-Setup.exe'
  $installer = Start-Process -FilePath $setup -ArgumentList @('/S', ('/D=' + $install)) -WindowStyle Hidden -PassThru -Wait
  if ($installer.ExitCode -ne 0) { throw 'Installer failed' }
  $exe = Join-Path $install 'FlowLog.Native.exe'
  if (-not (Test-Path -LiteralPath $exe)) { throw 'Installed executable absent' }
}
$appProcess = $null
function Await-Condition([scriptblock]$Predicate, [string]$Description) {
  $timer = [Diagnostics.Stopwatch]::StartNew()
  do {
    $result = & $Predicate
    if ($result) { return $result }
    Start-Sleep -Milliseconds 100
  } while ($timer.Elapsed.TotalSeconds -lt 20)
  throw "Timed out: $Description"
}
function Launch-App {
  $script:appProcess = Start-Process -FilePath $exe -ArgumentList @('--vault', ('"' + $vault + '"')) -WindowStyle Hidden -PassThru
  $pidCondition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $script:appProcess.Id)
  Await-Condition { [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, $pidCondition) } 'native window'
}
function Get-Editor($window) {
  $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, 'NoteEditor')
  Await-Condition { $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition) } 'native document editor'
}
function Close-App($window) {
  $pattern = $window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)
  $pattern.Close()
  if (-not $script:appProcess.WaitForExit(20000)) { throw 'Native close did not flush and exit' }
  if ($script:appProcess.ExitCode -ne 0) { throw 'Native process returned an error' }
}
try {
  $window = Launch-App
  $editor = Get-Editor $window
  $value = $editor.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
  $content = "- TODO 原生 Windows 自动化验证`r`n  - 跨进程保存 中文与 emoji 📝`r`n- [[项目计划]]"
  $value.SetValue($content)
  $journal = Join-Path $vault ('journals/' + (Get-Date -Format 'yyyy_MM_dd') + '.md')
  Await-Condition { (Test-Path -LiteralPath $journal) -and ([IO.File]::ReadAllText($journal) -eq $content) } 'real Markdown save' | Out-Null
  $modules = @($appProcess.Modules | ForEach-Object ModuleName)
  $appProcess.Refresh()
  $memory = [ordered]@{ workingSetBytes=$appProcess.WorkingSet64; privateBytes=$appProcess.PrivateMemorySize64; context='One small fixture journal after first save; not a large-vault benchmark' }
  if (-not ($modules -contains 'PresentationFramework.ni.dll' -or $modules -contains 'PresentationFramework.dll')) { throw 'WPF module not loaded' }
  if ($modules -match '(?i)webview|libcef|electron') { throw 'Unexpected browser runtime in native process' }
  Close-App $window
  $window = Launch-App
  $editor = Get-Editor $window
  $value = $editor.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
  if ($value.Current.Value -ne $content) { throw 'Reopened native editor differs from saved file' }
  $crashContent = $content + "`r`n- 草稿恢复：终止进程前最后一次修改"
  $value.SetValue($crashContent)
  Stop-Process -Id $appProcess.Id -Force
  $appProcess.WaitForExit()
  $window = Launch-App
  $editor = Get-Editor $window
  $value = $editor.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
  if ($value.Current.Value -ne $crashContent) { throw 'Forced process termination lost the last edit' }
  Await-Condition { [IO.File]::ReadAllText($journal) -eq $crashContent } 'crash recovery to Markdown' | Out-Null
  Close-App $window
  $window = Launch-App
  $editor = Get-Editor $window
  $value = $editor.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
  $external = '- External edit must survive 冲突检测'
  [IO.File]::WriteAllText($journal, $external, [Text.UTF8Encoding]::new($false))
  $conflicting = $crashContent + "`r`n- Local conflicting draft"
  $value.SetValue($conflicting)
  $conflictDir = Join-Path $vault '.flowlog/conflicts'
  Await-Condition { (Test-Path -LiteralPath $conflictDir) -and @(Get-ChildItem -LiteralPath $conflictDir -Filter '*.local.md' -File).Count -gt 0 } 'conflict preserves both versions' | Out-Null
  if ([IO.File]::ReadAllText($journal) -ne $external) { throw 'Conflict overwrote external edit' }
  Stop-Process -Id $appProcess.Id -Force
  $appProcess.WaitForExit()
  $window = Launch-App
  $editor = Get-Editor $window
  $value = $editor.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
  if ($value.Current.Value -ne $conflicting -or [IO.File]::ReadAllText($journal) -ne $external) { throw 'Conflict recovery failed' }
  Stop-Process -Id $appProcess.Id -Force
  $appProcess.WaitForExit()
  if ($Installed) {
    $uninstaller = Start-Process -FilePath (Join-Path $install 'Uninstall.exe') -ArgumentList '/S' -WindowStyle Hidden -PassThru -Wait
    Await-Condition { -not (Test-Path -LiteralPath $exe) } 'uninstall' | Out-Null
    if ([IO.File]::ReadAllText($journal) -ne $external) { throw 'Uninstall modified the vault' }
  }
  [ordered]@{ passed=$true; installed=[bool]$Installed; os=[Environment]::OSVersion.VersionString; memory=$memory; controls='Windows UIAutomation ValuePattern and WindowPattern'; checks=@('native process launch','Unicode Markdown save','reopen exact text','no browser runtime module','close flush','forced process termination recovery','external conflict preserves both sides','conflict recovery after forced termination','uninstall keeps vault'); moduleNames=$modules; vault=$vault } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $results 'native-smoke.json') -Encoding UTF8
  Write-Output 'PASS: native executable save, restart, forced termination, external conflict and runtime inspection'
} finally {
  if ($appProcess -and -not $appProcess.HasExited) { Stop-Process -Id $appProcess.Id -ErrorAction SilentlyContinue }
}
