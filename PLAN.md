# Jellyfin → PotPlayer 播放体验项目 · 实施计划

> **状态：计划已定稿，M0 技术验证已完成，尚未开始写正式代码。**
> 依据：`REQUIREMENTS.md`（需求文档，验收标准的唯一来源）。
> 本文档是**自包含**的：所有关键结论、实测数据、决策、坑都在这里，接手者不需要读历史对话。
> 如果你是这个项目的新接手者，**先读第 17 节「交接说明」**，再读第 0、1 节。

---

## 0. 一句话方案

在 Jellyfin 服务器上装一个插件负责「点的是哪一集、从哪开始、列表里有什么、片头片尾多少秒」；在 Windows 本机装一个小代理负责「拉起 PotPlayer、生成整季播放列表、读取进度、自动跳过、回写进度」。代理不做开机自启，第一次点播放时由网页按需拉起，之后常驻。

浏览器里只注入一小段脚本，把 Jellyfin 原本的播放按钮接管为「向本机代理发一个播放请求」，**不保留网页播放器路径**。

---

## 1. 已勘察到的客观事实

以下不是猜测，是已经在目标环境上实际验证过的。

### 1.1 Jellyfin 服务器

| 项目 | 实测结果 |
| --- | --- |
| 容器 | `jellyfin-shimmed:latest`，容器名 `jellyfin` |
| 版本 | **12.2.0**，运行时 **net10.0** |
| 访问地址 | `https://931002.xyz:8443`，Caddy 反代 |
| 媒体挂载 | `/opt/litepan/strm` → 容器内 `/media/strm` |
| STRM 数量 | 2619 个 |
| 已有插件 | MetaTube、SubtitleCat、MetaShark、Thunder Subtitle、AV Collection |
| 认证 | `Authorization: MediaBrowser Token="..."` 可用；`X-Emby-Token` 在 12.2 已返回 401 |

### 1.2 STRM 与字幕

实测一集 `誓言无声 S01E04`：

- `.strm` 文件内容是一行直链：
  `http://931002.xyz:5211/api/strm/play/1/.../S01E04.ts`
- Jellyfin 的 `PlaybackInfo` 返回：
  - `MediaSources[0].Path` 就是这条 HTTP 直链
  - `Protocol = Http`
  - `SupportsDirectPlay = true`
- 外挂字幕会出现在 `MediaStreams`：
  - `Type = Subtitle`
  - `IsExternal = true`
  - `Path = /media/strm/.../SNOS-152.chi.srt`
  - `Language = zho`
- 电视剧目录当前没有外挂字幕；AV 目录有大量 `.chi.srt`，可用于字幕功能验证。

**结论：PotPlayer 可以直接播放 `MediaSources[0].Path`，不需要经过 Jellyfin 的转码或 HTTPS 证书。** 这是首选播放地址。

### 1.3 Jellyfin Web 的 DOM

在 12.2.0 的实际页面上验证：

- 首页区块：`.homeSectionsContainer .verticalSection`
- 「继续观看」区块：没有 `type=nextup` 链接
- 「接下来」区块：标题容器里有 `a[href*="type=nextup"]`
- 卡片：`.card[data-id]`，带 `data-type`、`data-positionticks`、`data-context`
- 播放按钮：
  - 卡片悬浮按钮：`[data-action="resume"]`
  - 移动端/覆盖层按钮：`[data-action="play"]`
  - 详情页：`.btnPlay`（`data-action="resume"`）与 `.btnReplay`（`data-action="play"`）
- 收藏/标记已看/更多：`data-action="none"` / `"link"` / `"menu"`，不会被误伤
- 详情页有缓存 DOM，必须取**当前可见**的 `.itemDetailPage`，不能直接 `querySelector` 第一个

### 1.4 PotPlayer 本机情况

| 项目 | 实测结果 |
| --- | --- |
| 版本/路径 | `D:\Program Files\PotPlayer\PotPlayerMini64.exe` |
| 注册表 | `HKLM\SOFTWARE\DAUM\PotPlayer64\ProgramPath` |
| 协议 | `HKLM\SOFTWARE\Classes\potplayer\shell\open\command` |
| 进程 | 本机已有一个用户正在使用的 PotPlayer 实例 |

### 1.5 PotPlayer 控制接口（关键验证）

通过 `SendMessage` 向 PotPlayer 窗口发送消息，已实测可用：

| 消息 | wParam | 含义 | 实测 |
| --- | --- | --- | --- |
| `WM_USER (0x0400)` | `0x5002` | 总时长（毫秒） | ✅ 返回 2724718 |
| `WM_USER (0x0400)` | `0x5004` | 当前播放位置（毫秒） | ✅ 返回 18379 |
| `WM_USER (0x0400)` | `0x5006` | 状态（-1 停止 / 1 暂停 / 2 播放） | ✅ 返回 1 |
| `WM_USER (0x0400)` | `0x5005` | 跳转到指定毫秒 | 待实现时验证 |
| `WM_COMMAND (0x0111)` | `10124` | 下一集 | 社区资料 + 待验证 |
| `WM_COMMAND (0x0111)` | `10123` | 上一集 | 社区资料 |
| `WM_COMMAND (0x0111)` | `20002` | 停止 | 社区资料 |
| `WM_COMMAND (0x0111)` | `10126` | 字幕显示开/关 | 社区资料 |
| `WM_COMMAND (0x0111)` | `10172` | 字幕菜单 | 社区资料 |

窗口类名：`PotPlayer64`（Mini 版也是这个类名）。窗口标题形如 `剧集文件名 - PotPlayer`，可用于辅助判断当前集。

> 这是 PotPlayer 的非公开接口，但在当前版本实测有效。计划里会把它作为主通道，并保留 SMTC / 窗口标题作为回退。

### 1.6 DPL 播放列表格式（关键验证）

实测 `.dpl` 是 UTF-8 文本：

```
DAUMPLAYLIST
playname=<当前播放项的地址>
topindex=<当前播放项序号，1 开始>
saveplaypos=0
1*file*<地址>
1*title*<标题>
1*sub*<该集字幕的本地绝对路径>   ← 可选，整季连播时逐集生效
2*file*<地址>
2*title*<标题>
2*sub*<该集字幕的本地绝对路径>
```

实测结论：

- `topindex=2` 会**直接播放第 2 项**，不是从第 1 项开始。
- 一集播完会**自动播下一项**；最后一项播完后状态变为 `0`（停止），**不会自动循环回第一项**。
- `playname` 必须是真实可播放的地址；写成中文列表名会报「无法找到该文件」。
- 主窗口顶部会显示 `[2/3] 标题`，能看出当前是列表第几项。
- **`N*sub*` 字段逐项生效**（已实测）：第 1 集播完后自动切到第 2 集，第 2 集自动加载它自己的 `2*sub*` 字幕。
  这是字幕方案的主通道，详见 6.8。
- **列表名称的正确做法（已实测确认）**：
  - 把 `.dpl` **放进 PotPlayer 的 `Playlist` 目录**，**文件名（去掉 `.dpl`）就是播放列表窗口里的标签页名称**。
  - 例：`D:\Program Files\PotPlayer\Playlist\誓言无声 第1季.dpl` → 标签页显示 `誓言无声 第1季`。
  - 该做法**不影响播放**：整季列表、`topindex` 起播集、自动连播全部正常。
  - 标签名前会出现 `*`（PotPlayer 标记"该专辑有未保存改动"）。这是 PotPlayer 自身行为，无法通过 API 去掉；正常关闭一次后通常消失。**可接受**。

- **不要用 `/title=` 来设置列表名**：实测 `/title=` 与 DPL 同时使用会让列表不播放（窗口停在「即将播放…」）。本方案**禁用该参数**。

- **安全注意（保留为防御性约束）**：调研过程中曾观察到 `Playlist` 目录里的其它 `.dpl` 消失，一度怀疑是 PotPlayer 清理所致；**后经确认该文件是用户本人删除，与本程序无关**。但「强制结束 PotPlayer 进程可能让它下次退出时改写/清理专辑目录」这一行为尚未排除，因此本方案仍保留以下约束：
  1. 写入 `Playlist` 目录前后都要对整个目录做快照备份；
  2. 只增删本程序自己记录在案的文件，绝不碰其他 `.dpl`；
  3. **禁止强杀 PotPlayer**，只能优雅关闭（详见 6.5）。

---

## 2. 总体架构

```
┌──────────────────────────── 浏览器（Windows 本机） ────────────────────────────┐
│  Jellyfin Web (https://931002.xyz:8443)                                      │
│    └─ 注入的 clientScript.js                                                 │
│         ├─ 截获 .btnPlay / .btnReplay / 卡片播放按钮                          │
│         ├─ 判断意图：item / resume / restart                                 │
│         └─ POST http://127.0.0.1:18927/api/play                             │
└───────────────────────────────────┬──────────────────────────────────────────┘
                                    │ localhost + CORS
┌───────────────────────────────────▼──────────────────────────────────────────┐
│  Windows 本地代理 PotPlayerBridge.exe（托盘常驻，无管理员权限）                │
│    ├─ HTTP 服务（127.0.0.1:18927）                                            │
│    ├─ 播放器路径发现 / 手动指定 / 记忆                                        │
│    ├─ 调用 Jellyfin 插件 /Resolve 拿播放计划                                  │
│    ├─ 生成整季 DPL，拉起 PotPlayer                                            │
│    ├─ 用窗口消息读进度、跳片头、跳片尾、下一集                                 │
│    ├─ 下载外挂字幕并写入 DPL 的 N*sub*                                        │
│    └─ 定期调用 /Progress 回写 Jellyfin                                       │
└───────────────────────────────────┬──────────────────────────────────────────┘
                                    │ HTTPS（带用户 Token）
┌───────────────────────────────────▼──────────────────────────────────────────┐
│  Jellyfin 插件 Jellyfin.Plugin.PotPlayerBridge                               │
│    ├─ index.html 注入 clientScript.js                                        │
│    ├─ /Resolve：解析「点的是谁 → 播哪一集 → 整季列表 → 从哪开始」             │
│    ├─ /Progress：带防倒退守卫地写用户观看进度                                 │
│    ├─ /SeasonSettings：按季保存片头/片尾秒数                                  │
│    └─ 季/集详情页内嵌配置面板                                                 │
└──────────────────────────────────────────────────────────────────────────────┘
```

