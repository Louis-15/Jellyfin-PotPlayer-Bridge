#requires -Version 5.1
<#
.SYNOPSIS
    编译 Jellyfin 插件并打包成可以直接解压到插件目录的 zip。

.DESCRIPTION
    产物：
      artifacts\Jellyfin.Plugin.PotPlayerBridge_<version>\   dll + meta.json + logo.png
      artifacts\PotPlayerBridge-Plugin-<version>.zip        解压到 /config/plugins/<任意目录名>/ 即可

    zip 命名与 GitHub Release 资产保持一致（PotPlayerBridge-Plugin-<version>.zip），
    manifest.json 里的 sourceUrl 也指向这个文件名；改名会让自动更新下载 404。

    脚本会先校验 csproj 的 <Version> 与 meta.json 的 version 是否一致。
    两者不一致时 Jellyfin 显示的版本和 dll 里的版本会不同，排查问题时会非常混乱，
    所以这里直接报错，要求先把版本号改齐。

.PARAMETER Configuration
    构建配置，默认 Release。

.PARAMETER Dotnet
    dotnet 可执行文件路径。默认用 PATH 上的 dotnet，其次是 %DOTNET_ROOT%\dotnet.exe。
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Dotnet
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$projectDir = Join-Path $root 'Jellyfin.Plugin.PotPlayerBridge'
$project = Join-Path $projectDir 'Jellyfin.Plugin.PotPlayerBridge.csproj'
$metaPath = Join-Path $projectDir 'meta.json'

if (-not $Dotnet) {
    if (Get-Command dotnet -ErrorAction SilentlyContinue) {
        $Dotnet = 'dotnet'
    } elseif ($env:DOTNET_ROOT -and (Test-Path (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) {
        $Dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
    } else {
        throw '找不到 dotnet。请安装 .NET 10 SDK，或用 -Dotnet 指定路径。'
    }
}

# 显式按 UTF-8 读取：Windows PowerShell 5.1 默认会用 ANSI 代码页，中文会变乱码。
$meta = [System.IO.File]::ReadAllText($metaPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
$version = $meta.version

[xml]$projectXml = [System.IO.File]::ReadAllText($project, [System.Text.Encoding]::UTF8)
$projectVersion = $projectXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ($projectVersion -ne $version) {
    throw "版本号不一致：csproj=$projectVersion，meta.json=$version。请先把两处改成同一个版本再构建。"
}

$artifacts = Join-Path $root 'artifacts'
$packageDir = Join-Path $artifacts "Jellyfin.Plugin.PotPlayerBridge_$version"
$zipPath = Join-Path $artifacts "PotPlayerBridge-Plugin-$version.zip"

Write-Host "==> 编译 PotPlayer Bridge $version ($Configuration)" -ForegroundColor Cyan
& $Dotnet build $project -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    throw "编译失败（exit $LASTEXITCODE）"
}

$buildOutput = Join-Path $projectDir "bin/$Configuration/net10.0"

if (Test-Path $packageDir) {
    Remove-Item $packageDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $packageDir | Out-Null

Copy-Item (Join-Path $buildOutput 'Jellyfin.Plugin.PotPlayerBridge.dll') $packageDir
Copy-Item $metaPath $packageDir
Copy-Item (Join-Path $projectDir 'logo.png') $packageDir

if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}
Compress-Archive -Path (Join-Path $packageDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

$hash = (Get-FileHash $zipPath -Algorithm MD5).Hash.ToLowerInvariant()
$remoteDir = "<配置目录>/plugins/PotPlayer Bridge_$version"

Write-Host ''
Write-Host "插件目录:   $packageDir"
Write-Host "插件压缩包: $zipPath"
Write-Host "MD5:        $hash"
Write-Host ''
Write-Host '安装到服务器（先备份插件目录；把 <...> 换成你自己的值）：'
Write-Host "  scp -P <SSH端口> `"$zipPath`" <用户名>@<服务器地址>:/tmp/"
Write-Host "  ssh -p <SSH端口> <用户名>@<服务器地址> `"mkdir -p '$remoteDir' && unzip -o '/tmp/PotPlayerBridge-Plugin-$version.zip' -d '$remoteDir' && docker restart jellyfin`""
