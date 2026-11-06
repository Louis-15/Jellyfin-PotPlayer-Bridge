#requires -Version 5.1
<#
.SYNOPSIS
    发布 Windows 本地代理（自包含单文件）。

.DESCRIPTION
    产物：artifacts\PotPlayerBridge\PotPlayerBridge.exe
    目标机器不需要预装 .NET：运行时、WinForms、原生库都打进同一个 exe。

.PARAMETER Configuration
    构建配置，默认 Release。

.PARAMETER Runtime
    运行时标识，默认 win-x64。

.PARAMETER Dotnet
    dotnet 可执行文件路径。默认用 PATH 上的 dotnet。
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$Dotnet
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'PotPlayerBridge\PotPlayerBridge.csproj'
$output = Join-Path $root 'artifacts\PotPlayerBridge'

if (-not $Dotnet) {
    if (Get-Command dotnet -ErrorAction SilentlyContinue) {
        $Dotnet = 'dotnet'
    } elseif ($env:DOTNET_ROOT -and (Test-Path (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) {
        $Dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
    } else {
        throw '找不到 dotnet。请安装 .NET 10 SDK，或用 -Dotnet 指定路径。'
    }
}

Write-Host "==> 发布 PotPlayer Bridge（$Runtime，自包含单文件）" -ForegroundColor Cyan

# 清掉旧产物，避免把上一版的散装 dll 一起带到安装目录。
if (Test-Path $output) {
    Remove-Item $output -Recurse -Force
}

& $Dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $output `
    --nologo

if ($LASTEXITCODE -ne 0) {
    throw "发布失败（exit $LASTEXITCODE）"
}

$exe = Join-Path $output 'PotPlayerBridge.exe'
if (-not (Test-Path $exe)) {
    throw "没有生成 $exe"
}

$file = Get-Item $exe
$hash = (Get-FileHash $exe -Algorithm MD5).Hash.ToLowerInvariant()

Write-Host ''
Write-Host "产物：$exe"
Write-Host ("大小：{0:N1} MB" -f ($file.Length / 1MB))
Write-Host "MD5： $hash"
Write-Host ''
Write-Host '安装（当前用户，无需管理员）：'
Write-Host "  powershell -ExecutionPolicy Bypass -File `"$root\install.ps1`""
Write-Host ''
Write-Host '安装后验证：'
Write-Host '  curl.exe -H "X-PPB-Client: 1" http://127.0.0.1:18927/health'
