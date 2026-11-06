#requires -Version 5.1
<#
.SYNOPSIS
    卸载 PotPlayer Bridge 本地代理。

.DESCRIPTION
    默认保留 %LocalAppData%\PotPlayerBridge\config.json 与 agent.log，方便以后重装后继续使用。
    加 -RemoveData 会连同配置/日志一起删除。

.PARAMETER RemoveData
    同时删除配置、日志与数据目录。

.PARAMETER Force
    发现代理仍在运行且无法正常关闭时，强制结束进程。
#>
[CmdletBinding()]
param(
    [switch]$RemoveData,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$installDir = Join-Path $env:LOCALAPPDATA 'PotPlayerBridge'
$targetExe = Join-Path $installDir 'PotPlayerBridge.exe'

Write-Host '==> 停止 PotPlayer Bridge' -ForegroundColor Cyan
$running = Get-Process -Name 'PotPlayerBridge' -ErrorAction SilentlyContinue
if ($running) {
    foreach ($process in $running) {
        try {
            $process.CloseMainWindow() | Out-Null
        } catch {
            # 托盘程序没有主窗口。
        }
    }

    Start-Sleep -Milliseconds 800
    $running = Get-Process -Name 'PotPlayerBridge' -ErrorAction SilentlyContinue
    if ($running) {
        if ($Force) {
            $running | Stop-Process -Force
        } else {
            Write-Warning 'PotPlayer Bridge 仍在运行。请从托盘退出后重试，或加 -Force。'
        }
    }
}

Write-Host '==> 移除 potplayerbridge:// 协议' -ForegroundColor Cyan
$protocolKey = 'HKCU:\Software\Classes\potplayerbridge'
if (Test-Path $protocolKey) {
    Remove-Item $protocolKey -Recurse -Force
}

if ($RemoveData) {
    Write-Host "==> 删除数据目录：$installDir" -ForegroundColor Cyan
    if (Test-Path $installDir) {
        Remove-Item $installDir -Recurse -Force
    }
} else {
    Write-Host "==> 保留配置与日志：$installDir" -ForegroundColor Cyan
    if (Test-Path $targetExe) {
        Remove-Item $targetExe -Force
    }
}

Write-Host ''
Write-Host '卸载完成。'
