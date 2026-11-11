# PotPlayer Bridge · Jellyfin 服务器插件

这是本项目两半中的**服务器侧**，装在运行 Jellyfin 的 Linux / Docker 机器上。

它负责三件事：

1. 向 `jellyfin-web` 注入客户端脚本（把网页上的播放按钮接到本机 PotPlayer）；
2. 解析「用户点的是哪一集」，生成整季播放计划（后续里程碑实现）；
3. 守卫播放进度回写，避免错误的值把观看记录改小（后续里程碑实现）。

它**不会**、也**不能**启动用户 Windows 电脑上的 PotPlayer —— 那部分在仓库另一个顶层目录
`windows-potplayer-client/` 里。两边都装好才能得到完整的播放体验。

## 目录结构

```text
server-jellyfin-plugin/
├─ Jellyfin.Plugin.PotPlayerBridge.sln
├─ Jellyfin.Plugin.PotPlayerBridge/
│  ├─ Jellyfin.Plugin.PotPlayerBridge.csproj
│  ├─ Plugin.cs                     # 插件入口
│  ├─ PluginConfiguration.cs        # 配置项（注入开关、本机代理地址）
│  ├─ PluginServiceRegistrator.cs   # 注册注入过滤器、解析器、季配置存储
│  ├─ Api/
│  │  ├─ BridgeController.cs        # Client.js / SeasonPanel.js / PlayClient.js / CSS / Version（匿名）
│  │  ├─ BridgeControllerBase.cs    # 统一 JSON / 错误结构 / 当前用户
│  │  ├─ PlaybackController.cs      # /Resolve、/ItemInfo
│  │  ├─ ProgressController.cs      # /Progress 进度回写
│  │  ├─ ItemProgressController.cs  # /ItemProgress 卡片角标批量查询
│  │  └─ SeasonSettingsController.cs# 季配置读写
│  ├─ Core/
│  │  ├─ ClientScriptInjectionFilter.cs  # 请求时改写 index.html
│  │  ├─ PlaybackResolver.cs        # 点的是谁 → 播哪一集 → 整季列表 → 从哪开始
│  │  ├─ PlaybackPlanner.cs         # 纯规则（可单测）：最近播放、S1E1、片头计算
│  │  ├─ EpisodeLibraryQuery.cs     # 12.2.0 查询封装的坑集中在这里
│  │  ├─ StreamUrlResolver.cs       # STRM 直链 / Jellyfin 直链回退
│  │  ├─ SeasonSettingsStore.cs     # season-settings.json（原子写入 + 锁）
│  │  ├─ ProgressGuard.cs           # 防倒退判定 + 会话表（纯逻辑，可单测）
│  │  ├─ PlaybackModels.cs          # 接口 DTO
│  │  └─ BridgeException.cs         # 可安全返回给前端的错误
│  ├─ Web/
│  │  ├─ clientScript.js            # 注入到网页的引导脚本
│  │  ├─ seasonPanel.js             # 季/集详情页的片头片尾面板
│  │  ├─ progressBadge.js           # 卡片图片左上角的「已看/总时长」角标
│  │  └─ seasonSettings.css         # 面板样式（只作用于 .ppb*）
│  ├─ meta.json                     # Jellyfin 插件元数据
│  ├─ logo.png
├─ manifest.json                    # Jellyfin 插件仓库清单（CI 自动维护，勿手改版本条目）
├─ scripts/
│  └─ update-manifest.ps1           # 生成/更新清单（CI 发版时调用）
├─ release.ps1                      # 本地发版：改版本号 + 提交 + 打标签 + 推送
├─ tests/Jellyfin.Plugin.PotPlayerBridge.Tests/   # xUnit：PlaybackPlanner + ProgressGuard
├─ global.json
├─ build.ps1                        # 编译 + 打包
└─ README.md                        # 本文件
```

## 前置条件

| 项目 | 要求 |
| --- | --- |
| Jellyfin | `12.2.0`（`targetAbi = 12.2.0.0`），Docker 或裸机均可 |
| 前端框架 | `net10.0`，服务器需带 .NET 10 运行时 |
| 构建机 | .NET 10 SDK（`dotnet --version` 能输出 10.x） |
| 权限 | 能写入 Jellyfin 的插件目录（Docker 通常在宿主机 `/www/jellyfin/config/plugins/`） |

> 服务器插件与本机 Windows 程序**没有代码依赖**，可以分别构建、分别安装。

## 构建

在 Windows 上（PowerShell 5.1 或 7 均可）：

