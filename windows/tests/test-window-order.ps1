param([switch]$Baseline)
$ErrorActionPreference = 'Stop'
$taskFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $taskFramework 'csc.exe'))) {
    $taskFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$taskExecutable = Join-Path $PSScriptRoot 'WindowOrderTest.exe'
$taskReferences = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','Microsoft.CSharp.dll') | ForEach-Object { '/r:' + (Join-Path $taskFramework $_) }
$taskReferences += @('UIAutomationClient.dll','UIAutomationTypes.dll','WindowsBase.dll') | ForEach-Object { '/r:' + (Join-Path (Join-Path $taskFramework 'WPF') $_) }
& (Join-Path $taskFramework 'csc.exe') /nologo /target:winexe /main:WindowOrderTest /nowarn:4014 ('/out:' + $taskExecutable) @taskReferences (Join-Path (Split-Path $PSScriptRoot -Parent) 'QuotaBar.cs') (Join-Path (Split-Path $PSScriptRoot -Parent) 'SettingsWindow.cs') (Join-Path (Split-Path $PSScriptRoot -Parent) 'WindowFollower.cs') (Join-Path (Split-Path $PSScriptRoot -Parent) 'SettingsBridge.cs') (Join-Path $PSScriptRoot 'WindowOrderTest.cs')
if ($LASTEXITCODE -ne 0) { throw 'Window-order test compilation failed' }
$taskArguments = @('"' + (Join-Path (Split-Path $PSScriptRoot -Parent) 'CodexQuotaBar.exe') + '"')
if ($Baseline) { $taskArguments += '--baseline' }
$taskTest = Start-Process -FilePath $taskExecutable -ArgumentList $taskArguments -WindowStyle Hidden -Wait -PassThru
Get-Content -LiteralPath (Join-Path $PSScriptRoot 'window-order-result.json')
exit $taskTest.ExitCode
