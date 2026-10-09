$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskStage = Join-Path $taskRoot ('build\install-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskStage -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $taskRoot 'CodexQuotaBar.exe') -Destination $taskStage
$taskGuide = Join-Path $taskStage '使用说明.md'
'previous-version-document' | Set-Content -LiteralPath $taskGuide -Encoding UTF8
$taskSettings = Join-Path $taskStage 'settings.json'
'{"Theme":"dark","DisplayDetail":0}' | Set-Content -LiteralPath $taskSettings -Encoding UTF8
$taskSettingsHash = (Get-FileHash -LiteralPath $taskSettings).Hash
& (Join-Path $taskRoot 'install.ps1') -InstallDirectory $taskStage -NoStart
if ((Get-Content -LiteralPath $taskGuide -Raw).Trim() -eq 'previous-version-document') { throw 'Upgrade did not replace application payload' }
& (Join-Path $taskRoot 'install.ps1') -InstallDirectory $taskStage -Rollback -NoStart
if ((Get-Content -LiteralPath $taskGuide -Raw).Trim() -ne 'previous-version-document') { throw 'Rollback did not restore application payload' }
if ((Get-FileHash -LiteralPath $taskSettings).Hash -ne $taskSettingsHash) { throw 'User settings changed during upgrade/rollback' }
$taskRecord = Get-Content -LiteralPath (Join-Path $taskStage 'installed-version.json') -Raw | ConvertFrom-Json
if ($taskRecord.sha256 -ne (Get-FileHash -LiteralPath (Join-Path $taskStage 'CodexQuotaBar.exe')).Hash) { throw 'Version record hash mismatch' }
@{passed=$true;checks=@('upgrade replaces payload','rollback restores payload','settings preserved','version hash verified')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'install-result.json') -Encoding UTF8
Write-Output 'Installation regression passed.'