```powershell
cd D:\Projects\Jellyfin-PotPlayer-Bridge2\server-jellyfin-plugin
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

产物：

```text
artifacts\Jellyfin.Plugin.PotPlayerBridge_1.0.0.0\   # dll + meta.json + logo.png
artifacts\PotPlayerBridge-Plugin-1.0.0.0.zip
```

`build.ps1` 会先校验 `csproj` 的 `<Version>` 与 `meta.json` 的 `version` 是否一致；
不一致会直接报错。改版本时两处都要改（或者直接运行 `release.ps1`，它会一起改）。

> zip 名必须保持 `PotPlayerBridge-Plugin-<version>.zip`：`manifest.json` 的 `sourceUrl`
> 指向的正是这个文件名，改名会让 Jellyfin 自动更新下载 404。

## 从插件仓库安装（推荐，可自动更新）

Jellyfin 不直接读取 GitHub Release，而是读取一份「插件仓库清单」。本仓库已经把清单放在
`server-jellyfin-plugin/manifest.json`，由 GitHub Actions 在每次发版时用实际 zip 的
MD5 自动更新。

1. 打开 Jellyfin 控制台 →「插件」→ 右上角「存储库」（Repositories）→ 添加：

   ```text
   https://raw.githubusercontent.com/Louis-15/Jellyfin-PotPlayer-Bridge/main/server-jellyfin-plugin/manifest.json
   ```

2. 保存后回到「插件」→「目录」（Catalog），找到 **PotPlayer Bridge** →「安装」。
3. 安装完成后按 Jellyfin 的提示重启。

之后每次发版，Jellyfin 会按自己的「检查插件更新」定时任务读取清单并提示（或按设置自动）
更新；也可以在「插件」页面手动点「检查更新」。更新完成后同样需要重启 Jellyfin。

> **服务器网络要求**：Jellyfin 机器必须能访问 `raw.githubusercontent.com`（拉清单）
> 以及 `github.com` / `objects.githubusercontent.com`（下载 zip）。只把清单换成
> jsDelivr 之类的镜像，并不能解决 zip 下载失败的问题。

> **从手动安装迁移**：Jellyfin 更新时会自己删除旧版本目录。如果插件列表里出现两个同名
> PotPlayer Bridge，说明旧目录没被清理，删掉旧的 `plugins/PotPlayer Bridge_*` 目录后
> 重启即可。

## 手动安装（备用）

> 服务器访问不了 GitHub 时使用这种方式。**先备份插件目录**。重启 Jellyfin 会造成几十秒不可用，请在没人看片的时候操作。

```powershell
# 1. 备份（在服务器上执行）
ssh -p 2222 root@931002.xyz "tar czf /root/jellyfin-plugins-backup-$(date +%Y%m%d-%H%M%S).tgz -C /www/jellyfin/config plugins && ls -lh /root/jellyfin-plugins-backup-*.tgz | tail -1"

# 2. 上传压缩包
scp -P 2222 "artifacts\PotPlayerBridge-Plugin-1.0.0.0.zip" root@931002.xyz:/tmp/

# 3. 解压到插件目录（目录名随意，Jellyfin 以 meta.json 为准；这里用可读的名字）
ssh -p 2222 root@931002.xyz "mkdir -p '/www/jellyfin/config/plugins/PotPlayer Bridge_1.0.0.0' && unzip -o /tmp/PotPlayerBridge-Plugin-1.0.0.0.zip -d '/www/jellyfin/config/plugins/PotPlayer Bridge_1.0.0.0'"

# 4. 重启
ssh -p 2222 root@931002.xyz "docker restart jellyfin"
```

重启后 Jellyfin 需要几十秒才能恢复，等容器状态变成 `healthy` 再验证：

```powershell
ssh -p 2222 root@931002.xyz "docker ps --filter name=jellyfin --format '{{.Status}}'"
```

## 确认装好了（四步）

1. **插件被加载**：打开 Jellyfin 控制台 →「插件」，列表里应出现 **PotPlayer Bridge**，版本 `1.0.0.0`。
2. **脚本接口可访问**：

   ```powershell
   curl.exe -sk https://931002.xyz:8443/PotPlayerBridge/Version
   # 期望：{"name":"PotPlayer Bridge","version":"1.0.0.0","targetAbi":"12.2.0.0"}

   curl.exe -sk https://931002.xyz:8443/PotPlayerBridge/Client.js
   # 期望：一段 JavaScript，第一行是注释，内容是替换过配置的 clientScript.js
   ```

3. **index.html 里出现注入标记**：

   ```powershell
   curl.exe -sk https://931002.xyz:8443/web/index.html | Select-String 'potplayer-bridge-client'
   # 期望：<!--potplayer-bridge-client--><script src="/PotPlayerBridge/Client.js" defer></script>
   ```

4. **浏览器控制台打印版本**：打开 `https://931002.xyz:8443/web/`，按 `F12` 打开控制台，
   应看到 `[PotPlayerBridge] 客户端脚本已加载，版本 1.0.0.0`。
   登录后还会再打印一条 `已连接到 Jellyfin Web（ApiClient 就绪）`。
