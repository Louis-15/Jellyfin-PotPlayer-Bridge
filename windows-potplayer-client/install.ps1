#requires -Version 5.1
<#
.SYNOPSIS
    安装/更新 PotPlayer Bridge 本地代理，并注册 potplayerbridge:// 协议。

.DESCRIPTION
    全部操作都在当前用户范围内，不需要管理员：
      1. 复制 exe 到 %LocalAppData%\PotPlayerBridge\
      2. 注册 HKCU\Software\Classes\potplayerbridge
      3. 启动代理（-NoStart 可跳过）

    配置与日志不会被覆盖：它们和 exe 不在同一个文件里。

.PARAMETER Source
    发布产物目录，默认 .\artifacts\PotPlayerBridge。

.PARAMETER NoStart
    安装完成后不自动启动。

.PARAMETER Force
    发现代理正在运行时，直接结束进程（默认会先尝试正常关闭）。
#>
[CmdletBinding()]
param(
    [string]$Source,
    [switch]$NoStart,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

if (-not $Source) {
    $Source = Join-Path $PSScriptRoot 'artifacts\PotPlayerBridge'
}

$sourceExe = Join-Path $Source 'PotPlayerBridge.exe'
if (-not (Test-Path $sourceExe)) {
    throw "找不到 $sourceExe。请先在仓库根目录运行 build.ps1。"
}

$installDir = Join-Path $env:LOCALAPPDATA 'PotPlayerBridge'
$targetExe = Join-Path $installDir 'PotPlayerBridge.exe'

Write-Host '==> 停止正在运行的 PotPlayer Bridge（如果有）' -ForegroundColor Cyan
$running = Get-Process -Name 'PotPlayerBridge' -ErrorAction SilentlyContinue
if ($running) {
    foreach ($process in $running) {
        try {
            $process.CloseMainWindow() | Out-Null
        } catch {
            # 托盘程序没有主窗口，关闭失败属于正常情况。
        }
    }

    Start-Sleep -Milliseconds 800
    $running = Get-Process -Name 'PotPlayerBridge' -ErrorAction SilentlyContinue
    if ($running -and $Force) {
        $running | Stop-Process -Force
        Start-Sleep -Milliseconds 500
    } elseif ($running) {
        throw 'PotPlayer Bridge 仍在运行。请从托盘退出，或加 -Force 重试。'
    }
}

Write-Host "==> 复制到 $installDir" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item $sourceExe $targetExe -Force

Write-Host '==> 注册 potplayerbridge:// 协议（HKCU，无需管理员）' -ForegroundColor Cyan
$protocolKey = 'HKCU:\Software\Classes\potplayerbridge'
New-Item -Path $protocolKey -Force | Out-Null
Set-ItemProperty -Path $protocolKey -Name '(Default)' -Value 'URL:PotPlayer Bridge Protocol'
New-ItemProperty -Path $protocolKey -Name 'URL Protocol' -Value '' -PropertyType String -Force | Out-Null
New-Item -Path (Join-Path $protocolKey 'shell\open\command') -Force | Out-Null
Set-ItemProperty -Path (Join-Path $protocolKey 'shell\open\command') -Name '(Default)' -Value ('"' + $targetExe + '" "%1"')

Write-Host ''
Write-Host "已安装：$targetExe"
Write-Host "配置文件：$(Join-Path $installDir 'config.json')"
Write-Host "日志文件：$(Join-Path $installDir 'agent.log')"
Write-Host ''

if ($NoStart) {
    Write-Host '未启动（-NoStart）。可双击 exe 或运行 potplayerbridge://start 启动。'
} else {
    Start-Process $targetExe
    Write-Host '已启动。请查看系统托盘（可能需要点「显示隐藏的图标」）。'
}
