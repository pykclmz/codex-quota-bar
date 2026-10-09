param([string]$InstallDirectory=(Join-Path $env:LOCALAPPDATA 'Programs\CodexQuotaBar'))
$ErrorActionPreference='Stop'
$taskExe=Join-Path $InstallDirectory 'CodexQuotaBar.exe'
if(-not (Test-Path -LiteralPath $taskExe)) { throw '请先安装额度条。' }
if((Get-Item -LiteralPath $taskExe).VersionInfo.FileVersion -lt [version]'1.3.0.0') { throw '请先更新额度条到 1.3.0。' }
$taskRoot=Split-Path $PSScriptRoot -Parent
$taskPluginSource=Join-Path $taskRoot 'plugin'
if(-not (Test-Path -LiteralPath $taskPluginSource)) { $taskPluginSource=Join-Path $PSScriptRoot 'plugin' }
if(-not (Test-Path -LiteralPath $taskPluginSource)) { throw '缺少额度条插件源文件。' }
$taskMarketplace=Join-Path $InstallDirectory 'plugin-marketplace'
$taskPlugin=Join-Path $taskMarketplace 'plugins\codex-quota-bar'
New-Item -ItemType Directory -Path (Join-Path $taskMarketplace '.agents\plugins'),(Join-Path $taskPlugin 'bin'),(Join-Path $taskPlugin '.codex-plugin') -Force | Out-Null
# This desktop CLI reads legacy MCP declarations. Keep the portable source
# package intact, but install its supported Codex compatibility layout.
foreach($taskName in @('mcp.json','.mcp.json','README.md')) { Copy-Item -LiteralPath (Join-Path $taskPluginSource $taskName) -Destination (Join-Path $taskPlugin $taskName) -Force }
$taskOldPortable=Join-Path $taskPlugin 'plugin.json'
if(Test-Path -LiteralPath $taskOldPortable) { Remove-Item -LiteralPath $taskOldPortable }
Copy-Item -LiteralPath (Join-Path $taskPluginSource '.codex-plugin\plugin.json') -Destination (Join-Path $taskPlugin '.codex-plugin\plugin.json') -Force
Copy-Item -LiteralPath $taskExe -Destination (Join-Path $taskPlugin 'bin\CodexQuotaBar.exe') -Force
$taskManifest=@{ name='quota-local'; interface=@{displayName='额度条本地插件'}; plugins=@(@{name='codex-quota-bar';source=@{source='local';path='./plugins/codex-quota-bar'};policy=@{installation='AVAILABLE';authentication='ON_INSTALL'};category='Productivity'}) }
$taskManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskMarketplace '.agents\plugins\marketplace.json') -Encoding utf8
& codex plugin marketplace add $taskMarketplace --json
if($LASTEXITCODE -ne 0) { throw '本地插件市场注册失败。' }
& codex plugin add codex-quota-bar@quota-local --json
if($LASTEXITCODE -ne 0) { throw '插件安装失败。' }
$taskServers=& codex mcp list --json | ConvertFrom-Json
if($LASTEXITCODE -ne 0 -or -not ($taskServers | Where-Object { $_.name -eq 'quota-settings' -and $_.enabled })) { throw '插件已安装，但设置服务器尚未被 Codex 发现。集成入口未启用。' }
# Write the integration marker only after the supported install command succeeds.
$taskMarketplaceFile=Join-Path $taskMarketplace '.agents\plugins\marketplace.json'
$taskUrl='codex://plugins/codex-quota-bar@quota-local?marketplacePath='+[Uri]::EscapeDataString($taskMarketplaceFile)+'&pluginName=codex-quota-bar&breadcrumb=settings'
$taskPluginVersion=(Get-Content -LiteralPath (Join-Path $taskPlugin '.codex-plugin\plugin.json') -Raw | ConvertFrom-Json).version
@{url=$taskUrl;marketplacePath=$taskMarketplaceFile;version=$taskPluginVersion} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $InstallDirectory 'plugin-integration.json') -Encoding utf8
Write-Output '额度条插件已安装。右键额度条会打开 Codex 内的插件设置。'