### 职责边界

| 层 | 只负责 | 不负责 |
| --- | --- | --- |
| 注入脚本 | DOM 识别、意图判断、调用本机代理、错误提示 | 不决定播哪一集、不拼 URL、不写进度 |
| Windows 代理 | 拉起/监控 PotPlayer、播放列表、字幕、进度上报 | 不猜测 Jellyfin 的剧集语义 |
| Jellyfin 插件 | 解析语义、查用户进度、生成播放计划、防倒退写进度、季配置 | 不启动本机程序、不碰 PotPlayer |

这样设计的原因：需求 2 的六种按钮语义全在服务器解析，注入脚本只回答「用户点的是哪个 DOM、属于哪种意图」，避免前端因为剧集/季/集的层级不同而写六套逻辑。

---

## 3. 目录结构（两个独立顶层目录）

```text
Jellyfin-PotPlayer-Bridge2/
├─ README.md                         # 总览：两部分分别装在哪、安装顺序、只装一半的现象
├─ PLAN.md                           # 本文件
├─ docs/
│  ├─ PROTOCOL.md                    # 插件 API / 本机代理 API 的共享协议
│  └─ TESTING.md                     # 逐条验收步骤（A1-H3）
├─ server-jellyfin-plugin/           # 装到 Jellyfin 服务器（Linux/Docker）
│  ├─ Jellyfin.Plugin.PotPlayerBridge.sln
│  ├─ Jellyfin.Plugin.PotPlayerBridge/
│  │  ├─ Jellyfin.Plugin.PotPlayerBridge.csproj
│  │  ├─ Plugin.cs
│  │  ├─ PluginConfiguration.cs
│  │  ├─ PluginServiceRegistrator.cs
│  │  ├─ Api/BridgeController.cs
│  │  ├─ Core/ClientScriptInjectionFilter.cs
│  │  ├─ Core/PlaybackResolver.cs
│  │  ├─ Core/ProgressGuard.cs
│  │  ├─ Core/SeasonSettingsStore.cs
│  │  ├─ Core/ItemQuery.cs
│  │  ├─ Web/clientScript.js
│  │  ├─ Web/seasonSettings.css
│  │  └─ meta.json / logo.png
│  ├─ tests/Jellyfin.Plugin.PotPlayerBridge.Tests/
│  ├─ build.ps1
│  └─ README.md
└─ windows-potplayer-client/         # 装到用户 Windows 电脑
   ├─ PotPlayerBridge.sln
   ├─ PotPlayerBridge/               # net10.0-windows10.0.19041.0, WinForms 托盘
   │  ├─ PotPlayerBridge.csproj
   │  ├─ Program.cs
   │  ├─ TrayApplicationContext.cs
   │  ├─ Http/LocalHttpServer.cs
   │  ├─ Http/Routes.cs
   │  ├─ Core/PotPlayerLocator.cs
   │  ├─ Core/PotPlayerLauncher.cs
   │  ├─ Core/DplBuilder.cs
   │  ├─ Core/PotPlayerMonitor.cs
   │  ├─ Core/ProgressReporter.cs
   │  ├─ Core/JellyfinClient.cs
   │  ├─ Core/SubtitleService.cs
   │  ├─ Core/ConfigStore.cs
   │  ├─ SettingsForm.cs
   │  └─ app.manifest
   ├─ tests/PotPlayerBridge.Tests/
   ├─ build.ps1
   ├─ install.ps1
   ├─ uninstall.ps1
   └─ README.md
```

**硬性约束：**

- 两个目录互不引用源码，不共用根级构建脚本。
- 共享协议只写在 `docs/PROTOCOL.md` 里，两边各自实现；不靠相对路径引用。
- 单个目录拿走即可独立构建/安装。

---

## 4. 播放意图模型（需求 2 的统一解法）

注入脚本只产生四种意图：

| 意图 | 触发位置 | 服务器解析规则 |
| --- | --- | --- |
| `item` | 普通的播放按钮：主页剧集卡片、季卡片、集卡片、详情页 `.btnPlay`、电影 | 按 `item.Type` 分派：Series/Season 找「最近看的那一集」；Episode/Movie 播它自己 |
| `resume` | 「继续观看」卡片 | 播卡片上的**这一集/这部电影**，从用户进度续播 |
| `restart` | 「接下来」卡片、`.btnReplay`、重播按钮 | 播目标项并**从 0 开始**；Series/Season 目标是「第一季第一集/本季第一集」 |
| （不拦截） | 收藏、标记已看、更多、随机播放等 | 保持 Jellyfin 原行为 |

### 4.1 具体映射表

| 需求 | DOM 位置 | 意图 | 服务器实际播 |
| --- | --- | --- | --- |
| 2.1 主页剧集卡片 | `.card[data-type=Series]` 的播放按钮 | `item` | 全剧最近有进度的一集；否则 S1E1 |
| 2.2 剧集详情页的季播放按钮 | `.card[data-type=Season]` 的播放按钮 | `item` | 该季最近有进度的一集；否则本季 E1 |
| 2.3 季页面某集播放按钮 | `.card[data-type=Episode]` 的播放按钮 | `item` | 这一集；有进度续播 |
| 2.4 集详情页播放按钮 | `.btnPlay`（当前 item 是 Episode） | `item` | 这一集；有进度续播 |
| 2.5 继续观看 | 含 `data-positionticks` 的卡片，所在区块无 `type=nextup` 链接 | `resume` | 卡片上那一集/那部电影，续播 |
| 2.6 接下来 | 所在区块含 `a[href*="type=nextup"]` | `restart` | 卡片上那一集，从 0 开始 |
| 2.7 电影详情 | `.btnPlay`（当前 item 是 Movie） | `item` | 电影；有进度续播 |
| 2.8 重播 | `.btnReplay` | `restart` | 目标项从 0 开始 |
| B11 其他按钮 | `data-action=none/link/menu` | — | 不拦截 |

### 4.2 为什么「继续观看」和「接下来」不会混

- 「接下来」区块的标题容器里有 `a[href*="type=nextup"]`，这是 Jellyfin 自己生成的链接，和语言无关。
- 「继续观看」区块没有这个链接。
- 即使某个「接下来」条目意外带有 `data-positionticks`，只要识别到 `type=nextup`，就强制 `restart`，满足 2.6。

### 4.3 防止误伤

- 只在 `click` 捕获阶段拦截 `[data-action="play|resume"]` 或 `.btnPlay/.btnReplay`。
- 不拦截卡片本身的 `data-action="link"`。
- 不拦截 `.btnPlaystate`、`.btnUserRating`、`.btnMoreCommands`、`.btnShuffle`（见决策 D-06）。
- 不在 `#/video` 页面做任何拦截。

---

## 5. Jellyfin 插件设计

### 5.1 工程基线

| 项目 | 值 |
| --- | --- |
| TargetFramework | `net10.0` |
| Jellyfin.Controller | `12.2.0`，`ExcludeAssets=runtime` |
| ASP.NET Core | `<FrameworkReference Include="Microsoft.AspNetCore.App" />` |
| CopyLocalLockFileAssemblies | `false`（避免和服务器程序集冲突） |
| 插件 ID | 新生成一个固定 GUID，写进 `Plugin.Id` 和 `meta.json` |
| 版本 | 初始 `1.0.0.0`（是否用别的版本号由用户决定） |
| targetAbi | `12.2.0.0` |

### 5.2 网页注入

复用已在 12.2.0 验证过的 `IStartupFilter` 模式：

- 拦截 `/web`、`/web/`、`/web/index.html`
- 去掉 `Accept-Encoding` / `Range` / `If-None-Match`，拿完整 HTML
- 在 `</body>` 前插入：
  ```html
  <!--potplayer-bridge-client-->
  <script src="/PotPlayerBridge/Client.js" defer></script>
  ```
- 失败时原样返回，不能弄坏 Jellyfin Web
- 不写磁盘，Docker 只读镜像也能用

`Client.js` 用 `[AllowAnonymous]` 提供（登录前也要能加载），脚本内部检查 `window.ApiClient` 和登录状态。

### 5.3 客户端脚本职责

1. **接管播放按钮**
   - `document.addEventListener('click', handler, true)`
   - 找到最近的 `[data-action]` 或 `.btnPlay/.btnReplay`
   - 从 `.card[data-id]` 或当前详情页 URL 取 itemId
   - 判断意图（见第 4 节）
   - `preventDefault()` + `stopPropagation()`
2. **调用本机代理（不在线就先拉起）**
   - 先 `GET http://127.0.0.1:18927/health`（超时 800ms）
   - 在线 → `POST /api/play`
   - 不在线 → 跳转 `potplayerbridge://start`，然后每 500ms 重试 `/health`，最长 15 秒
   - 请求体：`{ serverUrl, accessToken, userId, itemId, intent }`
   - Token 从 `window.ApiClient.accessToken()` 取；取不到再从 `localStorage.jellyfin_credentials` 兜底
   - 带 `X-PPB-Client: 1` 自定义头（强制 CORS 预检）
3. **错误提示**
   - 代理不可达且拉起失败 → 显示可关闭的中文浮层：
     「无法启动本地 PotPlayer 桥接程序。可能尚未安装，或安装已损坏。」+「下载安装包」+「查看安装说明」
   - 代理返回错误 → 显示 `message`，并给「打开设置」链接（`potplayerbridge://settings`）
   - **任何情况都不回退到网页播放器**
   - 「点了没反应」是明确禁止的失败形态，必须有可见反馈