5. **季配置面板出现**：打开任意**季详情页**或**单集详情页**，标题/播放按钮区域下方应出现
   「PotPlayer 片头片尾」面板，片头/片尾输入框显示当前值（未设置为 0），点「保存」提示
   「已保存，下次播放生效」。剧/电影详情页**不应**出现该面板。

> 注入是**请求时改写**的，不往磁盘写 `index.html`，所以 Docker 只读镜像也能用。

## 更新

### 自动更新（通过插件仓库安装后）

1. Jellyfin 控制台 →「插件」→ 点「检查更新」；
2. 有新版时点「更新」，完成后重启 Jellyfin。

也可以在「控制台 → 计划任务」里确认「检查插件更新」任务已启用，让 Jellyfin 定期自动检查。
插件目录由 Jellyfin 自己创建和清理，不要再手工往旧版本目录里覆盖 dll。

### 手动更新（没有添加插件仓库时）

1. 把 `csproj` 与 `meta.json` 的版本号改成新版本（或直接运行 `release.ps1`）；
2. 重新运行 `build.ps1`；
3. 按「手动安装」的步骤解压到**新的**版本目录（例如 `PotPlayer Bridge_1.0.0.1`）；
4. 删掉旧版本目录，重启 Jellyfin。

旧目录不删的话 Jellyfin 会同时加载两个同名插件（ID 相同），行为不可预期。

## 发布新版本（维护者）

1. 确认工作区没有其他未提交改动（`git status`），否则 `release.ps1` 里的 rebase 会失败；
2. 运行：

   ```powershell
   .\release.ps1 -Version 1.0.0.1 -Changelog "这次改了什么"
   ```

   脚本会同步修改 `csproj` / `meta.json` 的版本号、提交、追平远端 `main`、
   打 annotated tag `v1.0.0.1` 并推送。

3. GitHub Actions（`.github/workflows/build.yml`）自动完成：
   - 跑测试、编译、生成 `PotPlayerBridge-Plugin-1.0.0.1.zip`；
   - 把新版本条目（含实际 zip 的 MD5）写进 `manifest.json` 并提交回 `main`；
   - 创建 Release 并上传插件 zip。

4. Actions 变绿后，Jellyfin 的插件目录就能看到新版本。

> 手工打 tag 也可以，但必须**先把包含新版本号的提交推到 main，再推 tag**。
> 否则 CI 切到 `origin/main` 后找不到 `scripts/update-manifest.ps1`，或者版本号对不上，
> 会直接失败。

## 卸载

```powershell
ssh -p 2222 root@931002.xyz "rm -rf '/www/jellyfin/config/plugins/PotPlayer Bridge_1.0.0.0' && docker restart jellyfin"
```

卸载后网页立刻回到 Jellyfin 原生行为，不需要改任何其他配置。

## 配置项

配置保存在 Jellyfin 的插件配置目录（`/config/plugins/configurations/`）下，由 Jellyfin 自动序列化。

| 配置 | 默认值 | 说明 |
| --- | --- | --- |
| `EnableClientInjection` | `true` | 关掉后不注入脚本，网页行为与原生完全一致（用于排查问题） |
| `AgentBaseUrl` | `http://127.0.0.1:18927` | 本机 Windows 代理地址，注入脚本会向这里发请求 |
| `AgentHealthTimeoutMs` | `800` | 健康检查超时（毫秒） |
| `AgentStartTimeoutMs` | `15000` | 等待代理被拉起的总时长（毫秒） |

> 当前还没有配置页面，如需改动可直接编辑 `/config/plugins/configurations/Jellyfin.Plugin.PotPlayerBridge.xml` 后重启 Jellyfin。

季级片头片尾配置**不在这里**，单独存在插件数据目录：

```text
/config/plugins/Jellyfin.Plugin.PotPlayerBridge/season-settings.json
```

```json
{
  "items": [
    { "seasonId": "d2be84cb-...", "introSeconds": 90, "outroSeconds": 120, "updatedAt": "2026-11-06T16:08:41+00:00" }
  ]
}
```

写入采用「临时文件 + 原子改名」，文件损坏时会在下次保存前备份成 `season-settings.json.broken-<时间戳>`，不会静默丢配置。

## 接口一览

