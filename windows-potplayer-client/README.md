# PotPlayer Bridge · Windows 本地代理

这是本项目两半中的**本机侧**，装在用户自己的 Windows 电脑上。
它负责启动/监控 PotPlayer、生成播放列表、回写播放进度；网页上的播放按钮由
[Jellyfin 插件](../server-jellyfin-plugin/) 注入的脚本接管后调用本程序。

> 当前进度：M5（进度同步）。本程序已经能常驻托盘、提供本机 HTTP 接口、自动发现 PotPlayer、
> 读写设置，在网页点播放时生成整季 DPL、拉起 PotPlayer，并把播放进度回写到 Jellyfin
> （含续播跳转与防倒退守卫）。片头片尾跳转与字幕（M6）还没做。

## 目录结构

```text
windows-potplayer-client/
├─ PotPlayerBridge.sln
├─ PotPlayerBridge/
│  ├─ PotPlayerBridge.csproj
│  ├─ Program.cs                    # 入口、单实例、协议参数
│  ├─ TrayApplicationContext.cs     # 托盘菜单、启停 HTTP 服务
│  ├─ SettingsForm.cs               # 设置窗口
│  ├─ AgentRuntime.cs               # 版本/端口/跨线程请求标志
│  ├─ AppPaths.cs / Logger.cs
│  ├─ Core/AgentConfig.cs           # 配置模型
│  ├─ Core/ConfigStore.cs           # config.json 原子读写
│  ├─ Core/PotPlayerLocator.cs      # 8 步路径发现
│  ├─ Core/JellyfinClient.cs        # 调服务器 /PotPlayerBridge/Resolve（Token 只在内存里）
│  ├─ Core/DplBuilder.cs            # 生成 DPL 文本（UTF-8 BOM + CRLF）
│  ├─ Core/PlaylistStore.cs         # 列表目录（LocalAppData）、备份、清单、保留 5 个
│  ├─ Core/PlaybackService.cs       # 解析 → 关旧实例 → 写 DPL → 拉起
│  ├─ Core/PotPlayerLauncher.cs     # 启动/优雅关闭（WM_COMMAND 20002 → WM_CLOSE → Kill 兜底）
│  ├─ Core/PotPlayerWindow.cs       # 窗口查找与消息
│  ├─ Core/PlaybackModels.cs        # /api/play 请求、播放计划、错误码
│  ├─ Core/ProgressModels.cs        # /Progress 请求模型
│  ├─ Core/PlaybackProgressState.cs # 进度状态机（纯逻辑，可单测）：集匹配 + armed
│  ├─ Core/PlaybackMonitor.cs       # 采样循环、续播跳转、上报队列
│  ├─ Http/LocalHttpServer.cs       # TcpListener 极简 HTTP/1.1
│  ├─ Http/Routes.cs                # 路由、CORS、来源校验
│  ├─ Http/HttpRequest.cs / HttpResponse.cs
│  ├─ Web/settings.html             # 内嵌的网页设置页
│  ├─ app.manifest                  # asInvoker（不需要管理员）
│  └─ app.ico
├─ build.ps1                        # 发布自包含单文件
├─ install.ps1 / uninstall.ps1      # 安装/卸载（含协议注册）
└─ README.md
```

## 前置条件

| 项目 | 要求 |
| --- | --- |
| 系统 | Windows 10 / 11 x64（托盘程序与 WinForms 需要） |
| .NET | **不需要**预装：发布产物是自包含单文件 |
| 构建机 | .NET 10 SDK（`dotnet --version` 为 10.x） |
| PotPlayer | 已安装即可；路径可以自动发现，也可以手动指定 |
| Jellyfin 插件 | 建议先装服务器插件，否则网页不会调用本程序 |

## 构建

```powershell
cd D:\Projects\Jellyfin-PotPlayer-Bridge2\windows-potplayer-client
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

产物：

```text
artifacts\PotPlayerBridge\PotPlayerBridge.exe
```

单文件、自包含，拷到别的 x64 Windows 机器上也能直接运行。

## 安装

**不要**管理员权限，所有内容都在当前用户下：

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

安装脚本会：

1. 停止正在运行的旧实例；
2. 把 `PotPlayerBridge.exe` 复制到 `%LocalAppData%\PotPlayerBridge\`；
3. 注册 `HKCU\Software\Classes\potplayerbridge`（`potplayerbridge://start` / `potplayerbridge://settings`）；
4. 启动程序（托盘出现图标）。

> 如果托盘里看不到图标，点任务栏右下角的「显示隐藏的图标」，把 PotPlayer Bridge 拖出来。

## 确认装好了

1. **托盘图标存在**，右键有菜单：设置 / 自动检测 PotPlayer / 打开配置目录 / 打开日志 / 退出。
2. **健康检查**（注意必须带 `X-PPB-Client: 1`，这是安全设计的一部分）：

   ```powershell
   curl.exe -H "X-PPB-Client: 1" http://127.0.0.1:18927/health
   ```

   期望类似：

   ```json
   {"ok":true,"version":"1.0.0.0","port":18927,"uptimeSeconds":12,
    "potPlayerPath":"D:\\Program Files\\PotPlayer\\PotPlayerMini64.exe","potPlayerSource":"registry-hklm"}
   ```