4. **季配置面板**
   - 监听 `hashchange` + `MutationObserver`
   - 取当前可见的 `.itemDetailPage`
   - 调 `GET /PotPlayerBridge/ItemInfo?itemId=...` 判断类型
   - 如果是 Season/Episode，在 `.detailPagePrimaryContainer` 后插入面板
   - 面板：片头秒数、片尾秒数、保存按钮、当前生效的季提示
   - 保存调 `POST /PotPlayerBridge/SeasonSettings`
   - 用 Jellyfin 现有样式类（`paperList`、`inputContainer`、`raised`、`emby-button`）保持观感一致

### 5.4 REST API

所有接口在 `[Authorize]` 下（`Client.js` 除外）。用户 ID 从 JWT claim `Jellyfin-UserId` 读取。

| 方法 | 路径 | 作用 |
| --- | --- | --- |
| GET | `/PotPlayerBridge/Client.js` | 注入脚本，匿名 |
| GET | `/PotPlayerBridge/ItemInfo?itemId=` | 返回 item 的 Type / SeriesId / SeasonId / IndexNumber 等，供面板判断 |
| GET | `/PotPlayerBridge/SeasonSettings?itemId=` | 取该集/该季的片头片尾配置 |
| POST | `/PotPlayerBridge/SeasonSettings` | 保存季配置 |
| POST | `/PotPlayerBridge/Resolve` | 解析播放计划 |
| POST | `/PotPlayerBridge/Progress` | 写进度（带防倒退守卫） |

#### `POST /Resolve` 请求

```json
{
  "itemId": "970d910847fe30222a747d922c433214",
  "intent": "item"
}
```

`intent` 取值：`item` / `resume` / `restart`。

#### `POST /Resolve` 响应（示意）

```json
{
  "playSessionId": "b3f0...",
  "userId": "f112...",
  "itemId": "970d...",
  "itemType": "Episode",
  "title": "誓言无声 第1季",
  "playlistName": "誓言无声 第1季",
  "startIndex": 4,
  "startPositionTicks": 14634390000,
  "introSeconds": 90,
  "outroSeconds": 120,
  "playlist": [
    {
      "itemId": "bc92...",
      "indexNumber": 1,
      "title": "S01E01 专家遭遇意外突袭",
      "streamUrl": "http://931002.xyz:5211/api/strm/play/.../S01E01.ts",
      "runtimeTicks": 27391280000,
      "subtitles": []
    },
    {
      "itemId": "970d...",
      "indexNumber": 4,
      "title": "S01E04 突现情报真假难辨",
      "streamUrl": "http://931002.xyz:5211/api/strm/play/.../S01E04.ts",
      "runtimeTicks": 27391280000,
      "subtitles": [
        {
          "index": 0,
          "codec": "subrip",
          "language": "zho",
          "title": "Chinese - SUBRIP - 外部",
          "isExternal": true,
          "url": "/Videos/970d.../d2be.../Subtitles/0/Stream.subrip"
        }
      ]
    }
  ]
}
```

要点：

- `playlist` 永远是**整季全集**，按 `IndexNumber` 升序；电影只有一项。
- `startIndex` 是 1 开始的列表序号，对应 DPL 的 `topindex`。
- `startPositionTicks` 已经过「续播 + 片头跳过」计算。
- `streamUrl` 优先用 `MediaSources[0].Path`（STRM 直链）。
  - 如果不是绝对 HTTP 地址，则返回相对路径 `/Videos/{id}/stream?static=true&MediaSourceId=...`，由代理补 `api_key`。
- `subtitles[].url` 是相对路径，代理带 Token 下载。

#### `POST /Progress` 请求

```json
{
  "playSessionId": "b3f0...",
  "itemId": "970d...",
  "positionTicks": 14640000000,
  "durationTicks": 27391280000,
  "isPaused": false,
  "stopped": false,
  "armed": true,
  "seeked": false,
  "ended": false
}
```

`ended` 是 M5 实测后补的字段：PotPlayer 播完那一刻 `0x5004`（位置）读成 0、`0x5002`（时长）也可能读成 0，
只靠「位置接近时长」判断不出「已看完」，会被当成「用户把进度拖回开头」而写入 0。
必须有这个显式标志，服务器才能正确标记已看完。

一个 `playSessionId` 会跨集（PotPlayer 自动连播），所以服务器**按集**记录 armed，不按会话。

#### `POST /Progress` 响应

```json
{ "accepted": true, "reason": "ok" }
```

被守卫拒绝时：`{ "accepted": false, "reason": "not-armed-and-position-regressed" }`。
注意仍然返回 HTTP 200：对代理来说这是「服务器收到但没采纳」，不该触发重试。

### 5.5 解析规则（服务器唯一权威）

#### Series（需求 2.1）

1. 取该剧所有 Episode。
2. 候选 = `UserData.PlaybackPositionTicks > 0 && UserData.Played == false`。
3. 按 `UserData.LastPlayedDate` 降序，取第一个。
4. 没有候选 → 取「季号最小、集号最小」的 Episode。
5. 播放列表 = 被选中那一集的整季全集。
6. 起播位置 = 该集 `PlaybackPositionTicks`（没有则 0）。

#### Season（需求 2.2）

1. 取该季所有 Episode。
2. 候选规则同上（按 `LastPlayedDate` 降序）。
3. 没有候选 → 本季 `IndexNumber` 最小的一集。
4. 播放列表 = 本季全集。

#### Episode / Movie（需求 2.3 / 2.4 / 2.7）

1. 播放项就是它自己。
2. Episode 的播放列表 = 它所在季全集；Movie 的播放列表 = 只有它自己。
3. 起播位置 = 用户进度（`Played=true` 视为 0）。

#### restart（需求 2.6 / 2.8）

| 目标类型 | 实际播放项 | 起播位置 |
| --- | --- | --- |
| Series | 第一季第一集对应的 Episode | 0 |
| Season | 本季第一集 | 0 |
| Episode | 它自己 | 0 |
| Movie | 它自己 | 0 |

> **注意**：`restart` 是「不续播」，不等于「不跳片头」。片头是否跳过由季配置决定，见决策 D-04。

#### 「最近播放的一集」为什么要用 `LastPlayedDate`

需求明确要求「以时间排序，不能靠集号推断」。Jellyfin 的 `UserData.LastPlayedDate` 会被本插件和其他客户端（VidHub、手机）共同更新，正好满足 A2 的跨端续播。

兜底：如果 `LastPlayedDate` 为空（老数据），按 `PlaybackPositionTicks > 0` 里 `LastPlayedDate` 最大者；再兜底按季/集号。

### 5.6 片头片尾配置

- 存储粒度：**Season**。
- 存储位置：插件数据目录下的 `season-settings.json`，原子写入 + 锁。
- 数据结构：
  ```json
  {
    "items": [
      { "seasonId": "d2be...", "introSeconds": 90, "outroSeconds": 120, "updatedAt": "2026-11-06T..." }
    ]
  }
  ```
- 未设置的季：不跳过（不用默认值）。
- `introSeconds` / `outroSeconds` 允许 0，表示关闭。
- 面板入口：
  - 季详情页
  - 集详情页（显示该集所属季的配置，编辑的也是季）
- 不按用户隔离（需求明确）。

### 5.7 进度守卫（需求 1 / A3 / A4 / A5 / H1 / H3）

这是整个项目最容易被「看起来能用」掩盖的失败点，单独设计。

#### 代理侧状态机

```
启动 -> 记录 expectedStartTicks
  |
  |-- 位置一直不在 expectedStart 附近，且没有检测到真实播放
  |      -> armed = false，不上报任何进度（假续播，A4）
  |
  |-- 位置落在 expectedStart ± 5s
  |   或 expectedStart≈0 且连续 3 秒位置在增长
  |      -> armed = true，开始正常上报
  |
  |-- armed = true 后用户拖回 0
  |      -> 视为 seek，继续上报（A5 要求记录拖回后的位置）
  |
  |-- 未 armed 就关闭
         -> 上报 stopped + armed=false；服务器拒绝写入
```

实测补充（M5 落地时的三条硬约束）：

- **续播跳转必须先做**：`Resolve` 给的 `startPositionTicks` 只有代理用 `0x5005` 跳过去才成立。
  跳转按「位置还停在开头」重试最多 6 次；跳不过去就放弃并**始终不 armed** —— 宁可这一次不记录，
  也不能把 20:00 的进度写成 3 分钟。
- **armed 不认「大跳 = 用户拖动」**：自动连播时标题可能还没刷新，位置先从 2700s 掉到 0，
  看起来和「用户拖回开头」一模一样；认错就是一次数据损坏。只有「同一集、两边都在播、时长没变、位置差 > 10s」才算拖动。
- **停与播完必须分开**：用户中途关播放器时 PotPlayer 的状态也从 2 掉到 0。
  只有「上一采样位置已在结尾 10 秒内」才判定播完；否则按 stopped 上报最后位置。
  两种情况下 `0x5004` 都可能读成 0，所以状态机保留「最后一次读到的正数位置」用于兜底。

#### 服务器侧守卫

按 `playSessionId` 在内存里维护：

```text
{ userId, armedItems[], lastAcceptedTicks, updatedAt }
```

写入规则：

