param([string]$InstallDirectory=(Join-Path $env:LOCALAPPDATA 'Programs\CodexQuotaBar'))
$ErrorActionPreference='Stop'
$taskInfo=[Diagnostics.ProcessStartInfo]::new((Join-Path $InstallDirectory 'CodexQuotaBar.exe'),'--mcp')
$taskInfo.UseShellExecute=$false
$taskInfo.CreateNoWindow=$true
$taskInfo.RedirectStandardInput=$true
$taskInfo.RedirectStandardOutput=$true
$taskInfo.StandardOutputEncoding=[Text.Encoding]::UTF8
$taskProcess=[Diagnostics.Process]::Start($taskInfo)
$taskInput=[IO.StreamWriter]::new($taskProcess.StandardInput.BaseStream,[Text.UTF8Encoding]::new($false))
$taskInput.AutoFlush=$true
$taskSequence=0
function Invoke-QuotaPlugin([string]$name,$arguments) {
    $script:taskSequence++
    $taskInput.WriteLine((@{jsonrpc='2.0';id=$taskSequence;method='tools/call';params=@{name=$name;arguments=$arguments}} | ConvertTo-Json -Depth 7 -Compress))
    $taskRead=$taskProcess.StandardOutput.ReadLineAsync()
    if(-not $taskRead.Wait(15000)) { throw 'Plugin response timeout' }
    $taskReply=$taskRead.Result | ConvertFrom-Json
    if($taskReply.error -or $taskReply.result.isError) { throw ($taskReply | ConvertTo-Json -Depth 5 -Compress) }
    return $taskReply.result
}
$taskOriginal=$null
$taskMutated=$false
try {
    $taskCurrent=(Invoke-QuotaPlugin 'settings_read' @{}).structuredContent
    $taskOriginal=$taskCurrent.values.detail
    $taskNext=if($taskOriginal -eq '仅额度') {'完整信息'} else {'仅额度'}
    $taskChanged=(Invoke-QuotaPlugin 'settings_update' @{set=@{detail=$taskNext}}).structuredContent
    if($taskChanged.values.detail -ne $taskNext) { throw 'Settings update not reflected' }
    $taskMutated=$true
    $taskExpected=if($taskNext -eq '仅额度') {0} else {2}
    $taskSaved=Get-Content -LiteralPath (Join-Path $InstallDirectory 'settings.json') -Raw | ConvertFrom-Json
    if($taskSaved.displayDetail -ne $taskExpected) { throw 'Settings update not persisted' }
    $taskReadBack=(Invoke-QuotaPlugin 'settings_read' @{}).structuredContent
    if($taskReadBack.values.detail -ne $taskNext) { throw 'Settings read did not synchronize' }
    $taskQuota=Invoke-QuotaPlugin 'quota_status' @{}
    if(-not $taskQuota.structuredContent.preview) { throw 'Cached quota unavailable' }
    Write-Output 'Live plugin read/update/persistence/cache checks passed.'
} finally {
    try { if($taskMutated) { $null=Invoke-QuotaPlugin 'settings_update' @{set=@{detail=$taskOriginal}} } }
    finally {
        $taskInput.Dispose()
        if(-not $taskProcess.WaitForExit(2000)) { $taskProcess.Kill() }
        $taskProcess.Dispose()
    }
}
