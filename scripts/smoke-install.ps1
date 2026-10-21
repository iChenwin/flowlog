param([string]$BundleDirectory = 'src-tauri/target/release/bundle/nsis')
$ErrorActionPreference = 'Stop'
$installer = Get-ChildItem -LiteralPath $BundleDirectory -Filter '*.exe' -File | Select-Object -First 1
if (-not $installer) { throw 'No NSIS installer found' }
$installDir = Join-Path $env:RUNNER_TEMP ('flowlog-smoke-' + [guid]::NewGuid().ToString('N'))
if (-not $env:RUNNER_TEMP) { throw 'RUNNER_TEMP is required for the CI smoke test' }
$app = $null
try {
  # NSIS requires /D to be the last argument and its value to be unquoted.
  $install = Start-Process -FilePath $installer.FullName -ArgumentList @('/S', "/D=$installDir") -WindowStyle Hidden -Wait -PassThru
  if ($install.ExitCode -ne 0) { throw "Installer exited with $($install.ExitCode)" }
  $exe = Get-ChildItem -LiteralPath $installDir -Filter '*.exe' -File | Where-Object { $_.Name -notmatch 'uninstall' } | Select-Object -First 1
  if (-not $exe) { throw 'Installed application executable missing' }
  $app = Start-Process -FilePath $exe.FullName -WindowStyle Hidden -PassThru
  Start-Sleep -Seconds 6
  $app.Refresh()
  if ($app.HasExited) { throw "Application exited before smoke check: $($app.ExitCode)" }
  Write-Output 'Application remained running for six seconds. This does not verify editor functionality.'
} finally {
  if ($app -and -not $app.HasExited) {
    $null = $app.CloseMainWindow()
    if (-not $app.WaitForExit(5000)) { Stop-Process -Id $app.Id -Force }
  }
  if (Test-Path -LiteralPath $installDir) {
    $uninstaller = Get-ChildItem -LiteralPath $installDir -Filter '*uninstall*.exe' -File | Select-Object -First 1
    if (-not $uninstaller) { throw 'Uninstaller missing' }
    # _?= keeps NSIS in place so that -Wait observes actual uninstall completion.
    $uninstall = Start-Process -FilePath $uninstaller.FullName -ArgumentList @('/S', "_?=$installDir") -WindowStyle Hidden -Wait -PassThru
    if ($uninstall.ExitCode -ne 0) { throw "Uninstaller exited with $($uninstall.ExitCode)" }
    if ($exe -and (Test-Path -LiteralPath $exe.FullName)) { throw 'Application executable remained after uninstall' }
  }
}