1. `positionTicks < 0` → 拒绝。
2. `armed == false` 且 `positionTicks < existingPositionTicks - 5s` → 拒绝。
3. `armed == false` 且 `positionTicks == 0` 且 `existingPositionTicks > 0` → 拒绝。
4. `armed == true` → 接受（包括变小，A5）。
5. `ended == true`，或 `durationTicks > 0` 且 `positionTicks >= durationTicks - 5s` → 标记 `Played=true`，位置归 0（按 Jellyfin 习惯）。
6. 接受时：`PlaybackPositionTicks = positionTicks`，`LastPlayedDate = UtcNow`，`Played=false`（除非第 5 条）。
7. 调用 `IUserDataManager.SaveUserData(user, item, userData, UserDataSaveReason.PlaybackProgress/PlaybackFinished, ct)`，让其他客户端和「继续观看」立即感知。

#### 额外保护

- 同一 `playSessionId` 的写入串行化（锁）。
- 旧 session 在 30 分钟无更新后清理；清理后重新收到同一 sessionId 按「全新会话」处理（armed 特权一并作废）。
- **armed 按集记录**：一个会话会跨集（自动连播），但「上一集确认过在播」不代表「新集可以随便写」。
  换集后新集必须重新建立信任，否则新集若已有旧进度会被一条 0 冲掉。
- 连点两集（H1）：代理侧用一把播放锁，新的播放请求会先关闭上一个由本代理拉起的 PotPlayer 实例，再启动新的；旧 session 停止上报。服务器按 session 隔离，不会互相覆盖。

### 5.8 播放地址与字幕地址

- 播放地址：`IMediaSourceManager.GetPlaybackMediaSources(item, user, false, false, ct)` 取第一个 `MediaSourceInfo`。
  - 如果 `Path` 是 `http://` / `https://` 绝对地址 → 直接返回。
  - 否则返回相对 `Videos/{itemId}/stream?static=true&MediaSourceId={id}`。
- 字幕地址：`Videos/{itemId}/{mediaSourceId}/Subtitles/{index}/Stream.{codec}`。
  - 代理带 `Authorization` 下载。
  - 选择规则：`Language` 命中 `zh/chi/zho/chs/cht` 或 `Title` 含「中/简/繁/CHS/CHT」优先；多个中文时简体优先；没有中文则第一条外挂字幕；没有外挂就不传。
  - 内嵌字幕不动，PotPlayer 自己可切换。

---

## 6. Windows 本地代理设计

### 6.1 运行形态

- WinForms 托盘程序，无主窗口，双击托盘图标打开设置。
- TargetFramework：`net10.0-windows10.0.19041.0`。
- 发布：`win-x64` 自包含单文件（`PublishSingleFile=true`），不要求目标机器预装 .NET。
- 安装位置：`%LocalAppData%\PotPlayerBridge\`（当前用户，不需要管理员）。
- **不做开机自启（已确认）**：代理只在用户第一次点播放时被拉起，之后常驻；PotPlayer 被关掉它也继续运行。
- 启动方式：自定义协议 `potplayerbridge://`（写 `HKCU\Software\Classes\potplayerbridge`，安装时注册，无需管理员）。
  - `potplayerbridge://start` → 启动代理（若已在运行则只激活托盘提示）
  - `potplayerbridge://settings` → 启动并打开设置窗口
- 退出方式：托盘右键「退出」。没有常驻需求时可以随时关。
- 配置：`%LocalAppData%\PotPlayerBridge\config.json`。

#### 6.1.1 首次点击的完整流程（对应 D-02）

```text
用户点播放
  -> 脚本 fetch http://127.0.0.1:18927/health  (超时 800ms)
  -> 连不上
       -> 显示「正在启动本地桥接程序…」
       -> location.href = 'potplayerbridge://start'
       -> 浏览器弹「是否打开 PotPlayerBridge?」-> 用户点「打开」
       -> 脚本每 500ms 重试 /health，最多 15 秒
       -> 连上后 -> 发 /api/play
       -> 15 秒还连不上 -> 提示「未安装或安装损坏」+ 下载链接 + 设置说明
  -> 连得上 -> 直接 /api/play
```

- 协议未注册（代理没装）时，浏览器不会有任何反应。脚本在跳转后等 3 秒仍无响应，就切换为「未安装」提示，避免用户以为「点了没反应」。
- 浏览器对该协议的确认框，勾选「始终允许」后不再出现。

### 6.2 本机 HTTP 服务

- `TcpListener` 绑定 `127.0.0.1:18927`（不用 `HttpListener`，避免 URL ACL / 管理员权限）。
- 手写最小 HTTP/1.1：
  - 只接受 `GET` / `POST` / `OPTIONS`
  - 请求体上限 1 MB
  - `Connection: close`
- 安全：
  - 只监听回环地址
  - 校验 `Origin` 必须是配置里允许的 Jellyfin 源（默认从 `serverUrl` 推导）
  - 要求 `X-PPB-Client: 1`（浏览器跨域必须预检，恶意网页无法静默发简单请求）
  - 不把 Token 写日志、不落盘
- CORS：
  - `Access-Control-Allow-Origin: <allowed origin>`
  - `Access-Control-Allow-Methods: GET, POST, OPTIONS`
  - `Access-Control-Allow-Headers: Content-Type, X-PPB-Client`
  - `Access-Control-Allow-Private-Network: true`
  - `Access-Control-Max-Age: 600`

| 方法 | 路径 | 作用 |
| --- | --- | --- |
| GET | `/health` | 返回 `{ ok: true, version, potPlayerPath }` |
| POST | `/api/play` | 接收播放请求，同步返回成功/失败 |
| GET | `/api/settings` | 返回当前配置（不含 Token） |
| POST | `/api/settings` | 保存播放器路径 / 服务器地址 |
| GET | `/settings` | 简易中文设置页 |
| GET | `/api/detect` | 重新自动发现 PotPlayer |

### 6.3 PotPlayer 路径发现

按顺序尝试，命中即停：

1. 配置里记住的路径（必须 `File.Exists`，且文件名包含 `PotPlayer`）。
2. `HKCU\Software\DAUM\PotPlayerMini64` / `PotPlayer64` 的 `ProgramPath` / `ProgramFolder`。
3. `HKLM\SOFTWARE\DAUM\PotPlayerMini64` / `PotPlayer64`；`WOW6432Node` 同样查。
4. `HKLM/HKCU\Software\Classes\potplayer\shell\open\command`，解析命令行里的 exe 路径。
5. `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\PotPlayerMini64.exe` / `PotPlayerMini.exe`。
6. 常见目录：
   - `%ProgramFiles%\DAUM\PotPlayer`
   - `%ProgramFiles(x86)%\DAUM\PotPlayer`
   - `%ProgramFiles%\PotPlayer`
   - `%ProgramFiles(x86)%\PotPlayer`
   - `%LocalAppData%\Programs\PotPlayer`
7. 所有固定磁盘的根级/二级目录里搜 `PotPlayerMini64.exe` / `PotPlayerMini.exe`（限制深度和数量，跳过 `Windows`、`$Recycle.Bin`、`System Volume Information`）。
8. 开始菜单快捷方式（解析 `.lnk` 的 target）。

优先级：

- `PotPlayerMini64.exe` > `PotPlayerMini.exe` > `PotPlayer64.exe` > `PotPlayer.exe`
- 64 位系统优先 64 位版本

失败时：

- 返回 409，`{ code: "potplayer-not-found", message: "...", settingsUrl: "http://127.0.0.1:18927/settings" }`
- 设置页可手动填路径；保存前校验 `File.Exists`
- 已记住路径失效 → 自动重新发现，不直接报错（F4）

### 6.4 播放列表生成

> **实测修正（M4 验收阶段，2026-11-07）：目录改为只写 `%LocalAppData%\PotPlayerBridge\playlists\`，废弃「写进 PotPlayer 的 `Playlist` 目录」的原方案。**
>
> 原因：PotPlayer 会把 `Playlist` 目录下的每个 `.dpl` 当成常驻**专辑**标签。原方案会导致
> 同一部剧同时出现「专辑 誓言无声 第1季」和本次播放的「`*`誓言无声 第1季」两个同名条目；
> 而且这些专辑在 PotPlayer 重启后依然存在，等于把「这台电脑看过哪些片子」留在了播放列表里。
> 改到本程序自己的目录后：标签名仍取文件名（`誓言无声 第1季` 照常显示），播放与连播完全不受影响，
> 但 PotPlayer 不会把它登记成专辑，关掉播放器后不留痕迹。
> 标签名前那个 `*` 是 PotPlayer 对「当前播放列表」的固有标记，与文件内容无关（下列写法全部实测过：
> `topindex=0`、补 `duration2`、补 `playtime`、设文件只读），均无法去掉，**确认为可接受**。

**目录：只写 `%LocalAppData%\PotPlayerBridge\playlists\`，绝不写 PotPlayer 安装目录下的 `Playlist`。**

- 文件名：`{playlistName}.dpl`
  - 名称：剧集 `剧名 第X季`；电影 `片名`
  - 做 Windows 非法字符替换（`\ / : * ? " < > |`）
  - 同名已存在且内容不同时，先删除旧的（**前提是它在我们自己的清单里**），再写新的
- 编码：UTF-8 带 BOM + CRLF（实测 PotPlayer 中文正常）
- 内容：

```text
DAUMPLAYLIST
playname=<startIndex 对应的 streamUrl>
topindex=<startIndex>
saveplaypos=0
1*file*<url1>
1*title*<S01E01 标题>
1*sub*<第 1 集中文字幕的本地绝对路径>
2*file*<url2>
2*title*<S01E02 标题>
2*sub*<第 2 集中文字幕的本地绝对路径>
...
```

- 列表里**始终是所有集**，不裁剪、不重排。
- `topindex` 是 1 开始。
- **每集写自己的 `N*sub*`**（该集没有外挂字幕就**不写这一行**，不要写空值）。
  这样整季连播时字幕逐集自动跟随，代理不需要在切集时额外干预。
- 只保留最近 **5** 个由本代理创建的 `.dpl`，更早的自动清理。
- **不使用 `/title=` 参数**（实测会让 DPL 完全不播放）。

