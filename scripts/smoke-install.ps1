param([string]$BundleDirectory = 'src-tauri/target/release/bundle/nsis')
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or -not $env:RUNNER_TEMP) { throw 'CI-only native test; personal vaults must not be used.' }
$installer = Get-ChildItem -LiteralPath $BundleDirectory -Filter '*.exe' -File | Select-Object -First 1
if (-not $installer) { throw 'No NSIS installer found' }
$testRoot = Join-Path $env:RUNNER_TEMP ('flowlog-smoke-' + [guid]::NewGuid().ToString('N'))
$installDir = Join-Path $testRoot 'app'
$vault = Join-Path $testRoot 'vault'
$remembered = Join-Path $env:APPDATA 'FlowLog/last-vault.txt'
$previousSelection = if (Test-Path -LiteralPath $remembered) { [System.IO.File]::ReadAllBytes($remembered) } else { $null }
$previousArguments = $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS
$previousUserData = $env:WEBVIEW2_USER_DATA_FOLDER
$app = $null
$exe = $null
$policyPath = 'HKLM:\SOFTWARE\Policies\Microsoft\Edge\WebView2\AdditionalBrowserArguments'
$previousPolicy = $null
$policyHadValue = $false
function Close-SmokeApp {
  if ($app) {
    if (-not $app.WaitForExit(15000)) { throw 'Normal app close timed out; pending saves may be blocking exit.' }
    if ($app.ExitCode -ne 0) { throw "Application exited with $($app.ExitCode)" }
  }
}
try {
  New-Item -ItemType Directory -Force -Path $installDir, (Join-Path $vault 'journals'), (Join-Path $vault 'pages'), (Split-Path -Parent $remembered) | Out-Null
  $journal = Join-Path $vault ('journals/' + (Get-Date -Format 'yyyy_MM_dd') + '.md')
  [System.IO.File]::WriteAllText($journal, "- 原生桥接测试初始记录`n", [System.Text.UTF8Encoding]::new($false))
  [System.IO.File]::WriteAllText($remembered, $vault, [System.Text.UTF8Encoding]::new($false))
  # CI-only overrides; restored below. No product debug setting is enabled.
  $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = '--remote-debugging-port=9222'
  $env:WEBVIEW2_USER_DATA_FOLDER = Join-Path $testRoot 'webview-profile'
  # NSIS /D must be last and unquoted.
  $install = Start-Process -FilePath $installer.FullName -ArgumentList @('/S', "/D=$installDir") -WindowStyle Hidden -Wait -PassThru
  if ($install.ExitCode -ne 0) { throw "Installer exited with $($install.ExitCode)" }
  $exe = Get-ChildItem -LiteralPath $installDir -Filter '*.exe' -File | Where-Object { $_.Name -notmatch 'uninstall' } | Select-Object -First 1
  if (-not $exe) { throw 'Installed application executable missing' }
  # WebView2 150+ ignores environment flags in elevated CI hosts. Microsoft
  # documents per-app HKLM policy as the supported channel in that situation.
  New-Item -Path $policyPath -Force | Out-Null
  $policyValues = Get-ItemProperty -LiteralPath $policyPath
  $policyHadValue = $null -ne $policyValues.PSObject.Properties[$exe.Name]
  if ($policyHadValue) { $previousPolicy = $policyValues.($exe.Name) }
  New-ItemProperty -LiteralPath $policyPath -Name $exe.Name -Value '--remote-debugging-port=9222' -PropertyType String -Force | Out-Null
  foreach ($phase in @('write', 'reopen-conflict')) {
    Write-Output "Native WebView2 phase: $phase"
    $app = Start-Process -FilePath $exe.FullName -WindowStyle Hidden -PassThru
    & node scripts/native-smoke.mjs $phase $vault $journal
    if ($LASTEXITCODE -ne 0) { throw "Native phase '$phase' failed with exit code $LASTEXITCODE" }
    Close-SmokeApp
    $app = $null
    Start-Sleep -Seconds 2
  }
  Write-Output 'Native bridge passed: Chinese Markdown save, reopen, external conflict and both-version preservation.'
} finally {
  if ($app -and -not $app.HasExited) {
    $null = $app.CloseMainWindow()
    if (-not $app.WaitForExit(5000)) { Stop-Process -Id $app.Id -Force }
  }
  $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = $previousArguments
  $env:WEBVIEW2_USER_DATA_FOLDER = $previousUserData
  if ($exe) {
    if ($policyHadValue) { Set-ItemProperty -LiteralPath $policyPath -Name $exe.Name -Value $previousPolicy }
    else { Remove-ItemProperty -LiteralPath $policyPath -Name $exe.Name -ErrorAction SilentlyContinue }
  }
  if ($null -ne $previousSelection) { [System.IO.File]::WriteAllBytes($remembered, [byte[]]$previousSelection) }
  elseif (Test-Path -LiteralPath $remembered) { Remove-Item -LiteralPath $remembered }
  if ($exe) {
    $uninstaller = Get-ChildItem -LiteralPath $installDir -Filter '*uninstall*.exe' -File | Select-Object -First 1
    if (-not $uninstaller) { throw 'Uninstaller missing' }
    $uninstall = Start-Process -FilePath $uninstaller.FullName -ArgumentList @('/S', "_?=$installDir") -WindowStyle Hidden -Wait -PassThru
    if ($uninstall.ExitCode -ne 0) { throw "Uninstaller exited with $($uninstall.ExitCode)" }
    if (Test-Path -LiteralPath $exe.FullName) { throw 'Application executable remained after uninstall' }
    Write-Output 'Uninstall passed; isolated vault retained for diagnostics.'
  }
}
