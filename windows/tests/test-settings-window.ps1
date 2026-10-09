$ErrorActionPreference = 'Stop'
$taskFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $taskFramework 'csc.exe'))) { $taskFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskExecutable = Join-Path $PSScriptRoot 'SettingsWindowTests.exe'
$taskReferences = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','Microsoft.CSharp.dll') | ForEach-Object { '/r:' + (Join-Path $taskFramework $_) }
$taskReferences += @('UIAutomationClient.dll','UIAutomationTypes.dll','WindowsBase.dll') | ForEach-Object { '/r:' + (Join-Path (Join-Path $taskFramework 'WPF') $_) }
& (Join-Path $taskFramework 'csc.exe') /nologo /target:winexe /nowarn:4014 /main:CodexQuotaBar.SettingsWindowTests ('/out:' + $taskExecutable) @taskReferences (Join-Path $taskRoot 'QuotaBar.cs') (Join-Path $taskRoot 'SettingsWindow.cs') (Join-Path $taskRoot 'WindowFollower.cs') (Join-Path $taskRoot 'SettingsBridge.cs') (Join-Path $PSScriptRoot 'SettingsWindowTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Settings-window test compilation failed' }
$taskReport = Join-Path $PSScriptRoot 'settings-window-result.json'
if (Test-Path -LiteralPath $taskReport) { Remove-Item -LiteralPath $taskReport -Force }
$taskTest = Start-Process -FilePath $taskExecutable -WindowStyle Hidden -PassThru
if (-not $taskTest.WaitForExit(60000)) { $taskTest.Kill(); throw 'Settings-window test timeout' }
if (-not (Test-Path -LiteralPath $taskReport)) { throw ('UI test exited without a report. Exit code: ' + $taskTest.ExitCode) }
Get-Content -LiteralPath $taskReport
exit $taskTest.ExitCode