#### 6.4.1 用户专辑安全（强制约束）

本程序**不再向 PotPlayer 的 `Playlist` 目录写入任何文件**，因此不存在污染或误删用户专辑的路径。仍保留以下约束：

1. **绝不删除、绝不覆盖用户自己的 `.dpl`**：删除动作只针对 `created-playlists.json` 里登记过的文件名。
2. **升级清理**：早期版本可能在 `Playlist` 目录留下过文件，播放前按清单逐个清除（只删登记过、且签名匹配的），避免残留成专辑。
3. **写前快照**：每次写自己的列表目录之前，把该目录复制到 `%LocalAppData%\PotPlayerBridge\playlist-backup\<时间戳>\`，最多保留 10 份。
4. **写后校验**：写完后重新列目录，如果发现本代理的文件数量变少，立即从最近一次快照恢复，并记录错误日志。
5. **禁止强杀**：见 6.5。

### 6.5 启动与实例管理

- 用 `ProcessStartInfo.ArgumentList` 传参，避免中文/空格路径被拆开。
- 启动命令：`PotPlayerMini64.exe "<dpl路径>" /new`
- **禁止使用 `/title=`**：实测会让 DPL 不播放。
- 同一时间只保留一个由本代理拉起的 PotPlayer：
  - 新播放请求先**优雅关闭**旧实例，顺序：
    1. `WM_COMMAND 20002`（停止）— 实测 `0x5009` 只是"停止播放"，不会退出进程
    2. `PostMessage WM_CLOSE`
    3. 等待 3 秒
    4. 仍不退出才 `Kill`，并**在配置里打上 `abnormalExit=true` 标记**
  - 下次启动前如果发现该标记，先做一次 Playlist 目录快照比对 + 备份，再继续。
- 不碰用户自己打开的 PotPlayer 实例（只管理自己记录的 PID）。
- 启动后等待主窗口出现（最多 10 秒），拿 `MainWindowHandle`。
- 如果主窗口句柄为 0，用 `EnumWindows` + PID + 类名 `PotPlayer64` 查找。

### 6.6 进度读取与当前集判断

**状态码映射（实测修正）**：

| `0x5006` 返回 | 含义 |
| --- | --- |
| `-1` | 已停止 / 未加载媒体 |
| `0` | 媒体已播完 / 空闲（**实测播完最后一集后返回 0**） |
| `1` | 暂停 |
| `2` | 正在播放 |

- 每 1 秒读一次：`0x5002` 总时长、`0x5004` 当前位置、`0x5006` 状态。
- 当前集判断：
  - 主路径：读 PotPlayer 窗口标题（`GetWindowText`），标题形如 `S01E02 突现情报真假难辨 - PotPlayer`，和 DPL 的 `N*title*` 精确比对。
  - DPL 的 title 里统一带 `SxxExx`，用正则 `S(\d+)E(\d+)` 匹配播放计划。
  - 匹配不到时，按播放计划顺序 + 位置回落推算。
- 集切换检测：
  - 窗口标题变化 → 换集
  - 状态由 `2` 变 `0` → 当前集播完
  - 位置突然变小且总时长变化 → 换集
  - 上报上一集 `stopped`（如接近结尾则 `played=true`），开始新一集
- 暂停/恢复：状态从 `2` 变 `1` 时上报一次 `isPaused=true`；恢复后继续。

### 6.7 片头/片尾执行

- 起播位置已经由服务器算好（`startPositionTicks` 已包含片头跳过）。
- 代理仍要做**兜底**：
  - 启动后如果 `introSeconds > 0` 且当前位置 < 片头结束，等媒体就绪后发 `0x5005` 跳到 `introSeconds`。
  - STRM 首次加载慢，可能前几秒位置还是 0；**重试 3~5 次，直到位置 > 0 或超时**，避免「跳了个寂寞」。
- 片尾：
  - 当 `position >= duration - outroSeconds` 且**还有下一集** → 发 `WM_COMMAND 10124`。
  - **最后一集 → 不跳、不循环**（实测 PotPlayer 播完最后一项会自然停止）。到结尾标记已看，之后什么也不播。
- 未配置的季（intro/outro 都是 0）→ 什么都不做。
- **电影不跳片头片尾**（需求明确排除；且很多电影片头有信息量）。

#### 6.7.1 媒体就绪判定（STRM 首播慢，必须处理）

实测：一个 10 GB 的 STRM 直链从 `Process.Start` 到出现画面可能超过 5 秒。所有「立即执行」的动作都必须等就绪：

```text
启动后每 300ms 轮询，最多 30 秒：
  status = 0x5006 ; pos = 0x5004 ; dur = 0x5002
  就绪 = (status == 2 && pos > 0 && dur > 0)
  → 就绪后才允许：跳片头、开始上报进度、片尾检测
超时 30 秒仍不就绪：
  → 写日志 + 托盘提示「媒体加载超时」
  → 仍然不写任何进度（避免把 0 覆盖到服务器）
```

- 跳片头时如果 `0x5005` 没生效，继续在下一轮重试，最多 10 次。
- `introSeconds = 0` 时整段跳过。

### 6.8 字幕（2026-11-08 重测并推翻旧结论）

**结论：PotPlayer 的 DPL 播放列表没有字幕字段，整季连播无法带外挂字幕。
外挂字幕只能在「直接播放视频地址」时通过命令行 `/sub=` 生效。**

实测矩阵（2026-11-08，本机 PotPlayer + Jellyfin STRM 直链，逐条验证）：

| 方式 | 结果 |
| --- | --- |
| DPL 每项写 `N*sub*路径` | ❌ **无效**。PotPlayer 完全忽略该行；它自己「保存播放列表」时也从不写字幕字段 |
| DPL 里把地址写成 `url\|Subtitle=路径` | ❌ 播放直接失败（提示「服务器已关闭或地址错误」） |
| `<dpl> /sub="路径"` | ❌ **无效**。只要用播放列表启动，`/sub=` 就被丢弃 |
| `<视频地址> /sub="路径"` | ✅ **有效**（唯一可行形式） |
| `<视频地址> /sub="路径" /new` | ❌ 加了 `/new` 后字幕参数被丢弃 |
| `<视频地址> "-sub=路径"`（单横线） | ❌ 无效 |
| `"/sub=路径"`（整个参数被引号包住，.NET `ArgumentList` 的默认拼法） | ❌ 无效，且不报错 |
| 拖放字幕文件（`WM_DROPFILES`） | ❌ 无效 |
| 对已运行实例再发 `/sub` | ❌ 无效（单实例模式下参数被忽略） |
| 视频同目录同名 sidecar（HTTP 流） | ❌ PotPlayer 不会去取 |
| 程序化打开「打开字幕」对话框（`WM_COMMAND 10146`） | ⚠️ 对话框能弹出，但它是 Windows DirectUI 结构：UI Automation 找不到文件名输入框、`SetWindowText` 写入后界面不显示。只能靠模拟键盘，脆弱且抢焦点 |

**最终方案（按媒体类型分流）**：

```text
电影 / 单片视频（ItemType = Movie / Video）
  PotPlayerMini64.exe "<视频直链>" /sub="<本地字幕绝对路径>"
  - 单集直启，字幕 100% 加载
  - 不加 /new、不给字幕参数加外层引号
  - 代价：PotPlayer 播放列表里只有这一项

剧集（ItemType = Episode）
  <dpl> /new
  - 保持整季播放列表，连播、手动切集、标题全部不变
  - 外挂字幕不带（剧集一般有内嵌字幕）；DPL 里不写任何字幕行
```

补充约束：

- **字幕文件名扩展名必须是 PotPlayer 认识的标准扩展名**（`.srt` / `.ass` / `.ssa` / `.sub` / `.vtt` / `.sup` …）。
  Jellyfin 的 `codec` 值是 `subrip` / `mov_text` 这种编码名，**不能直接拿来当扩展名**：
  文件叫 `xxx.subrip` 时 PotPlayer 静默不加载（`/sub=` 命令没错、文件也下载了，就是没字幕）。
  代理侧由 `SubtitleDownloader.ToExtension` 做映射。
- 代理先把字幕下载到 `%LocalAppData%\PotPlayerBridge\subtitles\<playSessionId>\<SxxExx|itemId8>.<lang>.<ext>`，
  统一补 UTF-8 BOM（无 BOM 的 UTF-8 中文在部分机器上会乱码），播放结束 7 天后清理（N-04）。
- **单集直启模式下 PotPlayer 的窗口标题是文件名**（`PFES-107.mp4`），不是 Jellyfin 标题，
  所以进度状态机必须工作在 `singleItemMode`，直接固定为起播项，不能按标题匹配（否则进度完全不上报）。

字幕选择优先级（D-07）：简体 > 繁体 > 其他中文 > 第一条外挂字幕。

- 判定依据：`MediaStreams[].Language` 命中 `zh/chi/zho/chs/cht`，或 `Title`/`DisplayTitle` 含
  「中/简/繁/CHS/CHT/Chinese」。
- 内嵌字幕不动，PotPlayer 原生可切换。
- 不要用 `WM_COMMAND 10126` 去“选中”字幕，它只是显示开关。

> ⚠️ 已知坑：`/sub=` 生效与否跟视频是否已加载有关，**不能靠它做可靠交付**。
> 只把它当作可选补充，主通道是 DPL 的 `N*sub*`。

下载用 `Authorization: MediaBrowser Token="..."`；自签名证书场景只对配置的服务器地址放宽校验，并在 README 说明。

### 6.9 进度上报

- 正常播放：每 5 秒一次。
- 暂停：立即一次。
- 拖动（位置跳变 > 10 秒）：立即一次，带 `seeked=true`。
- 停止/关闭：一次 `stopped=true`。
- 集切换：上一集 `stopped` + 新一集开始。
- 网络失败：指数退避重试 3 次；不阻塞播放。
- 代理退出：`ApplicationExit` 里尽量上报最后位置。
- Token 只在内存里保存当前会话，不写入配置文件。

### 6.10 错误与降级

| 情况 | 行为 |
| --- | --- |
| 代理没运行 | 自动跳 `potplayerbridge://start` 拉起；显示「正在启动本地桥接程序…」 |
| 代理未安装（协议不存在） | 3 秒后显示「未安装」浮层：下载链接 + 安装说明；**不静默** |
| 浏览器协议确认框被点「取消」 | 显示「已取消启动本地桥接程序」，并给「再试一次」按钮 |
| PotPlayer 找不到 | 409 + 自动打开设置页 + 托盘气泡 |
| `/Resolve` 失败 | 代理返回错误，网页浮层显示服务器返回的中文信息 |
| PotPlayer 启动失败 | 同上；不打开网页播放器 |
| 本程序列表目录不可写 | 直接返回错误（`%LocalAppData%` 正常都可写），不静默降级 |
| 本程序列表文件数异常减少 | 自动从最近快照恢复 + 记录错误日志 |
| 进度上报失败 | 静默重试；托盘日志记录；不弹窗打扰 |
| 播放器被用户手动关闭 | 上报最后位置 + `stopped` |

