$ErrorActionPreference = 'Stop'
$taskFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $taskFramework 'csc.exe'))) { $taskFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskExecutable = Join-Path $PSScriptRoot 'ReliabilityTests.exe'
$taskReferences = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','Microsoft.CSharp.dll') | ForEach-Object { '/r:' + (Join-Path $taskFramework $_) }
$taskReferences += @('UIAutomationClient.dll','UIAutomationTypes.dll','WindowsBase.dll') | ForEach-Object { '/r:' + (Join-Path (Join-Path $taskFramework 'WPF') $_) }
& (Join-Path $taskFramework 'csc.exe') /nologo /target:winexe /nowarn:4014 /main:CodexQuotaBar.ReliabilityTests ('/out:' + $taskExecutable) @taskReferences (Join-Path $taskRoot 'QuotaBar.cs') (Join-Path $taskRoot 'SettingsWindow.cs') (Join-Path $taskRoot 'WindowFollower.cs') (Join-Path $PSScriptRoot 'ReliabilityTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Reliability test compilation failed' }
$taskTest = Start-Process -FilePath $taskExecutable -WindowStyle Hidden -PassThru
if (-not $taskTest.WaitForExit(60000)) { $taskTest.Kill(); throw 'Reliability test timeout' }
Get-Content -LiteralPath (Join-Path $PSScriptRoot 'reliability-result.json')
exit $taskTest.ExitCode
