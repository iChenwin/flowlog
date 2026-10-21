param([string]$BundleDirectory = 'src-tauri/target/release/bundle/nsis', [long]$LimitBytes = 20000000, [string]$ReportPath = 'artifacts/package-report.json')
$ErrorActionPreference = 'Stop'
$packages = @(Get-ChildItem -LiteralPath $BundleDirectory -Filter '*.exe' -File)
if ($packages.Count -eq 0) { throw "No NSIS installer found in $BundleDirectory" }
$report = @($packages | ForEach-Object {
  [pscustomobject]@{ file = $_.Name; bytes = $_.Length; limitBytes = $LimitBytes; passed = ($_.Length -lt $LimitBytes); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $ReportPath) | Out-Null
ConvertTo-Json -InputObject $report -Depth 4 | Set-Content -LiteralPath $ReportPath -Encoding utf8
$report | Format-Table
if (@($report | Where-Object { -not $_.passed }).Count -gt 0) { throw "Installer size must be less than $LimitBytes bytes" }