---

## 7. 端到端时序（一次「继续观看」点击）

```text
用户点「继续观看」卡片上的播放
  -> clientScript.js 捕获 click
  -> 识别：卡片 type=Episode, positionticks>0, 区块无 nextup 链接 => intent=resume
  -> POST 127.0.0.1:18927/api/play { itemId, intent:"resume", token, serverUrl }
  -> 代理校验 Origin / X-PPB-Client
  -> 代理 POST https://931002.xyz:8443/PotPlayerBridge/Resolve
       Authorization: MediaBrowser Token="..."
  -> 插件解析：该集 + 该集所在季全集 + 起播位置（含片头跳过）
  -> 代理生成 DPL（整季，topindex=当前集）
  -> 代理下载首选中文字幕
  -> 代理关闭上一个自己拉起的 PotPlayer
  -> 启动 PotPlayerMini64.exe "<dpl>" /new
  -> 等待窗口，读 0x5002/0x5004/0x5006
  -> armed 后每 5 秒 POST /Progress
  -> 片尾到达 -> WM_COMMAND 10124 -> 下一集
  -> 用户关闭 -> POST /Progress stopped=true
```

---

## 8. 安全设计

- 代理只监听 `127.0.0.1`，不监听局域网。
- 代理校验 `Origin`，只允许配置的 Jellyfin 源。
- 代理要求自定义头 `X-PPB-Client`，强制浏览器预检，避免任意网页静默调用。
- Token 只在内存中使用，不写日志、不写配置。
- 自签名证书：代理的 `HttpClient` 只对**配置的 Jellyfin 主机**放宽证书校验，不做全局关闭；README 明确说明风险。
- 插件不引入外部依赖，不执行用户输入的路径/命令。
- 季配置接口对所有已登录用户开放（已确认，符合「看剧时顺手改」的体验）。

---

## 9. 与需求验收项的对应关系

| 验收组 | 由谁保证 | 验证方式 |
| --- | --- | --- |
| A 进度同步 | 代理状态机 + 服务器守卫 | 手动播放/关闭/拖动，查 Jellyfin 用户数据与 VidHub |
| B 按钮映射 | 注入脚本 + 插件 resolve | 逐条点 B1-B12 |
| C 播放列表 | DPL 生成器 + topindex | 播 E3，检查列表全集与实际起播集 |
| D 片头片尾 | 插件配置 + 代理跳转 | 设置 90/120，观察起播与切集 |
| E 字幕 | 插件字幕选择 + 代理下载/加载 | 用 AV 库 `.chi.srt` 与临时测试字幕 |
| F 播放器路径 | 路径发现器 + 设置页 | 全新配置、非默认目录、删除后重查 |
| G 交付结构 | 两个独立目录 + README | 按 README 从零安装 |
| H 健壮性 | 播放锁 + 守卫 + 错误提示 | 连点、失败、关闭 |

---

## 10. 实施顺序（里程碑）

### M0：技术验证收尾（已完成）

已验证通过：

- [x] DPL `topindex` / 自动连播 / 播完停止
- [x] PotPlayer 窗口消息读进度（`0x5002` / `0x5004` / `0x5006`）
- [x] 列表名称方案（写入 `Playlist` 目录，文件名即标签名）
- [x] `0x5005` 跳转、`WM_COMMAND 10124` 下一集、`WM_CLOSE` 优雅退出
- [x] **https 页面 → `http://127.0.0.1:18927` 跨域调用**（预检、自定义头、中文 JSON 全部通过）
- [x] **外挂字幕逐集生效**：DPL 每项写 `N*sub*<绝对路径>`，第 1 集播完自动切第 2 集，
      第 2 集自动加载自己的字幕（真实 STRM 也验证通过）

待真机确认：

- [ ] `potplayerbridge://` 从**真实 Chrome/Edge** 拉起本地程序
      （VS Code 内置浏览器是 Electron，会拦截自定义协议；系统级注册与触发已验证可用）

待随代码实现：

- [ ] Playlist 目录备份 / 恢复工具

#### M0 的额外发现（影响设计）

1. **STRM 直链首次加载很慢**：实测一个 10 GB 远程文件从启动到出画面明显超过 5 秒。
   - 片头跳转必须等「媒体真正就绪」再发 `0x5005`，不能用固定延时。
   - 进度上报的 `armed` 判定必须等 `status=2` 且 `pos>0`，否则会把 0 写回服务器。
   - 首屏等待上限放宽到 30 秒，期间显示「正在加载」。
2. **`0x5006` 的 4 种取值**：`-1` 停止、`0` 播完/空闲、`1` 暂停、`2` 播放。播完与未加载都要当作「不该写进度」。
3. **`0x5009` 只是停止播放，不退出进程**；退出必须 `WM_CLOSE`。
4. **PotPlayer 退出时会重写 `Playlist\PotPlayerMini64.dpl`**（记录最后播放项），属正常行为，不要去改它。
5. **`/sub=` 命令行参数不可靠**（有时静默忽略），字幕必须走 DPL 的 `N*sub*` 字段。
6. **`/title=` 会让 DPL 完全不播放**，必须禁用；列表名只能靠文件名。

### M1：服务器插件骨架（1 天）

- 新建 `server-jellyfin-plugin/`
- Plugin / 配置 / 注入过滤器 / Client.js 最小版本
- 本地 build，复制到 VPS 插件目录，重启 Jellyfin
- 验证：`/web/index.html` 里出现脚本；浏览器控制台能打印版本；`/PotPlayerBridge/Client.js` 可访问

### M2：播放语义与配置（2 天）

- `PlaybackResolver`：Series/Season/Episode/Movie + resume/restart
- `SeasonSettingsStore` + API
- 季/集详情页面板
- 单元测试：最近播放排序、S1E1 兜底、整季列表、片头计算
- 验证：用真实库手动调 `/Resolve`，检查 B1-B10 的返回

### M3：Windows 代理骨架（1.5 天）

- WinForms 托盘 + 配置存储 + 单文件发布
- 本机 HTTP 服务 + CORS + 健康检查
- PotPlayer 路径发现 + 设置页
- 验证：`/health`、`/api/detect`、手动指定路径、删除路径后重查

### M4：DPL 与拉起（1 天）

- DplBuilder + 启动 + 实例管理
- 验证：播 E3，列表全集、实际播 E3、自动下一集
- 验证：连点两集只保留一个实例

### M5：进度同步（2 天）

- 窗口消息轮询 + 当前集匹配
- ProgressReporter + 状态机
- 服务器 ProgressGuard
- 验证：A1-A5、H1、H3
- **重点验证「接口返回 20:00 但播放器实际从 0 开始」这种假续播**

### M6：片头片尾 + 字幕（1.5 天）—— ✅ 已实现

- 片头跳转重试、片尾下一集
- 字幕下载 + 按媒体类型分流（详见 6.8）：
  - 电影/单片：单集直启 + `/sub="本地字幕"`
  - 剧集：整季 DPL，不带字幕行
- 验证：D1-D7、E1-E3

### M5.5：卡片进度角标（用户新增，半天）

> 用户需求（2026-11-07）：Jellyfin 网页的卡片只有进度条、没有数字时间，
> 而 VidHub 在卡片上直接显示 `16:08 / 30:22`。希望在网页上也显示。

- **位置：卡片图片内部的左上角**（右下角是进度条/操作按钮、右上角是已看对勾，都不能占）。
  不做「在文字区多加一行」的方案 —— 用户明确否掉了。
- **样式**：半透明深色圆角小标签，白字，字号略小于卡片标题，不遮挡海报主体。
- **显示规则（用户确认）**：
  - 有进度未看完 → `16:08 / 30:22`
  - 没看过 → `0:00 / 30:22`
  - 已看完 → 显示「已看完」
  - 拿不到总时长（元数据缺失）→ 不显示角标
- **覆盖范围：只限「指向具体某一集/某部电影」的卡片**（`data-type` 为 `Episode` / `Movie`）。
  剧（Series）、季（Season）、合集（BoxSet）一律不显示 —— 它们每集时长都不一样，
  在卡片上写一个数字只会误导（用户要的「这剧多长」这个数字本身不存在）。
  - 首页/媒体库：「继续观看」的集卡片、电影卡片显示；剧卡片、季卡片不显示。
  - 「接下来」：显示（永远是具体某一集）。
  - 季页剧集行（`.listItem`）：显示。
  - 详情页海报：「集/电影」详情页显示，「剧/季」详情页不显示。
