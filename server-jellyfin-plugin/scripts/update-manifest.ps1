#requires -Version 5.1
<#
.SYNOPSIS
    生成 / 更新 Jellyfin 插件仓库清单 manifest.json。

.DESCRIPTION
    Jellyfin 的「插件仓库」就是一个可公开访问的 JSON 清单。服务器会定期拉取它，
    把清单里的最高版本与自己已安装的版本比较；发现新版本后按 sourceUrl 下载 zip，
    并用 checksum（MD5）校验。因此这份清单必须由“实际发布的那次构建”生成：
    zip 被替换而 checksum 没跟着换，所有客户端都会在下载校验阶段失败。

    脚本采用“读旧清单 + 插入新版本 + 整体重写”的方式，而不是原地打补丁：
    Windows PowerShell 5.1 与 PowerShell 7 对单元素数组的 ConvertFrom-Json 结果不一致
    （5.1 会把单元素数组退化成对象），直接 .versions += ... 会悄悄把数组写成对象，
    Jellyfin 随后解析失败。这里统一用 @() 强制成数组，规避这个坑。

    历史版本会保留（默认最多 20 条），这样低版本 Jellyfin（targetAbi 较旧）仍能
    在清单里找到兼容的旧版本，而不是只能看到最新版、无法降级。

.PARAMETER Version
    插件版本，必须与 meta.json / csproj 一致，例如 1.0.0.1。

.PARAMETER Checksum
    发布 zip 的 MD5（大小写均可，脚本会转成小写）。

.PARAMETER SourceUrl
    zip 的下载地址，通常是 GitHub Release 资产 URL。

.PARAMETER Changelog
    显示在 Jellyfin 插件页的更新说明。为空时退化为一句版本说明。

.PARAMETER Repository
    GitHub 仓库 owner/name，用于拼插件列表里的 logo 地址。

.PARAMETER MaxVersions
    清单里最多保留多少个版本，默认 20；传 0 表示不限制。

.EXAMPLE
    ./scripts/update-manifest.ps1 `
        -Version 1.0.0.1 `
        -Checksum 0123456789abcdef0123456789abcdef `
        -SourceUrl "https://github.com/Louis-15/Jellyfin-PotPlayer-Bridge/releases/download/v1.0.0.1/PotPlayerBridge-Plugin-1.0.0.1.zip" `
        -Changelog "修复 xxx"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Checksum,
    [Parameter(Mandatory = $true)][string]$SourceUrl,
    [string]$Changelog,
    [string]$Repository = 'Louis-15/Jellyfin-PotPlayer-Bridge',
    [int]$MaxVersions = 20
)

$ErrorActionPreference = 'Stop'

$scriptRoot = $PSScriptRoot
$pluginRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptRoot '..'))
$metaPath = Join-Path $pluginRoot 'Jellyfin.Plugin.PotPlayerBridge\meta.json'
$manifestPath = Join-Path $pluginRoot 'manifest.json'
$utf8 = New-Object System.Text.UTF8Encoding($false)

if (-not (Test-Path $metaPath)) {
    throw "找不到 meta.json：$metaPath"
}

# meta.json 是纯对象（不是数组），PowerShell 5.1 / 7 的解析结果一致。
$meta = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($metaPath, [System.Text.Encoding]::UTF8))

if (-not $Changelog) {
    $Changelog = "Release $Version"
}

$entry = [pscustomobject]@{
    version   = $Version
    changelog = $Changelog.Trim()
    targetAbi = $meta.targetAbi
    sourceUrl = $SourceUrl
    checksum  = $Checksum.ToLowerInvariant()
    timestamp = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
}

# ---- 读取旧清单里的历史版本 -------------------------------------------------
$versions = @()
if (Test-Path $manifestPath) {
    $raw = [System.IO.File]::ReadAllText($manifestPath, [System.Text.Encoding]::UTF8)
    if ($raw.Trim().Length -gt 0) {
        $existing = ConvertFrom-Json -InputObject $raw
        # 顶层是数组；5.1 可能把单元素数组解析成对象，用 @() 统一成数组。
        $plugin = @($existing) | Select-Object -First 1
        if ($null -ne $plugin -and $null -ne $plugin.versions) {
            $versions = @($plugin.versions)
        }
    }
}

# 同版本重跑时覆盖旧条目，避免清单里出现两条一模一样的版本号。
$versions = @($versions | Where-Object { $_.version -ne $Version })

# 历史版本按语义版本从高到低排；解析失败的条目排到最后，不让它打断整个更新。
$versions = @($versions | Sort-Object -Property @{
    Expression = {
        try { [version]$_.version } catch { [version]'0.0.0.0' }
    }
} -Descending)

$allVersions = @($entry) + $versions
if ($MaxVersions -gt 0 -and $allVersions.Count -gt $MaxVersions) {
    $allVersions = @($allVersions[0..($MaxVersions - 1)])
}

# ---- 写出完整清单 -----------------------------------------------------------
$manifest = @(
    [pscustomobject]@{
        guid        = $meta.guid
        name        = $meta.name
        description = $meta.description
        overview    = $meta.overview
        owner       = $meta.owner
        category    = $meta.category
        imageUrl    = "https://raw.githubusercontent.com/$Repository/main/server-jellyfin-plugin/Jellyfin.Plugin.PotPlayerBridge/logo.png"
        versions    = @($allVersions)
    }
)

# 用 -InputObject 把顶层数组当成一个值序列化，避免单插件时被展开成对象。
$json = ConvertTo-Json -InputObject $manifest -Depth 8
[System.IO.File]::WriteAllText($manifestPath, $json + "`n", $utf8)

Write-Host "manifest.json -> $Version (checksum $($entry.checksum), $($allVersions.Count) 个版本)"