3. **自动检测**：

   ```powershell
   curl.exe -H "X-PPB-Client: 1" http://127.0.0.1:18927/api/detect
   ```

   找到时返回 `{"found":true,"path":"...","source":"registry-hklm"}`；找不到返回 409。

4. **设置页**：浏览器打开 `http://127.0.0.1:18927/settings`，
   能看到服务器地址、PotPlayer 路径，并能「自动检测 / 保存」。

5. **设置窗口**：双击托盘图标，窗口里可以浏览选择 `PotPlayerMini64.exe`、打开日志。

6. **拉起播放（M4）**：确保 Jellyfin 插件已安装并已登录网页，打开任意季/集详情页，
   点「播放」或某集海报上的播放按钮，应当：

   - 网页不进入自带播放器，而是出现「正在启动 PotPlayer…」提示后自动消失；
   - 本机弹出 PotPlayer，播放列表名为「剧名 第N季」，并停在点击的那一集；
   - 连点两集时只保留一个 PotPlayer 实例（后点的那集生效）；
   - 一集播完自动接下一集（列表里有下一集时）。

   也可以直接用 HTTP 验证（Token 从网页控制台 `ApiClient.accessToken()` 取，不要写进任何文件）：

   ```powershell
   $body = '{"serverUrl":"https://931002.xyz:8443","accessToken":"<token>","itemId":"<episodeId>","intent":"item"}'
   curl.exe -X POST http://127.0.0.1:18927/api/play `
     -H "X-PPB-Client: 1" -H "Content-Type: application/json" -d $body
   ```

   期望：

   ```json
   {"ok":true,"playlistName":"誓言无声 第1季","startIndex":4,"playlistCount":20,
    "itemId":"...","itemType":"Episode",
    "dplPath":"C:\\Users\\<你>\\AppData\\Local\\PotPlayerBridge\\playlists\\誓言无声 第1季.dpl",
    "playlistDir":"C:\\Users\\<你>\\AppData\\Local\\PotPlayerBridge\\playlists",
    "processId":1234,"windowHandle":5678}
   ```

   失败时返回 `{ "code": "...", "message": "中文提示" }`，常见 code：
   `potplayer-not-found`(409)、`server-unreachable`(502)、`resolve-failed`(服务器侧)、
   `no-token` / `bad-intent` / `bad-item-id`(400)。

> 注意：**PotPlayer 退出时会把当前播放项写回 `.dpl`**（`topindex` / `playname` 都会变），
> 所以代理固定按「先关旧实例 → 再写 DPL → 最后启动」的顺序执行。
> 如果改成先写后关，刚算好的起播集会被旧实例的退出状态覆盖，表现为「点了 E4 却播 E5」。

> 列表标签名前那个 `*` 是 PotPlayer 给「当前播放列表」打的未保存标记，与 DPL 内容无关
> （`topindex=0`、补 `duration2`、补 `playtime`、把文件设为只读，四种写法都实测过），去不掉，属正常现象。
> 但是**不能**把 DPL 写进 PotPlayer 的 `Playlist` 目录：那样 PotPlayer 会额外把它登记成常驻「专辑」，
> 播放列表里就会出现两个同名条目，而且关掉播放器后仍留在专辑里（下一个用这台电脑的人能看到你都播过什么）。

> 代理重启时（含 `install.ps1 -Force` 强杀）会读 `%LocalAppData%\PotPlayerBridge\potplayer-session.json`
> 里记的 PID，把上次遗留的播放器关掉。否则新旧两个实例会同时播放，M5 的进度回写会互相覆盖。
> 只会处理该 PID 且进程名匹配 `PotPlayer*` 的情况，你自己开的播放器不受影响。

## 进度同步（M5）

代理在播放期间每秒读一次 `0x5002`（总时长）/`0x5004`（当前位置）/`0x5006`（状态），
并用窗口标题识别当前是第几集，然后按下面的规则上报：

| 时机 | 行为 |
| --- | --- |
| 正常播放 | 每 5 秒一次 |
| 暂停 / 恢复 | 立即一次 |
| 拖动进度条（同集内跳变 > 10 秒） | 立即一次，带 `seeked` |
| 换集 | 上一集 `stopped`（接近结尾则标已看完）+ 新一集开始 |
| 播到结尾 | 带 `ended`，服务器标记已看完并把位置归零 |
| 关闭播放器 / 代理退出 | 最后一次 `stopped`，位置精确到关闭前一秒 |

**续播**：`Resolve` 给的 `startPositionTicks` 会被真正执行——媒体就绪后用 `0x5005` 跳到该位置，
STRM 首次加载慢时重试最多 6 次。

**为什么要有 `armed`**：这是「宁可少报一次，也不要用错误的值覆盖」的落点。

- 只有位置落在期望起播点 ±5 秒（或期望本来就是 0 且连续 3 秒在增长）才 `armed`。
- 假续播（服务器说从 20:00 续播、播放器却从头开始）**永远不会 armed**，因此不会把 20:00 写成几分钟。
- 服务器侧还有第二道防线：未 armed 的上报只能往上抬，不能倒退、不能清零。

设计细节见 [PLAN.md](../PLAN.md) 5.7 / 6.6 / 6.9。

## 配置

配置位置：`%LocalAppData%\PotPlayerBridge\config.json`

```json
{
  "serverUrl": "https://931002.xyz:8443",
  "potPlayerPath": "D:\\Program Files\\PotPlayer\\PotPlayerMini64.exe",
  "port": 18927,
  "extraAllowedOrigins": []
}
```

| 字段 | 说明 |
| --- | --- |
| `serverUrl` | Jellyfin 地址，用于推导允许的网页来源；默认值与联调环境一致 |
| `potPlayerPath` | 可执行文件路径；留空则每次启动自动发现 |
| `port` | 本机端口，固定 18927（注入脚本里写死了这个地址） |
| `extraAllowedOrigins` | 额外放行的网页来源，一般不需要 |

日志：`%LocalAppData%\PotPlayerBridge\agent.log`（轮转上限 2 MB）。
**日志里不会出现任何 Token。**

## 路径发现顺序

按顺序尝试，命中即停（详见 [PotPlayerLocator.cs](PotPlayerBridge/Core/PotPlayerLocator.cs)）：

1. 配置里记住的路径（必须存在且文件名含 PotPlayer）；
2. `HKCU\Software\DAUM\PotPlayerMini64` / `PotPlayer64` 的 `ProgramPath` / `ProgramFolder`；
3. 同样的键在 `HKLM`（含 32 位视图）；
4. `potplayer://` 协议命令（解析命令行里的 exe）；
5. `App Paths\PotPlayerMini64.exe` 等；
6. 常见安装目录（`Program Files\DAUM\PotPlayer` 等）；
7. 固定磁盘浅层扫描（深度 2，最多 4000 个目录 / 8 秒）；
8. 开始菜单快捷方式（解析 `.lnk` 目标）。