- **为什么有的卡片没数字**：Jellyfin 的 `RunTimeTicks` 来自媒体元数据。STRM 文件若没有
  `nfo` 里的 `<runtime>`，Jellyfin 探测不到时长，`RunTimeTicks` 就是 0 —— 这时没有第二个
  数字可显示。这不是「播放过才有」，而是「有没有元数据」。
  实测：誓言无声（STRM）多数集为 0，鹊刀门传奇有完整时长。
- **数据来源**：新增批量接口 `GET /PotPlayerBridge/ItemProgress?itemIds=a,b,c`，
  一次返回 `{ itemId, itemType, positionTicks, runtimeTicks, played }`。
  为什么不用卡片上的 `data-positionticks`：它只在「继续观看」卡片上有，且没有总时长与已看状态。
- **更新时机**：页面渲染/路由切换时补算；收到 WebSocket `UserDataChanged` 时增量更新。

### M7：交付文档与端到端（1 天）

- 根 README、两个目录 README、PROTOCOL、TESTING
- install.ps1 / uninstall.ps1
- 按 A-H 全量走一遍
- 清理临时文件

> 总计约 9~10 个工作日（不含等待用户确认和 VPS 重启窗口）。

---

## 11. 测试策略

### 11.1 谁来做验证

| 验证类型 | 谁做 | 怎么做 |
| --- | --- | --- |
| 单元测试、窗口消息返回值、HTTP 状态码、文件存在性、日志 | **AI 自己做** | 命令行 / 测试框架，不需要截图也不需要问人 |
| **UI 观感、字幕是否显示、列表名是否正确、播放是否顺畅** | **必须问用户** | `ask_user` 出**带选项卡**的问题 |
| 跨端续播（手机 VidHub → 电脑） | 用户 | 需要用户拿手机配合 |

> ⚠️ **用户明确要求：不要靠截图验证 UI 效果。**
> AI 截图经常截不准（窗口没置前、媒体没加载完、被遮挡），浪费大量时间。
> 正确做法：出带选项卡的问题，让用户看一眼后回答，例如
>
> ```
> PotPlayer 的播放列表标签页显示的是什么？
> [A] 誓言无声 第1季    [B] 默认专辑    [C] 其他（请描述）
> ```

### 11.2 各层的测试方式

- 服务器插件：
  - 单元测试：`PlaybackResolver` 的纯逻辑（mock item + user data）
  - 集成测试：在 VPS 上用真实 Jellyfin API 调 `/Resolve`
- Windows 代理：
  - 单元测试：路径发现、DPL 生成、防倒退状态机
  - 手动集成：真实 PotPlayer + 真实 STRM
- 端到端清单：`docs/TESTING.md` 按 A1–H3 逐条打勾
- 每次改动只跑最小范围；交付前跑全量

### 11.3 验证「续播真的生效」的正确方法

不要只看 `/Resolve` 返回的 `startPositionTicks`。必须：

1. 启动 PotPlayer
2. 等媒体就绪（`status=2 && pos>0`）
3. 读 `0x5004` 的实际位置
4. 比对：`|实际 - 期望| < 10 秒` 才算通过

这条对应需求文档第 10 条提醒，是最容易漏的失败模式。

---

## 12. 风险与回退

| 风险 | 影响 | 回退 |
| --- | --- | --- |
| **写 PotPlayer `Playlist` 目录可能影响用户已有专辑**（本次未确证，但风险真实） | 用户数据丢失，不可恢复 | 6.4.1 强制：写前整目录快照、只碰登记过的文件、写后校验、禁止强杀；设置页提供手动备份/恢复 |
| PotPlayer 窗口消息是非公开接口 | 升级后可能失效 | 保留 SMTC（`GlobalSystemMediaTransportControlsSessionManager`）作为回退；README 标注已验证版本 |
| `/title` 与 DPL 冲突 | 列表不播放 | **禁用 `/title`**，改用 Playlist 目录文件名方案 |
| `/sub` 参数不可靠（实测有时静默忽略） | 字幕不自动加载 | 已改用 DPL 的 `N*sub*` 字段（实测稳定）；`/sub` 只作可选补充 |
| 用户手动在 PotPlayer 里换片/改列表 | 当前集判断错位，可能把进度写到错误的集 | 窗口标题匹配 + 顺序回落；标题无法匹配时**停止上报**而不是猜 |
| Jellyfin 12.2 DOM 变动 | 按钮接管失效 | 选择器集中在一个文件，加多重兜底；README 标注仅验证 12.2.0 |
| 浏览器 Private Network Access 限制 | 网页调不到本机代理 | 自定义协议 `potplayerbridge://` 兜底；README 给浏览器设置说明 |
| 浏览器对自定义协议弹确认框，用户点「取消」 | 首次体验中断 | 显示「已取消启动」提示 + 提供手动启动方式 + 「不再询问」说明 |
| 自签名证书 | 代理调 Jellyfin API 失败 | 仅对配置主机放宽校验；提供严格模式开关 |
| STRM 首播慢 | 片头跳转/续播失效 | 重试 + armed 状态机；不在媒体就绪前写进度 |
| 用户手动改 PotPlayer 列表 | 当前集判断错位 | 窗口标题匹配 + 顺序回落；README 说明 |
| 连点/多实例 | 进度互相覆盖 | 播放锁 + 关闭旧实例 + session 隔离 |
| 用户数据被清零 | 观看记录丢失 | 服务器守卫拒绝未 armed 的倒退写入 |

---

## 13. 已确认的决策

> 以下均已由你确认，作为编码依据。

### D-01 列表名称 —— 已解决，用文件名

**结论：把 `.dpl` 写进 PotPlayer 的 `Playlist` 目录，文件名就是列表标签页名称。不影响播放。**

你提供的截图（`默认专辑 / *誓言无声 第1季 / 体育`）是该结论的直接证据，我这边的独立实验也复现了。

- ✅ 实现了需求 3 的「列表名 = 剧名 第X季」「电影 = 片名，无第X季」
- ✅ 未使用 `/title=`（该参数会破坏播放，禁用）
- ⚠️ 标签名前会有 `*`，属 PotPlayer 自身标记，可接受
- ⚠️ 写该目录有数据风险，已按 6.4.1 强制约束处理（备份 + 只碰自己的文件 + 禁止强杀）
### D-02 代理运行方式 —— 网页按需拉起，常驻不自动退出

- ❌ 不做开机自启
- ✅ 网页先探 `127.0.0.1:18927/health`，探不到就跳 `potplayerbridge://start` 拉起
- ✅ 拉起后常驻；PotPlayer 关闭它也不退出
- ✅ 首次会弹一次浏览器确认框，可勾「始终允许」
- 详见 6.1.1

### D-03 全部看完 —— 停止，不再播放

- ✅ 只把 `PlaybackPositionTicks > 0 && !Played` 视为「有进度」
- ✅ 整季播完（最后一项）→ 不跳、不循环、不再启动任何播放
- ✅ 实测 PotPlayer 播完最后一项自然停止（状态码 `0`），无需额外干预
- ✅ 整剧已看完 → 点剧集卡片从 S1E1 开始

### D-04 重播 —— 从 0 开始，但仍跳片头

- ✅ `restart` 意图：位置归 0，之后仍应用该季片头跳过

### D-05 季配置权限 —— 所有已登录用户可改

- ✅ `/SeasonSettings` 只要求 `[Authorize]`，不要求管理员
- ✅ 配置按季共享，不按用户隔离

### D-06 随机播放 —— 不接管

- ✅ 不拦截 `btnShuffle`，保持 Jellyfin 原行为
- ✅ 文档与验收清单里标注「随机播放不在范围内」

### D-07 字幕优先级 —— 中文优先

- ✅ 简体 > 繁体 > 其他中文 > 第一条外挂字幕
- ✅ 没有外挂字幕时不传 `/sub`，不影响播放

### D-08 部署联调 —— 已授权

- ✅ 可把插件装到 VPS `/www/jellyfin/config/plugins/` 并重启 `jellyfin` 容器
- ✅ 可在本机安装/运行代理，可启动/关闭 PotPlayer 做测试
- ✅ 可创建临时测试字幕
- ⚠️ **附加约束**：任何写 `Playlist` 目录的操作必须先做整目录快照（6.4.1）

### D-09 命名与版本 —— 已确认

- 插件显示名：`PotPlayer Bridge`
- 版本：`1.0.0.0`
- 目录：`server-jellyfin-plugin/`、`windows-potplayer-client/`

### D-10 电影 —— 不跳片头片尾

- ✅ 电影只做「续播 + 列表名 + 字幕」，不应用 intro/outro
- ✅ 季配置面板在电影页不显示

---

## 14. 已确认的小事（N 系列）

### N-01 最后一集播完后 —— 保持窗口打开

- ✅ 停在结束画面，不自动关闭。
- ✅ 不跳、不循环、不启动新的播放。

### N-02 我们创建的 dpl 保留策略 —— 最近 5 个

- ✅ 只保留最近 **5** 个由本代理创建的 `.dpl`。
- ✅ 更早的自动删除（只删登记在案、确实由我们创建的文件）。

### N-03 标签名 —— 不加前缀

- ✅ 就是 `誓言无声 第1季`，符合需求 3 原文。

### N-04 临时字幕 —— 保留 7 天

- ✅ 保留 7 天，自动清理更早的。

### N-05 ~~换机时的 Playlist 目录写权限 —— 安装时授权一次~~（已作废）

- ❌ **2026-11-07 作废**：本程序不再写 PotPlayer 的 `Playlist` 目录（见 6.4 的实测修正），
  也就不需要管理员授权，换机不会再有权限问题。原方案的两条（`icacls` 授权、失败回退）一并取消。

### N-06 `体育.dpl` 恢复 —— 不需要

- ❌ 用户已说明该文件是本人删除，与本程序无关，无需恢复尝试。
- ✅ 计划中仍保留 6.4.1 的防御性约束（成本低、风险真实）。

