param([switch]$Tests)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$native = Join-Path $project 'native'
$library = Join-Path $native 'lib'
$output = Join-Path $native 'out'
New-Item -ItemType Directory -Force -Path $library, $output | Out-Null
$dll = Join-Path $library 'ICSharpCode.AvalonEdit.dll'
if (-not (Test-Path -LiteralPath $dll)) {
  $package = Join-Path $library 'avalonedit.zip'
  Invoke-WebRequest 'https://api.nuget.org/v3-flatcontainer/avalonedit/6.3.1.120/avalonedit.6.3.1.120.nupkg' -OutFile $package
  $expected = '8ac3e5aa2bb6178d92cbe72c7900a683fab0aee66a8eafa481611c444dbee3e3'
  if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'AvalonEdit package checksum mismatch' }
  $expanded = Join-Path $library 'avalonedit'
  Expand-Archive -LiteralPath $package -DestinationPath $expanded -Force
  Copy-Item -LiteralPath (Join-Path $expanded 'lib/net462/ICSharpCode.AvalonEdit.dll') -Destination $dll
}
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler is unavailable' }
$references = @('System.dll','System.Core.dll','System.Xml.dll','System.Xml.Linq.dll','System.Runtime.Serialization.dll','System.Windows.Forms.dll','System.Drawing.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$references += @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll','System.Xaml.dll','UIAutomationClient.dll','UIAutomationTypes.dll') | ForEach-Object {
  $path = Join-Path $framework ('WPF/' + $_)
  if (-not (Test-Path -LiteralPath $path)) { $path = Join-Path $framework $_ }
  '/reference:' + $path
}
$references += '/reference:' + $dll
$sources = @(Get-ChildItem -LiteralPath $native -Filter '*.cs' -File | Where-Object { $_.Name -notmatch 'Tests?\.cs$|TestProgram\.cs$' } | ForEach-Object FullName)
$argsList = @('/nologo','/utf8output','/langversion:5','/optimize+','/platform:anycpu','/target:winexe','/main:FlowLog.Native.Program',('/win32manifest:' + (Join-Path $native 'app.manifest')),('/out:' + (Join-Path $output 'FlowLog.Native.exe'))) + $references + $sources
& $compiler @argsList
if ($LASTEXITCODE -ne 0) { throw 'Native build failed' }
Copy-Item -LiteralPath $dll -Destination $output
Copy-Item -LiteralPath (Join-Path $native 'FlowLog.Native.exe.config') -Destination $output
Copy-Item -LiteralPath (Join-Path $project 'LICENSE') -Destination (Join-Path $output 'LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $native 'THIRD-PARTY-NOTICES.txt') -Destination $output
if ($Tests) {
  $testSources = @(Get-ChildItem -LiteralPath $native -Filter '*.cs' -File | ForEach-Object FullName)
  & $compiler @('/nologo','/utf8output','/langversion:5','/platform:anycpu','/target:exe','/main:FlowLog.Native.TestProgram',('/out:' + (Join-Path $output 'FlowLog.Tests.exe'))) @references @testSources
  if ($LASTEXITCODE -ne 0) { throw 'Native tests did not compile' }
  Copy-Item -LiteralPath (Join-Path $native 'FlowLog.Native.exe.config') -Destination (Join-Path $output 'FlowLog.Tests.exe.config')
  & (Join-Path $output 'FlowLog.Tests.exe')
  if ($LASTEXITCODE -ne 0) { throw 'Native tests failed' }
}
Get-ChildItem -LiteralPath $output -File | Select-Object Name,Length
