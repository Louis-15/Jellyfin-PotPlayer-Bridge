# PotPlayer Bridge for Jellyfin

在 **Jellyfin 网页上点播放**，直接用**本机 PotPlayer** 播放，并把播放进度、片头片尾、外挂字幕都处理好。

再也不用：先把文件下载到本地 → 再拖进播放器 → 看完还得自己记进度。

---

## 目录

- [它能做什么](#它能做什么)
- [它是怎么工作的](#它是怎么工作的)
- [安装前准备](#安装前准备)
- [第一部分：安装服务器插件](#第一部分安装服务器插件)
- [第二部分：安装 Windows 本地代理](#第二部分安装-windows-本地代理)
- [验证安装是否成功](#验证安装是否成功)
- [日常使用](#日常使用)
- [片头片尾设置](#片头片尾设置)
- [配置说明](#配置说明)
- [常见问题](#常见问题)
- [卸载](#卸载)
- [从源码构建](#从源码构建)
- [已知限制](#已知限制)

---

## 它能做什么

| 功能 | 说明 |
| --- | --- |
| 网页点播 → 本机播放 | 在 Jellyfin 网页点任意一集/电影，自动在本机 PotPlayer 打开，不用下载 |
| 整季播放列表 | 点第 4 集，PotPlayer 里是整季列表并从第 4 集开始，播完自动下一集 |
| 进度同步 | 播放进度实时回写 Jellyfin，「继续观看」在手机、电视、网页上都同步 |
| 续播 | 接着上次的位置继续播，不是从头开始 |
| 防进度倒退 | 播放器异常读到 0 时不会把你的进度冲掉 |
| 片头跳过 | 按季配置片头秒数，每集起播自动跳过片头（含自动连播的下一集） |
| 片尾跳过 | 按季配置片尾秒数，到片尾提前跳下一集，并把这集标记为已看完 |
| 外挂字幕 | 电影/单片视频自动挂载 Jellyfin 里的外挂中文字幕 |
| 卡片时长角标 | 网页卡片左上角显示 `16:08 / 30:22`，并自动修正 STRM 缺失的时长元数据 |

---

## 它是怎么工作的

本项目由**两个必须同时安装的部分**组成：

```text
┌──────────────────────────┐          ┌──────────────────────────┐
│  Jellyfin 服务器          │          │  你的 Windows 电脑        │
│                          │          │                          │
│  PotPlayer Bridge 插件    │          │  PotPlayer Bridge 代理    │
│  （装在 Jellyfin 上）      │          │  （托盘程序）             │
│                          │          │         │                │
│  ① 往网页注入脚本 ────────┼──────────┼─→ ② 接收播放请求          │
│  ④ 写回播放进度   ←───────┼──────────┼── ③ 启动 PotPlayer 播放   │
└──────────────────────────┘          └──────────────────────────┘
```

- **服务器插件**：向 Jellyfin 网页注入脚本、解析「你点的是哪一集」、生成整季播放计划、保存片头片尾配置、守卫进度回写。
- **本地代理**：真正的执行者 —— 生成播放列表、启动 PotPlayer、监控播放位置、把进度发回服务器。

> ⚠️ **只装一个是不行的。** 服务器插件无法启动你电脑上的程序；本地代理也拿不到 Jellyfin 的播放意图。

---

## 安装前准备

### 服务器侧

| 项目 | 要求 |
| --- | --- |
| Jellyfin | **12.2.0**（插件 `targetAbi = 12.2.0.0`） |
| 运行时 | .NET 10（官方 `jellyfin/jellyfin` 12.2.0 镜像已自带） |
| 部署方式 | Docker 或裸机均可 |

> 插件版本与 Jellyfin 版本强绑定。如果你的 Jellyfin 不是 12.2.0，插件会加载失败并在日志里报 ABI 不匹配。

### 本机侧

| 项目 | 要求 |
| --- | --- |
| 操作系统 | Windows 10 / 11（x64） |
| PotPlayer | 已安装（推荐 64 位版 `PotPlayerMini64.exe`） |
| 权限 | **不需要管理员**，全部装在当前用户目录 |
| 网络 | 本机能访问你的 Jellyfin 服务器 |

代理会通过 8 种方式自动查找 PotPlayer（注册表、常见安装路径、开始菜单快捷方式等）。装在非标准位置也能找到；实在找不到可在设置里手动指定。

---

## 第一部分：安装服务器插件

### 方式一：插件仓库（推荐，可自动更新）

在 Jellyfin 控制台 →「插件」→ 右上角「存储库」（Repositories）→ 添加：

```text
https://raw.githubusercontent.com/Louis-15/Jellyfin-PotPlayer-Bridge/main/server-jellyfin-plugin/manifest.json
```

保存后回到「插件」→「目录」（Catalog），找到 **PotPlayer Bridge** →「安装」，然后按提示重启 Jellyfin。

之后每次发版，Jellyfin 会按「检查插件更新」定时任务读取清单，也可以在「插件」页面手动点
「检查更新」。更新完成后同样需要重启 Jellyfin。

> Jellyfin 机器需要能访问 `raw.githubusercontent.com`（清单）以及 `github.com` /
> `objects.githubusercontent.com`（下载 zip）。服务器访问不了 GitHub 时，用下面的手动安装。

### 方式二：手动安装（服务器不能访问 GitHub 时）

1. **下载插件包**

   到 [Releases](https://github.com/Louis-15/Jellyfin-PotPlayer-Bridge/releases/latest) 页面下载最新的 **`PotPlayerBridge-Plugin-*.zip`**（注意不要下成 Agent 那个包）。

   下面命令里的 `1.0.0.0` 只是示例，实际以 Releases 页面的版本号为准。

2. **解压到 Jellyfin 插件目录**

   Jellyfin 的插件目录是 `<配置目录>/plugins/`。在它下面**新建一个文件夹**（名字随意，建议 `PotPlayerBridge`），把 zip 里的内容解压进去。

   **Docker 部署**（最常见）：

   ```bash
   # 在宿主机上，把 zip 传到服务器
   scp PotPlayerBridge-Plugin-1.0.0.0.zip user@your-server:/tmp/

   # 登录服务器后执行（把 /path/to/jellyfin/config 换成你自己的配置目录）
   mkdir -p /path/to/jellyfin/config/plugins/PotPlayerBridge
   unzip -o /tmp/PotPlayerBridge-Plugin-1.0.0.0.zip -d /path/to/jellyfin/config/plugins/PotPlayerBridge
   ```

   如果你的 Jellyfin 用 compose 部署，配置目录一般是 `./config`：

   ```bash
   mkdir -p ./config/plugins/PotPlayerBridge
   unzip -o /tmp/PotPlayerBridge-Plugin-1.0.0.0.zip -d ./config/plugins/PotPlayerBridge
   ```

   **裸机 / Windows 部署**：

   - Linux：解压到 `/var/lib/jellyfin/plugins/PotPlayerBridge/`
   - Windows：解压到 `%PROGRAMDATA%\Jellyfin\Server\plugins\PotPlayerBridge\`

3. **确认文件结构正确**

   解压后目录里应该**直接**看到这些文件（不要再套一层文件夹）：

   ```text
   plugins/PotPlayerBridge/
   ├─ Jellyfin.Plugin.PotPlayerBridge.dll
   ├─ meta.json
   └─ logo.png
   ```

   > 如果变成了 `plugins/PotPlayerBridge/PotPlayerBridge-Plugin-1.0.0.0/...`，说明多套了一层，插件不会被加载。把里面的文件移上一层即可。

4. **重启 Jellyfin**

   ```bash
   docker restart jellyfin
   ```

   裸机部署：

   ```bash
   sudo systemctl restart jellyfin
   ```

5. **确认插件已加载**

   打开 Jellyfin → 控制台 → 插件，应该能看到 **PotPlayer Bridge 1.0.0.0**。

   或者在服务器上验证接口：

   ```bash
   curl -k https://你的服务器/PotPlayerBridge/Version
   # 期望输出：{"name":"PotPlayer Bridge","version":"1.0.0.0","targetAbi":"12.2.0.0"}
   ```

### 方式三：从源码构建

见 [从源码构建](#从源码构建)。

---

## 第二部分：安装 Windows 本地代理

> 服务器插件装好之前不要装代理，否则网页上的播放按钮不会被接管。

1. **下载代理包**

   到 [Releases](https://github.com/Louis-15/Jellyfin-PotPlayer-Bridge/releases) 页面下载最新的 **`PotPlayerBridge-Agent-*.zip`**。

   > 服务器插件与本地代理是两条独立的发布链路。如果最新 Release 里没有 Agent 包，
   > 到 [`v1.0.0.0`](https://github.com/Louis-15/Jellyfin-PotPlayer-Bridge/releases/tag/v1.0.0.0) 下载，或从源码构建。

2. **解压**

   解压到任意目录，例如 `D:\PotPlayerBridge\`。解压后应该有：

   ```text
   PotPlayerBridge-Agent-1.0.0.0/
   ├─ PotPlayerBridge.exe      ← 主程序（自包含，无需装 .NET 运行时）
   ├─ install.ps1              ← 安装脚本
   └─ uninstall.ps1            ← 卸载脚本
   ```

3. **运行安装脚本**

   在解压目录里**右键 → 在终端中打开**（或按住 Shift 右键 → 「在此处打开 PowerShell 窗口」），执行：

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install.ps1
   ```

   脚本会做三件事（全部在当前用户范围，**不需要管理员**）：

   - 把 `PotPlayerBridge.exe` 复制到 `%LocalAppData%\PotPlayerBridge\`
   - 注册 `potplayerbridge://` 协议（用于网页唤起）
   - 启动代理

4. **确认托盘图标出现了**

   启动后程序常驻系统托盘。Windows 11 默认会把它折叠起来，点任务栏的 **`^`**（显示隐藏的图标）就能看到。

   如果托盘图标看不到，用这个命令确认进程是否在跑：

   ```powershell
   Get-Process PotPlayerBridge
   ```

5. **确认代理工作正常**

   ```powershell
   curl.exe -H "X-PPB-Client: 1" http://127.0.0.1:18927/health
   ```

   期望输出类似：

   ```json
   {"ok":true,"version":"1.0.0.0","port":18927,"uptimeSeconds":42,
    "potPlayerPath":"C:\\Program Files\\DAUM\\PotPlayer\\PotPlayerMini64.exe",
    "potPlayerSource":"registry"}
   ```

   - `potPlayerPath` 有值 → 找到了 PotPlayer
   - `"potPlayerPath": null` → 没找到，右键托盘图标 → 「设置」→ 手动指定 `PotPlayerMini64.exe` 路径

---

## 验证安装是否成功

两项都装好后，做一次端到端验证：

1. 打开 Jellyfin 网页，进任意一部剧的**季页面**或**集详情页**
2. 点击某一集的**播放按钮**
3. 预期现象：
   - 弹出提示「正在唤起本机播放器」（或类似提示）
   - 本机 PotPlayer 自动打开，播放你点的那一集
   - PotPlayer 的播放列表里是**整季**
   - Jellyfin 网页上出现进度条

**如果点击播放后网页播放器照常播放（没有唤起 PotPlayer）**，说明注入脚本没生效，检查：

- 服务器插件是否真的加载了（控制台 → 插件）
- 是否用 `https://` 访问 Jellyfin（脚本对来源有校验）
- 浏览器控制台（F12）有没有 `[PotPlayerBridge]` 开头的日志

---

## 日常使用

### 播放入口

| 位置 | 行为 |
| --- | --- |
| 集详情页「播放」按钮 | 播放该集，PotPlayer 里是整季列表 |
| 季页面某一集 | 同上 |
| 电影详情页「播放」按钮 | 播放该电影（单集模式，字幕会自动挂载） |
| 「继续观看」卡片 | 从上次位置继续 |

### 进度同步

播放进度会自动回写 Jellyfin：

- **每 5 秒**写一次数据（保证续播位置精确）
- **每 60 秒**通知网页刷新一次（避免把所有打开的网页刷得太频繁）
- **暂停、拖动、播完、换集、关闭播放器**时立即刷新网页

所以手机上、电视上的 Jellyfin 客户端也能很快看到你看到哪了。

### 关闭代理

右键托盘图标 → 「退出」。退出前会把当前播放位置上报一次，不会丢进度。

---

## 片头片尾设置

片头片尾是**按季**配置的（同一季的集通常片头长度一致）。

1. 打开某一季的页面（不是剧页面，是季页面）
2. 页面上方会出现 **「PotPlayer 片头片尾」** 面板
3. 填入这一季的片头秒数和片尾秒数，保存

设置后的行为：

| 配置 | 行为 |
| --- | --- |
| 片头 = 120 | 每集起播自动跳到 2:00（含自动连播的下一集） |
| 片尾 = 150 | 播放到「总时长 − 150 秒」时自动跳下一集，并把这一集标记为**已看完** |
| 片头/片尾 = 0 | 不跳过（默认） |

**注意事项**：

- 续播的集会优先用续播位置，不会因为配了片头就把你拽回片头
- 最后一集不会跳片尾（让它正常播完）
- 电影不会跳片尾（很多电影片尾有彩蛋）

---

## 配置说明

### 服务器插件配置

控制台 → 插件 → PotPlayer Bridge → 设置：

| 配置项 | 默认值 | 说明 |
| --- | --- | --- |
| `EnableClientInjection` | `true` | 是否注入网页脚本。关掉后网页恢复 Jellyfin 原生行为，用于排查问题 |
| `AgentBaseUrl` | `http://127.0.0.1:18927` | 本机代理地址。注入脚本只允许访问本机回环地址 |
| `AgentHealthTimeoutMs` | `800` | 健康检查超时（毫秒） |
| `AgentStartTimeoutMs` | `15000` | 等待代理启动的总时长（毫秒） |

> 本机代理的端口固定是 **18927**。如果你改了代理端口，这里也要跟着改。

### 本地代理配置

右键托盘图标 → 「设置」，或在浏览器打开 `http://127.0.0.1:18927/settings`。

配置文件位置：`%LocalAppData%\PotPlayerBridge\config.json`

| 配置项 | 说明 |
| --- | --- |
| `PotPlayerPath` | PotPlayer 可执行文件路径。留空则自动查找 |

### 数据文件位置

**服务器侧**（`<插件数据目录>/`，Docker 一般是 `config/plugins/Jellyfin.Plugin.PotPlayerBridge/`）：

| 文件 | 说明 |
| --- | --- |
| `season-settings.json` | 按季保存的片头片尾配置 |
| `runtime-durations.json` | 实测时长的本地兜底记录 |

**本机侧**（`%LocalAppData%\PotPlayerBridge\`）：

| 文件 / 目录 | 说明 |
| --- | --- |
| `PotPlayerBridge.exe` | 主程序 |
| `config.json` | 代理配置 |
| `agent.log` | 运行日志（排查问题先看这个） |
| `playlists\` | 生成的 `.dpl` 播放列表 |
| `subtitles\` | 下载的外挂字幕（7 天后自动清理） |
| `potplayer-session.json` | 当前拉起的 PotPlayer 进程记录（异常退出后用于清理） |

---

## 常见问题

### 网页上点播放没有反应，还是 Jellyfin 自己的播放器

1. 打开浏览器控制台（F12），看有没有 `[PotPlayerBridge]` 开头的日志
2. 没有日志 → 服务器插件没生效。确认插件已加载，并**强制刷新**网页（Ctrl+F5）
3. 有日志但报错 → 看日志里的错误信息。提示「代理未运行」就是本地代理没启动

### 提示「代理未运行」

```powershell
Get-Process PotPlayerBridge
```

没有输出说明代理没在跑。从开始菜单或 `%LocalAppData%\PotPlayerBridge\PotPlayerBridge.exe` 启动它。

### 提示「没有找到 PotPlayer」

右键托盘 → 「设置」→ 手动指定 `PotPlayerMini64.exe` 的完整路径（通常在 `C:\Program Files\DAUM\PotPlayer\`）。

### 播放了但是没有进度同步

1. 看代理日志：`%LocalAppData%\PotPlayerBridge\agent.log`
2. 搜 `进度已写入` —— 有这条说明上报成功
3. 搜 `续播跳转` / `片头跳转` —— 说明起播跳转生效
4. 搜 `跳转多次未生效` —— 说明 PotPlayer 没有响应跳转命令，通常是视频还没加载完

### 「继续观看」里堆了同一部剧的好几集

这是旧版本（1.0.0.0 之前）的已知问题：跳过片尾时没有把那一集标记为已看完。

**已修复**。处理历史遗留数据：在网页上把那几集手动标记为已看完即可。

### 卡片左上角的时长角标不显示数字

角标需要「总时长」才能显示 `16:08 / 30:22`。Jellyfin 的时长来自扫描媒体库时的探测，而 **STRM 只是文本指针，扫描器不会为了读时长去拉远端视频**，所以这些条目一开始没有时长。

**播一次就有了。** 代理在播放时能读到真实时长，会写回 Jellyfin 数据库（`RunTimeTicks`），之后所有用户、所有客户端都能看到这个数字，Jellyfin 自己的排序筛选也能用上。

### 外挂字幕没有自动加载

当前的设计：

- **电影 / 单片视频**：自动挂载外挂中文字幕（单集模式）
- **剧集**：走整季播放列表，不自动挂字幕

原因：实测 PotPlayer **只有直接播放视频地址时**才接受 `/sub=` 字幕参数；一旦用播放列表（`.dpl`）启动，字幕参数会被丢弃，而 DPL 格式本身没有字幕字段。（详细实测矩阵见 [PLAN.md](PLAN.md) 第 6.8 节）

所以剧集建议用**内嵌字幕**的片源。

### 片头配置了但没有跳过

- 该集是**续播**：续播位置优先于片头，不会把你拽回片头
- 日志里搜 `片头跳转` / `续播跳转`，看跳转是否执行
- 如果搜到 `跳转多次未生效`，说明 PotPlayer 没响应跳转命令（视频还没加载完），本次不写进度以避免污染记录

### Jellyfin 更新后插件失效

插件与 Jellyfin 版本强绑定（`targetAbi`）。Jellyfin 大版本升级后需要对应版本的插件。

如果通过插件仓库安装，升级 Jellyfin 后到「插件」页面检查更新即可；清单里保留了历史版本，
新版本还没发布时可以继续使用与当前 Jellyfin 兼容的旧版本。

---

## 卸载

### 卸载本地代理

在安装目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
```

或手动删除：

```powershell
# 退出程序
Stop-Process -Name PotPlayerBridge -ErrorAction SilentlyContinue

# 删除协议注册
Remove-Item 'HKCU:\Software\Classes\potplayerbridge' -Recurse -Force -ErrorAction SilentlyContinue

# 删除程序和所有数据（配置、日志、播放列表、字幕）
Remove-Item "$env:LOCALAPPDATA\PotPlayerBridge" -Recurse -Force
```

### 卸载服务器插件

1. 控制台 → 插件 → PotPlayer Bridge → 卸载
2. 或直接删除插件目录，然后重启 Jellyfin：

   ```bash
   rm -rf /path/to/jellyfin/config/plugins/PotPlayerBridge
   docker restart jellyfin
   ```

3. 可选：删除插件遗留的季配置

   ```bash
   rm -rf /path/to/jellyfin/config/plugins/Jellyfin.Plugin.PotPlayerBridge
   ```

---

## 从源码构建

需要 **.NET 10 SDK**（`dotnet --version` 输出 10.x）。

```powershell
git clone https://github.com/Louis-15/Jellyfin-PotPlayer-Bridge.git
cd Jellyfin-PotPlayer-Bridge
```

### 构建服务器插件

```powershell
cd server-jellyfin-plugin
.\build.ps1
```

产物：

- `artifacts/Jellyfin.Plugin.PotPlayerBridge_1.0.0.0/` — 可直接解压到插件目录
- `artifacts/PotPlayerBridge-Plugin-1.0.0.0.zip` — 打包好的压缩包（与 Release 资产同名）

### 构建本地代理

```powershell
cd windows-potplayer-client
.\build.ps1          # 发布自包含单文件
.\install.ps1        # 安装到当前用户
```

产物：`artifacts/PotPlayerBridge/PotPlayerBridge.exe`（自包含，约 53 MB，目标机无需装 .NET）

### 运行测试

```powershell
cd server-jellyfin-plugin
dotnet test            # 46 个用例

cd windows-potplayer-client
dotnet test            # 18 个用例
```

---

## 已知限制

| 限制 | 原因 |
| --- | --- |
| 只支持 Windows 本机播放 | PotPlayer 只有 Windows 版 |
| 只支持 Jellyfin 12.2.0 | 插件 ABI 强绑定 |
| 剧集不能自动挂外挂字幕 | PotPlayer 的 DPL 播放列表没有字幕字段（见上文） |
| PotPlayer 播放列表里第一项带 `*` 前缀 | PotPlayer 对「当前播放项」的标记，实测无法去掉 |
| 集与集之间有小段黑屏 | 播放列表切集时 PotPlayer 的正常行为 |
| 需要 Jellyfin 用 https 访问 | 脚本对来源有校验 |

---

## 相关文档

| 文档 | 内容 |
| --- | --- |
| [PLAN.md](PLAN.md) | 完整实施计划、所有实测结论、踩坑记录 |
| [REQUIREMENTS.md](REQUIREMENTS.md) | 需求与验收项 |
| [server-jellyfin-plugin/README.md](server-jellyfin-plugin/README.md) | 服务器插件开发文档 |
| [windows-potplayer-client/README.md](windows-potplayer-client/README.md) | 本地代理开发文档 |

---

## 许可

[MIT License](LICENSE) © 2026 Louis-15

你可以自由使用、修改、分发本项目（包括商用），只需保留版权声明和许可声明。
本软件按「原样」提供，不附带任何担保。
