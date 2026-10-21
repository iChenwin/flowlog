param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build-native.ps1') }
$native = Join-Path $project 'native'
$library = Join-Path $native 'lib'
$packages = Join-Path $native 'packages'
New-Item -ItemType Directory -Force -Path $packages | Out-Null
$nsis = Join-Path $library 'nsis/nsis-3.13/makensis.exe'
if (-not (Test-Path -LiteralPath $nsis)) {
  $archive = Join-Path $library 'nsis.zip'
  if (-not (Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest 'https://github.com/tauri-apps/binary-releases/releases/download/nsis-3.13/nsis-3.13.zip' -OutFile $archive
  }
  $expected = 'ba63dffc4410ee89193e1cb5a41989991bd77c61068da17e3156d136b7b0b3d8'
  if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'NSIS checksum mismatch' }
  Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $library 'nsis') -Force
}
& $nsis /V2 /INPUTCHARSET UTF8 "/DPACKAGE_DIR=$packages" (Join-Path $native 'setup.nsi')
if ($LASTEXITCODE -ne 0) { throw 'Native installer packaging failed' }
$names = @('FlowLog.Native.exe','FlowLog.Native.exe.config','ICSharpCode.AvalonEdit.dll','LICENSE.txt','THIRD-PARTY-NOTICES.txt')
$files = @($names | ForEach-Object { Join-Path $native ('out/' + $_) })
$zip = Join-Path $packages 'FlowLog-Native-0.2.0-Portable.zip'
Compress-Archive -LiteralPath $files -DestinationPath $zip -Force
$assets = @(Get-ChildItem -LiteralPath $packages -File | Where-Object { $_.Extension -in '.exe','.zip' } | ForEach-Object {
  if ($_.Length -ge 20000000) { throw 'Package exceeds 20MB' }
  [ordered]@{ name=$_.Name; bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$report = [ordered]@{ version='0.2.0'; ui='Windows WPF + AvalonEdit; no WebView/Electron'; runtime='System .NET Framework 4.8 required; excluded from package size'; assets=$assets }
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $packages 'native-package-report.json') -Encoding UTF8
$report | ConvertTo-Json -Depth 5