---

## 15. 本计划明确不做的事

- 不支持手机/平板/电视上的 PotPlayer（需求明确）。
- 不实现网页播放器兜底。
- 不改 Jellyfin 转码/播放器内核。
- **不为 PotPlayer 以外的播放器做适配**（即需求原文的「不支持 MPV/VLC」：本项目只对接 PotPlayer，不会做"改用 VLC 播放"的功能；不影响你机器上装的 VLC/MPV 本身）。
- 不做弹幕、倍速记忆、画质切换。
- 不做按用户的片头片尾隔离。
- 不做电影片头片尾。
- 不接管「随机播放」按钮。
- 不使用 `/title=` 命令行参数。

### 后续可做（本轮不做）

- Jellyfin 插件仓库自动更新（等调试稳定后再加）。
- 电影片头片尾。
- 多用户各自的片头片尾配置。

---

## 16. 下一步

所有决策已确认，计划定稿，M0 已完成。按以下顺序编码：

1. **M1 服务器插件骨架** → 用户会先看到网页里注入生效
2. **M3 Windows 代理骨架** → 用户会先看到 `/health` 与设置页可用
3. **M4 DPL 与拉起** → 第一次能真的用 PotPlayer 播起来（第一个可体验里程碑）
4. M2 / M5 / M6 / M7 依次推进

每完成一个可体验里程碑，**停下来让用户手动验证**，不要闷头往下做。

---

## 17. 交接说明（给下一个 AI / 下一个人）

### 17.1 你的起点

- 仓库：`D:\Projects\Jellyfin-PotPlayer-Bridge2\`，**目前只有 `REQUIREMENTS.md` 和 `PLAN.md` 两个文件，没有一行代码**。
- 需求：`REQUIREMENTS.md`。它是验收标准的**唯一权威**，以它为准。
- 计划：本文档。所有决策、实测数据、坑都在这里。
- 下一步动作：直接进入 M1（第 10 节），不需要再问用户"要不要开始"。

### 17.2 第一件必须知道的事

**这个项目的难点不是「怎么拉起播放器」，而是这三件事：**

1. **点的是哪一集** —— 需求 2 有六种按钮语义，全部由服务器插件解析（第 4 节）。注入脚本只回答"用户点了哪个 DOM、什么意图"，不要在前端写剧集/季/集的层级逻辑。
2. **续播是否真的生效** —— 接口返回 20:00 但播放器从 0 开始，是最容易漏掉的失败模式。验证时必须看**播放器实际位置**（`0x5004`），不能只看接口返回。
3. **进度会不会被清零/倒退** —— 用户的观看记录比功能更宝贵。宁可少报一次，也不要用错误的值覆盖（第 5.7 节）。

### 17.3 已验证可用的技术事实（不要重复验证）

| 能力 | 实现方式 | 状态 |
| --- | --- | --- |
| 读取播放位置 | `SendMessage(hwnd, 0x0400, 0x5004, 0)` → 毫秒 | ✅ 实测 |
| 读取总时长 | `SendMessage(hwnd, 0x0400, 0x5002, 0)` → 毫秒 | ✅ 实测 |
| 读取播放状态 | `SendMessage(hwnd, 0x0400, 0x5006, 0)` → `-1/0/1/2` | ✅ 实测 |
| 跳转到指定位置 | `SendMessage(hwnd, 0x0400, 0x5005, 毫秒)` | ✅ 实测 |
| 下一集 | `SendMessage(hwnd, 0x0111, 10124, 0)` | ✅ 实测 |
| 优雅退出 | `PostMessage(hwnd, WM_CLOSE=0x0010, 0, 0)` | ✅ 实测，进程正常退出 |
| 指定起播集 | DPL `topindex=N`（1 开始） | ✅ 实测 |
| 列表名 | DPL 放进 PotPlayer 的 `Playlist` 目录，文件名即标签名 | ✅ 实测 |
| 逐集字幕 | DPL 每项加 `N*sub*<绝对路径>` | ✅ 实测 |
| 网页调本机端口 | `https://...` → `http://127.0.0.1:18927` | ✅ 实测（CORS 预检 + 中文 JSON 通过） |
| 自定义协议 | `HKCU\Software\Classes\potplayerbridge` | ✅ 系统级可用；**真实浏览器待确认** |

窗口类名：`PotPlayer64`（Mini 版也是这个类名）。

### 17.4 已知的坑（踩过的，别再踩）

1. **`/title=` 参数禁用**。它能让主窗口标题变成列表名，但和 DPL 一起用会让列表**完全不播放**（停在"即将播放…"）。列表名只能靠文件名。
2. **`/sub=` 不可靠**。实测有时生效有时静默忽略，与媒体是否已加载有关。字幕走 DPL 的 `N*sub*`。
3. **`0x5009` 不是退出**，只是停止播放。退出用 `WM_CLOSE`。
4. **不要强杀 PotPlayer 进程**。用 `ShowWindow(SW_MINIMIZE)` + `WM_CLOSE`，等 3 秒，实在不行才 Kill。
5. **STRM 首播很慢**。10 GB 远程直链从启动到出画面可能 > 5 秒。所有"立即执行"的动作必须等媒体就绪（第 6.7.1 节）。
6. **`Playlist\PotPlayerMini64.dpl` 是 PotPlayer 自己的"默认专辑"**，退出时会重写记录最后播放项。**不要改它、不要删它。**
7. **详情页有缓存 DOM**。`document.querySelector('.itemDetailPage')` 会拿到不可见的旧页面，必须取当前可见的那个。
8. **窗口句柄要用 PID 反查**。`Process.MainWindowHandle` 有时返回 0，要用 `EnumWindows` + `GetWindowThreadProcessId` + 类名 `PotPlayer64` 兜底。
9. **`ProcessStartInfo.ArgumentList` 优于拼字符串**，中文/空格路径才不会被打散。
10. **PotPlayer 播放列表窗口是自绘的**，UI Automation 读不到内容（`FindAll` 返回 0），只能靠窗口标题和窗口消息。

### 17.5 验证方式（重要：用户明确要求）

> **不要靠截图验证 UI 效果。**

用户反馈过多次：AI 截图经常截不准（窗口没置前、媒体没加载完、被其他窗口遮挡），浪费大量时间。

**正确做法**：做完一个可体验的功能后，用 `ask_user` 出**带选项卡的问题**，让用户看一眼后回答。

示例：

```
问题：PotPlayer 的播放列表窗口里，标签页名称是什么？
[A] 「誓言无声 第1季」
[B] 「默认专辑」
[C] 其他（请描述）
```

用户答一下就结束了，比 AI 反复截图快得多。

例外：**纯技术性验证可以自己完成**（窗口消息返回值、HTTP 状态码、文件是否存在、单元测试），这些不需要截图也不需要问用户。

### 17.6 环境信息（联调直接用）

| 项目 | 值 |
| --- | --- |
| Jellyfin | `https://931002.xyz:8443`，版本 12.2.0 |
| SSH | `ssh root@931002.xyz -p 2222`（已免密，可直接连） |
| 容器名 | `jellyfin` |
| 插件目录 | `/www/jellyfin/config/plugins/` |
| 媒体挂载 | `/opt/litepan/strm` → 容器内 `/media/strm` |
| 重启命令 | `docker restart jellyfin` |
| 本机 PotPlayer | `D:\Program Files\PotPlayer\PotPlayerMini64.exe`（用户机器，换机需重新发现） |
| Jellyfin 账号 | 管理员 `admin` / 普通用户 `Louis`；密码不写入仓库，见本地私有记录 |

> ⚠️ 部署到服务器前先备份 `/www/jellyfin/config/plugins/`，重启会造成几十秒不可用。

### 17.7 工作方式约定

1. **先计划后代码**：任何超出本计划的架构改动，先更新本计划再写代码。
2. **每个里程碑停下来验收**：不要连续推进多个里程碑。
3. **不要自行发版**：改完代码 + 验证通过就是终点。除非用户当轮明确说"发版"，否则不打 tag、不 push、不改版本号。
4. **中文注释**：所有源码注释用中文，说明"为什么这么写"而不是复述代码。
5. **文件行数**：单文件超过 400 行要提醒用户拆分，超过 600 行必须先拆分再继续加功能。
6. **临时文件**：放 `D:\Projects\Intermediate Products\`，不要污染仓库。
7. **UTF-8**：所有文件读写显式使用 UTF-8，禁止用 shell 重定向覆写源码（会产生中文乱码）。

### 17.8 当前遗留事项

- `potplayerbridge://` 从**真实 Chrome/Edge** 拉起本地程序，尚未在真实浏览器里确认。这是 M1/M3 联调时要确认的第一件事。
- 本机调研时注册的测试用 `HKCU\Software\Classes\potplayerbridge` **已清理**，正式安装程序会重新注册。
- `D:\Projects\Intermediate Products\potplayer-spike\` 是本次调研的临时验证目录，**用户要求等正式代码写完后再删**。
- 用户机器上可能残留调研用的 PotPlayer 窗口（播放 sample 视频），用户会自行关闭，**不要主动去关**。

---

## 18. 文档清单

| 文件 | 作用 |
| --- | --- |
| `REQUIREMENTS.md` | 需求与验收标准（唯一权威） |
| `PLAN.md` | 本文件：架构、决策、实测数据、交接说明 |
| `docs/PROTOCOL.md` | 待创建：插件 API 与代理 API 的共享协议 |
| `docs/TESTING.md` | 待创建：A1–H3 逐条验收步骤 |
| `README.md` | 待创建：两部分安装总览 |
| `server-jellyfin-plugin/README.md` | 待创建：服务器插件安装/卸载 |
| `windows-potplayer-client/README.md` | 待创建：Windows 代理安装/卸载 |