已记住的路径失效时会自动重新发现，不会直接报错（验收项 F4）。

## 安全设计

- 只监听 `127.0.0.1`，局域网/公网无法访问；
- 请求带 `Origin` 时，必须命中 `serverUrl` 推导出的源或本机设置页；
- `/health` 与 `/api/*` 强制要求 `X-PPB-Client: 1`，浏览器跨域必须先预检，恶意网页无法静默调用；
- `/api/play` 会临时接收 Jellyfin Token，但**只在内存里用于一次 Resolve 请求**，不落盘、不写日志；
- DPL 里的播放地址带 Jellyfin 直链参数，属于 PotPlayer 播放必需内容，会写进
  `%LocalAppData%\PotPlayerBridge\playlists`（**不写** PotPlayer 自己的 `Playlist` 目录，
  否则 PotPlayer 会把它登记成常驻「专辑」，既会出现同名重复标签，也会把观看记录留在播放列表里）；
  代理只管理自己创建过的文件（`created-playlists.json`），删除前会比对目录快照。

## 更新

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

配置和日志在 `%LocalAppData%\PotPlayerBridge\`，更新时不会丢。

## 卸载

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
```

默认保留配置与日志；要彻底清理：

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -RemoveData
```

卸载后 `potplayerbridge://` 协议会一起移除，网页再次点播放会走「未安装」提示（服务器插件那侧）。

## 只装了这一半会怎样

- 桌面/托盘里会多一个 PotPlayer Bridge，`/health` 正常，但**网页点播放没有任何变化**——
  因为网页脚本来自 Jellyfin 插件，没有插件就没有调用方。
- 反过来只装服务器插件：网页会注入脚本，但点播放时本机 18927 连不上，
  脚本会提示「无法启动本地 PotPlayer 桥接程序」。

## 当前进度（M5）

- [x] WinForms 托盘、单实例、无主窗口
- [x] 自包含单文件发布（无需预装 .NET）
- [x] TcpListener 本机 HTTP（127.0.0.1:18927）+ CORS + Origin / `X-PPB-Client` 校验
- [x] `/health`、`/api/settings`、`/api/detect`、`/settings`
- [x] PotPlayer 路径发现（8 步）+ 手动指定 + 失效重查
- [x] `potplayerbridge://` 协议注册（install.ps1）
- [x] `/api/play`：调 Resolve → 生成整季 DPL → 拉起 PotPlayer
- [x] 实例管理：先关旧实例再写列表，连点只留一个实例；异常退出标记 + 列表快照
- [x] 代理重启时清理遗留播放器（`potplayer-session.json` 记 PID），避免重装/崩溃后出现两个实例同时播放
- [x] 列表只写 `%LocalAppData%\PotPlayerBridge\playlists`（不污染 PotPlayer 专辑），清单登记 + 只清理自己的文件（保留最近 5 个）
- [x] M5：每秒采样（`0x5002/0x5004/0x5006`）+ 窗口标题识集 + armed 状态机 + 5 秒上报
- [x] M5：续播跳转（`0x5005`，媒体就绪后重试）、暂停/拖动/停止/播完事件上报
- [ ] 片头片尾与字幕（M6）
