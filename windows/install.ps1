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
    $taskApplicationVersion=[version](Get-Item -LiteralPath $taskDestinationExe).VersionInfo.FileVersion
    if($taskApplicationVersion -lt [version]'1.3.1.0') {
        # Older rollback payloads do not understand the new launcher/health flags.
        # Dispatch them independently as a demand-only Windows task as well.
        $taskService=New-Object -ComObject Schedule.Service
        $taskService.Connect()
        $taskFolder=$taskService.GetFolder('\')
        $taskSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        $taskDefinition=$taskService.NewTask(0)
        $taskDefinition.RegistrationInfo.Description='Codex 额度条：按需启动回退版本（无登录触发器）。'
        $taskDefinition.Principal.UserId=$taskSid
        $taskDefinition.Principal.LogonType=3
        $taskDefinition.Principal.RunLevel=0
        $taskAction=$taskDefinition.Actions.Create(0)
        $taskAction.Path=$taskDestinationExe
        $taskAction.Arguments=if($DebugState) {'--background --debug-state'} else {'--background'}
        $taskAction.WorkingDirectory=$taskDestination
        $taskDefinition.Settings.Enabled=$true
        $taskDefinition.Settings.DisallowStartIfOnBatteries=$false
        $taskDefinition.Settings.StopIfGoingOnBatteries=$false
        $taskDefinition.Settings.ExecutionTimeLimit='PT0S'
        $taskDefinition.Settings.MultipleInstances=2
        $taskRegistered=$taskFolder.RegisterTaskDefinition(('CodexQuotaBar-Manual-'+$taskSid),$taskDefinition,6,$taskSid,$null,3,$null)
        $null=$taskRegistered.Run($null)
        Start-Sleep -Seconds 2
        if(-not (Get-Process -Name CodexQuotaBar -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $taskDestinationExe})) { throw '回退版本未能启动。' }
        Write-Output '回退版本已通过 Windows 独立后台任务启动。'
        return
    }
    $taskArguments = @('--start-background')
    if ($DebugState) { $taskArguments += '--debug-state' }
    $taskLauncher = Start-Process -FilePath $taskDestinationExe -ArgumentList $taskArguments -WorkingDirectory $taskDestination -WindowStyle Hidden -PassThru -Wait
    if($taskLauncher.ExitCode -ne 0) { throw '额度条独立启动失败，请检查任务计划程序。' }
    $taskHealthy=$false
    for($taskAttempt=0;$taskAttempt -lt 4;$taskAttempt++) {
        $taskProbe=Start-Process -FilePath $taskDestinationExe -ArgumentList '--check-running' -WindowStyle Hidden -PassThru -Wait
        if($taskProbe.ExitCode -eq 0) { $taskHealthy=$true;break }
        Start-Sleep -Milliseconds 250
    }
    if(-not $taskHealthy) { throw '额度条启动后未能响应，安装已停止。' }
    Write-Output '额度条已通过 Windows 独立后台任务启动。'
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
