#requires -Version 5.1
<#
.SYNOPSIS
    发布新的 Jellyfin 插件版本：改版本号 → 提交 → 打标签 → 推送。

.DESCRIPTION
    推送 v* 标签后，GitHub Actions（.github/workflows/build.yml）会自动：
      1. 跑单元测试、编译插件、生成发布 zip；
      2. 用实际 zip 的 MD5 更新 server-jellyfin-plugin/manifest.json 并提交回 main；
      3. 创建 GitHub Release 并上传插件 zip。

    Jellyfin 读取 main 分支上的 manifest.json，所以用户端不需要手动下载：
    在控制台「插件 → 存储库」添加清单地址后，Jellyfin 会按自己的定时任务检查更新。

    注意：
      - 本脚本只负责服务器插件。Windows 本地代理（windows-potplayer-client/）
        单独构建、单独发版，不参与这条链路。
      - 本脚本会 push 到远端。确认版本号和更新说明无误后再运行。

.PARAMETER Version
    新版本号，例如 1.0.0.1。必须与本次要打的标签 v<Version> 一致。

.PARAMETER Changelog
    更新说明，会写入 tag 消息，最终显示在 Jellyfin 插件页。

.EXAMPLE
    .\release.ps1 -Version 1.0.0.1 -Changelog "修复 xxx"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Changelog
)

$ErrorActionPreference = 'Stop'

$pluginRoot = $PSScriptRoot
$repoRoot = Split-Path -Parent $pluginRoot
$csproj = Join-Path $pluginRoot 'Jellyfin.Plugin.PotPlayerBridge\Jellyfin.Plugin.PotPlayerBridge.csproj'
$metaPath = Join-Path $pluginRoot 'Jellyfin.Plugin.PotPlayerBridge\meta.json'
$utf8 = New-Object System.Text.UTF8Encoding($false)

if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
    throw "版本号格式应为 1.2.3 或 1.2.3.4，收到：$Version"
}

if (-not $Changelog) {
    $Changelog = "Release $Version"
}

if (-not (Test-Path $csproj)) { throw "找不到 csproj：$csproj" }
if (-not (Test-Path $metaPath)) { throw "找不到 meta.json：$metaPath" }

$tagName = "v$Version"
$existingTag = (git -C $repoRoot tag -l $tagName)
if ($existingTag) {
    throw "标签 $tagName 已存在。版本号必须递增，不能重复使用。"
}

Write-Host "==> 更新插件版本号到 $Version" -ForegroundColor Cyan

# csproj 与 meta.json 必须同步改：build.ps1 会校验两者一致，
# 而 Jellyfin 显示的是 meta.json 的版本，dll 里则是 csproj 的版本。
$csprojText = [System.IO.File]::ReadAllText($csproj, [System.Text.Encoding]::UTF8)
$csprojText = [regex]::Replace($csprojText, '<Version>[^<]+</Version>', "<Version>$Version</Version>")
$csprojText = [regex]::Replace($csprojText, '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>$Version</AssemblyVersion>")
$csprojText = [regex]::Replace($csprojText, '<FileVersion>[^<]+</FileVersion>', "<FileVersion>$Version</FileVersion>")
[System.IO.File]::WriteAllText($csproj, $csprojText, $utf8)

$meta = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($metaPath, [System.Text.Encoding]::UTF8))
$meta.version = $Version
[System.IO.File]::WriteAllText($metaPath, (ConvertTo-Json -InputObject $meta -Depth 5) + "`n", $utf8)

Write-Host "==> 提交并打标签 $tagName" -ForegroundColor Cyan

# git 会往 stderr 写进度和 CRLF 警告；在 ErrorActionPreference=Stop 下会被当成异常。
$ErrorActionPreference = 'Continue'

git -C $repoRoot add -- $csproj $metaPath
if ($LASTEXITCODE -ne 0) { throw 'git add 失败' }

git -C $repoRoot commit -m "Release $Version" -m $Changelog
if ($LASTEXITCODE -ne 0) { throw 'git commit 失败（版本号是否没有变化？）' }

# CI 每次发版后会把新 checksum 回写进 manifest.json 并提交回 main，
# 所以本地 main 往往落后一个提交，直接 push 会被拒（non-fast-forward）。
# 先追平远端再打 tag，确保标签指向真正要发布的提交。
git -C $repoRoot fetch origin main
if ($LASTEXITCODE -ne 0) { throw 'git fetch origin main 失败' }

git -C $repoRoot rebase origin/main
if ($LASTEXITCODE -ne 0) {
    throw 'git rebase origin/main 失败：先手动解决冲突，再执行 git tag / git push'
}

git -C $repoRoot tag -a $tagName -m $Changelog
if ($LASTEXITCODE -ne 0) { throw "git tag $tagName 失败" }

git -C $repoRoot push origin main
if ($LASTEXITCODE -ne 0) { throw 'git push main 失败' }

git -C $repoRoot push origin $tagName
if ($LASTEXITCODE -ne 0) { throw "git push $tagName 失败" }

Write-Host ''
Write-Host "已推送 $tagName。GitHub Actions 会完成编译、打包、回写 manifest 并发布 Release：" -ForegroundColor Green
Write-Host "  https://github.com/Louis-15/Jellyfin-PotPlayer-Bridge/actions"
Write-Host "  https://github.com/Louis-15/Jellyfin-PotPlayer-Bridge/releases"
Write-Host ''
Write-Host '发布完成后，Jellyfin 的插件仓库会自动看到新版本；也可以到「控制台 → 插件」手动检查更新。'