除标注「匿名」外，全部要求 Jellyfin 登录；用户 ID 从 JWT 的 `Jellyfin-UserId` 读取，不接受请求体传入。

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/PotPlayerBridge/Client.js` | 注入脚本（匿名） |
| GET | `/PotPlayerBridge/SeasonPanel.js` | 季配置面板脚本（匿名） |
| GET | `/PotPlayerBridge/PlayClient.js` | 播放按钮接管脚本（匿名，由 Client.js 在 ApiClient 就绪后加载） |
| GET | `/PotPlayerBridge/SeasonSettings.css` | 面板样式（匿名） |
| GET | `/PotPlayerBridge/Version` | 版本与 targetAbi（匿名） |
| GET | `/PotPlayerBridge/ItemInfo?itemId=` | 项类型、所属剧/季、当前季配置 |
| GET | `/PotPlayerBridge/SeasonSettings?itemId=` | 读取该集/该季的片头片尾配置 |
| POST | `/PotPlayerBridge/SeasonSettings` | 保存季配置 `{ itemId, introSeconds, outroSeconds }` |
| POST | `/PotPlayerBridge/Resolve` | 解析播放计划 `{ itemId, intent }`，intent 为 `item` / `resume` / `restart` |
| POST | `/PotPlayerBridge/Progress` | 写播放进度（带防倒退守卫），`{ playSessionId, itemId, positionTicks, durationTicks, isPaused, stopped, armed, seeked, ended }` |
| GET | `/PotPlayerBridge/ItemProgress?itemIds=` | 卡片角标：批量返回 `itemType` / 位置 / 总时长 / 是否已看（最多 100 个） |

`/Resolve` 返回的 `startPositionTicks` 已经合并了「续播 + 片头跳过」：
续播点落在片头里时跳到片头结束，已经超过片头时保持原位置（D7），`restart` 则从 0 开始但仍按季配置跳片头（D-04）。

## 只装了这一半会怎样

- 网页能正常打开、控制台能看到版本号，季/集详情页能看到配置面板，但**点播放按钮仍然走 Jellyfin 网页播放器**。
- 这是预期行为：接管按钮、拉起 PotPlayer 依赖 `windows-potplayer-client/`，且要到后续里程碑才接入。
- 反过来只装 Windows 程序，则没有任何入口能触发它——它只监听本机 `127.0.0.1:18927`。

## 当前进度（M4 已接上播放按钮）

- [x] M1：插件工程、配置类、服务注册、`index.html` 请求时注入
- [x] M1：`/PotPlayerBridge/Client.js`、`/PotPlayerBridge/Version`
- [x] M2：`PlaybackResolver` 解析 Series / Season / Episode / Movie 与 item / resume / restart
- [x] M2：整季播放列表、S1E1 兜底、最近播放排序、片头起播位置计算
- [x] M2：`SeasonSettingsStore`（原子写入 + 锁）与 `/SeasonSettings` 读写接口
- [x] M2：季/集详情页的片头片尾面板
- [x] M2：16 个单元测试覆盖排序、兜底、片头计算
- [x] M4：`PlayClient.js` 接管播放按钮（卡片 / 季页剧集行 / 详情页播放），调用本机 `/api/play`
- [x] M5：`/Progress` 进度回写 + `ProgressGuard` 防倒退守卫（未 armed 的上报只能抬高、不能倒退或清零）
- [x] M5.5：卡片进度角标（图片左上角 `16:07 / 30:22`，仅限集/电影；剧与季不显示）
- [x] M5.5：暂停/拖动/播完改用会广播的 `SaveReason`，网页立即更新（不再等半分钟）

## 故障排查

| 现象 | 先看哪里 |
| --- | --- |
| 插件列表里没有 PotPlayer Bridge | `ssh -p 2222 root@931002.xyz "docker logs --tail 200 jellyfin"` 里是否有插件加载异常；目录里是否只放了 dll（不要放 Jellyfin 的程序集） |
| 接口 404 | 容器里是否存在 `/config/plugins/PotPlayer Bridge_1.0.0.0/Jellyfin.Plugin.PotPlayerBridge.dll` 与 `meta.json` |
| `index.html` 里没有注入标记 | 配置里 `EnableClientInjection` 是否被改成 `false`；反向代理是否缓存了旧页面 |
| 控制台没有输出但接口正常 | 浏览器缓存了旧 `index.html`，强制刷新（`Ctrl+F5`） |
| 季/集详情页没有配置面板 | 控制台看有没有 `季配置面板已就绪`；`/PotPlayerBridge/SeasonPanel.js` 是否 200；`ItemInfo` 是否 200（401 说明登录态失效） |
| 保存季配置报错 | 面板会显示 `message`；服务器日志里搜 `已保存季配置` / `季配置读取失败` |
| 网页打不开 / 白屏 | 先删掉插件目录并重启，确认问题由插件引起；插件自身只在注入失败时返回原始页面，不会主动破坏网页 |
