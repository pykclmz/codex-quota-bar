param(
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\CodexQuotaBar'),
    [switch]$Rollback,
    [switch]$DebugState,
    [switch]$NoStart
)
$ErrorActionPreference = 'Stop'
$taskDestination = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$taskSource = $PSScriptRoot
$taskNames = @('CodexQuotaBar.exe','启动额度条.cmd','退出额度条.cmd','开启自动跟随.cmd','关闭自动跟随.cmd','使用说明.md','install.ps1','回退上一版本.cmd')
$taskBackupRoot = Join-Path $taskDestination 'backups'
$taskPointer = Join-Path $taskDestination 'last-backup.json'
if ($Rollback) {
    if (-not (Test-Path -LiteralPath $taskPointer)) { throw '没有可回退的版本。' }
    $taskSource = [IO.Path]::GetFullPath((Get-Content -LiteralPath $taskPointer -Raw | ConvertFrom-Json).directory)
    if (-not $taskSource.StartsWith($taskBackupRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '备份目录校验失败。' }
} elseif ([string]::Equals($taskSource.TrimEnd('\'),$taskDestination,[StringComparison]::OrdinalIgnoreCase)) {
    throw '请从新版本解压目录运行 install.ps1；回退请使用 -Rollback。'
}
$taskSourceExe = Join-Path $taskSource 'CodexQuotaBar.exe'
if (-not (Test-Path -LiteralPath $taskSourceExe)) { throw '安装来源缺少 CodexQuotaBar.exe。' }
$taskSelfTest = Start-Process -FilePath $taskSourceExe -ArgumentList '--self-test' -WindowStyle Hidden -PassThru
if (-not $taskSelfTest.WaitForExit(30000)) { $taskSelfTest.Kill(); throw '新版本自检超时，安装已停止。' }
if ($taskSelfTest.ExitCode -ne 0) { throw '新版本自检失败，安装已停止。' }
New-Item -ItemType Directory -Path $taskDestination -Force | Out-Null
New-Item -ItemType Directory -Path $taskBackupRoot -Force | Out-Null
$taskDestinationExe = Join-Path $taskDestination 'CodexQuotaBar.exe'
$taskOldProcesses = @(Get-Process -Name CodexQuotaBar -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $taskDestinationExe })
if ($taskOldProcesses.Count -gt 0) {
    $taskQuit = Start-Process -FilePath $taskDestinationExe -ArgumentList '--quit' -WindowStyle Hidden -Wait -PassThru
    foreach ($taskOldProcess in $taskOldProcesses) {
        if (-not $taskOldProcess.WaitForExit(10000)) { throw '旧版本未退出，安装已停止。' }
    }
}
$taskBackup = Join-Path $taskBackupRoot ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $taskBackup | Out-Null
foreach ($taskName in $taskNames + @('installed-version.json')) {
    $taskExisting = Join-Path $taskDestination $taskName
    if (Test-Path -LiteralPath $taskExisting) { Copy-Item -LiteralPath $taskExisting -Destination $taskBackup }
}
function Start-QuotaWatcher {
    if ($NoStart) { return }
    $taskArguments = @('--background')
    if ($DebugState) { $taskArguments += '--debug-state' }
    $taskWatcher = Start-Process -FilePath $taskDestinationExe -ArgumentList $taskArguments -WorkingDirectory $taskDestination -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 2
    if ($taskWatcher.HasExited) { throw '额度条启动失败。' }
    Write-Output ('运行进程：' + $taskWatcher.Id)
}
try {
    foreach ($taskName in $taskNames) {
        $taskFile = Join-Path $taskSource $taskName
        if (Test-Path -LiteralPath $taskFile) { Copy-Item -LiteralPath $taskFile -Destination (Join-Path $taskDestination $taskName) -Force }
    }
    Start-QuotaWatcher
    $taskVersion = (Get-Item -LiteralPath $taskDestinationExe).VersionInfo.FileVersion
    $taskRecord = @{ version=$taskVersion; installedAt=[DateTimeOffset]::Now.ToString('o'); sha256=(Get-FileHash -LiteralPath $taskDestinationExe -Algorithm SHA256).Hash; backup=$taskBackup }
    $taskRecord | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskDestination 'installed-version.json') -Encoding UTF8
    if (Test-Path -LiteralPath (Join-Path $taskBackup 'CodexQuotaBar.exe')) {
        @{ directory=$taskBackup } | ConvertTo-Json | Set-Content -LiteralPath $taskPointer -Encoding UTF8
    }
    Write-Output ('已安装版本：' + $taskVersion)
    Write-Output ('安装目录：' + $taskDestination)
    Write-Output ('旧版备份：' + $taskBackup)
} catch {
    $taskFailure = $_
    # Restore only this utility's known files. Settings and login data are never part of the payload.
    if (Test-Path -LiteralPath (Join-Path $taskBackup 'CodexQuotaBar.exe')) {
        $taskNewProcesses = @(Get-Process -Name CodexQuotaBar -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $taskDestinationExe })
        if ($taskNewProcesses.Count -gt 0) {
            $taskQuit = Start-Process -FilePath $taskDestinationExe -ArgumentList '--quit' -WindowStyle Hidden -Wait -PassThru
            foreach ($taskNewProcess in $taskNewProcesses) { if (-not $taskNewProcess.WaitForExit(10000)) { throw '更新失败且程序未退出，请从备份目录手动恢复。' } }
        }
        foreach ($taskName in $taskNames + @('installed-version.json')) {
            $taskOriginal = Join-Path $taskBackup $taskName
            if (Test-Path -LiteralPath $taskOriginal) { Copy-Item -LiteralPath $taskOriginal -Destination (Join-Path $taskDestination $taskName) -Force }
        }
        Start-QuotaWatcher
    }
    throw $taskFailure
}
