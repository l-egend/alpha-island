# Alpha Island

Windows 版「灵动岛」：屏幕顶部居中的液态玻璃浮岛，监听 **SMTC** 显示当前正在播放的媒体，
接上 **WASAPI Loopback 实时音频频谱**（真波形，不是假动画），并从**网易云**拉同步歌词做岛内卡拉OK 滚动。

---

## 快速开始

使用 **Visual Studio** 打开 `alpha-island.sln`，选择 **Debug / Any CPU**，将 `alpha-island` 设为启动项目，按 **F5** 调试；按 **Ctrl+Shift+B** 普通编译。

项目目前处于构建和实验阶段，开发期间只做普通编译与必要回归检查；功能完成后再打包。日常不生成 MSIX、签名证书或额外发布副本。具体流程见 [开发与构建](docs/开发与构建.md)。

独立扩展模块可通过设置 → 插件启用；解决方案附带默认关闭的时钟示例与 TrafficMonitor 接收插件。后续新增卡片、设置、服务和网络数据源可参考 [插件开发文档](docs/插件开发.md)。

外部 Agent 可接入运行状态和工具权限审批，见 [DeepSeek Harness 接入](docs/DeepSeekHarness接入.md) 与 [Codex 接入](docs/Codex接入.md)。

全程序配置集中保存在 `%LOCALAPPDATA%\alpha-island\config`，旧配置启动时自动迁移。AI 密钥使用 Windows DPAPI 加密保存，详见 [AI 验证与配置目录](docs/AI验证与配置目录.md)。设置 → 关于可直接打开配置目录；设置窗口支持置顶显示和 Esc 关闭。

AI 设置新增连接、模型列表选择及所选模型实际测试，详见 [AI 模型选择与连接测试](docs/AI模型选择与连接测试.md)。

需要命令行运行时：

```powershell
cd X:\FixtureWorkspace\alpha-island
dotnet run
```

* 鼠标移到岛体上 → 展开成媒体卡片
* 点击岛外任意位置 → 收起
* 换曲瞬间 → 自动弹出紧凑胶囊，6 秒后自动收起
* 托盘图标：**左键单击**打开设置，右键弹菜单（⚙️ 打开设置 / 🧹 整理内存 / 退出程序）
* 卡片页包含媒体播放器 / 剪切板 / 音量 / AI 对话 / 系统状态与网络 / 下载 / 应用联动 / 消息 / 天气 / 番茄钟 / 屏幕工具；AI 页按设置启用。媒体播放器支持 Chrome、Edge 发布的 Windows 媒体会话，来源箭头可选择自动或手动跟随；视频不搜索歌曲歌词。主卡片旁显示其他活跃组件的缩略岛，点击可切换并保持展开。
* **设置 → 行为 → 媒体暂停时隐藏**：默认关闭。开启后暂停媒体退出主岛和旁侧小岛，后台会话与置顶状态仍保留；继续播放或关闭此开关即恢复。仍可从组件菜单手动打开播放器。
* 原圆点卡片分页已全部改为岛外右侧轮廓线：线条与岛体留有间距，小岛时沿圆角弯曲，卡片展开后为竖线。单击下一卡片，滚轮切换；长按约 0.4 秒展开全部可用卡片的紧凑圆形菜单，按住移向图标时只高亮并显示名称，松手才打开卡片。移出菜单缓冲范围、在空白处松开或按 Esc 取消。
* 在胶囊或卡片上**右键 → 置顶常驻**（岛不再自动收回，再点一次取消）
* 下载期间（下行 ≥ 128 KB/s）细条会常显「正在下载 + 实时下行/上行」的胶囊，停止 3 拍后自动收起
* **应用联动（只读，默认开启，可在 设置 → 系统状态 → 应用联动 关掉）**：
  * **手机联动（vivo 办公套件）**：只读 `%APPDATA%\pcsuite\historyPhone.json`，手机连上 / 断开时弹一条提示（设备名 / 连接方式 / IP），系统状态卡片底部常显「手机：…（最近连接时间）」，并且**第 8 页「应用联动」卡片**把这条记录摊开成四行（设备 / 状态 / 连接 / 地址），这一页永远能翻到 —— 关掉开关时页内写「手机联动已关闭」。**手机实时电量、文件传输进度它没有写进文件，取不到**。
  * **网络数据（TrafficMonitor）**：可选桥接 DLL 与接收插件提供原始速率、今日字节计数，并接入所有网络岛显示；未连接时使用本机采样并标明来源。历史文件以 KB 保存，已修正单位换算。安装和验证见 [TrafficMonitor 桥接](docs/TrafficMonitor桥接.md)。
* **下载进度（只读，默认开启，可在 设置 → 系统状态 → 下载进度 关掉）**：
  * **浏览器（Chrome / Edge）**：读它们 `User Data\<profile>\History` 里的 `downloads` 表（用系统自带的 `winsqlite3.dll`，不引 SQLite 依赖）。**但 Chromium 只在下载结束时才写这张表**：实测探针（30 MB 慢速下载 40 秒）在整个下载期间，直连只读（浏览器在跑就报 `prepare rc=5 database is locked`）和「复制主库 + `-wal` 再读副本」两种方式都是 0 行，下载一结束才出现 `state=1` 那行；盘上的 `*.crdownload` 半成品文件能给「已下字节」（实测随下载一起涨：30 MB / ~2.5 MB/s 那轮 4.3 → 8.6 → 12.8 → 17.0 → 21.3 → 25.4 → 29.6 MB），但**没有总量**，所以拿不到百分比、也拿不到剩余时间。所以浏览器侧**只做「正在下载」与「下载完成」两种提示**（细条上不出现字节数、百分比与剩余时间，也不编一个），卡片里最近完成的条目才显示 100%。**Firefox / Brave 等本机没装的浏览器，未验证、取不到**。
  * **Steam**：只读各库的 appmanifest 清单发现任务，再增量读取 `logs/content_log.txt` 的客户端事件，区分下载、暂停、排队、准备、校验和文件更新。`UpdateStarted` 在暂停后仍保留，不作为运行判据；ACF 字节数是保存的快照，下载字节与文件更新字节不能相加后冒充实时进度。卡片与小岛不显示不可靠的进度条、百分比、大小、速率和 ETA，也不借整机网速；暂停只提醒一次并保留在卡片中。日志不可用时保守显示等待状态更新。Epic / 战网 / WeGame 等尚未验证。

---

## 结构

| 文件 | 职责 |
|---|---|
| `Services/SmtcService.cs` | 包装 `Windows.Media.Control` 的 WinRT 投影：媒体会话、封面、播放状态、进度、上一首/下一首/播放暂停/跳转。事件统一派发到 UI 线程。 |
| `Services/AudioSpectrumService.cs` | WASAPI Loopback 采集默认播放设备 → 单声道混音 → 汉宁窗 + FFT(2048) → 64 个对数频段 → dB 归一化 + 快起慢落平滑。 |
| `Services/LyricsService.cs` | 用曲名+艺术家+时长去网易云搜索歌曲 ID（模糊匹配打分），再拉 LRC / 翻译 / 罗马音 / 逐字歌词。换曲才请求一次，结果按 `曲名\|艺术家` 缓存。 |
| `Services/LrcParser.cs` | LRC / yrc 解析器：多时间戳、四种精度、元数据与制作人行过滤、翻译按毫秒对齐、逐字时间解析。 |
| `Controls/SpectrumVisualizer.cs` | 纯 `FrameworkElement`，`OnRender` 画镜像胶囊柱 + 横向色相渐变。`PushFrame` 线程安全；`IsActive=false` 时完全停止逐帧渲染。 |
| `MainWindow.xaml(.cs)` | 三态状态机（细条 / 胶囊 / 卡片）、三段式收回动画 + Siri 球、动画、托盘、悬停轮询、歌词卡拉OK。**代码已按职责拆成多个 partial 文件**（`MainWindow.State/Cards/Pages/SystemCards/Media/Services/Interaction/Reveal/Volume/Battery/Clipboard/Notifications/Lyrics/SystemStatus/Ai/Agent/AgentBridge/Motion/Cover3D/Startup…`），`MainWindow.xaml.cs` 只留字段、构造、托盘与优雅退出。 |
| `MainWindow.Cover3D.cs` | **可选 3D 封面**（设置 → 岛屿与动效 → 「3D 封面」，默认关闭）：卡片封面槽换成 `Viewport3D` 里的立体卡片，换曲时绕 Y 轴 `-90°→0°` 翻转入场（420ms，`减弱动效` 时直接到位）。开启前先查渲染层级（**需 `RenderCapability.Tier ≥ 2`**，否则拒绝并说明；不满足只拒本次会话、不改写设置），再用 `RenderTargetBitmap` 探一次「这个分层窗口里 3D 到底画不画得出来」（0 像素自动退回 2D）。为保证软件渲染路径可用，材质用的是 `DiffuseMaterial` + `ImageBrush`，**不是** `Viewport2DVisual3D`。 |
| `MainWindow.Pages.cs` + `MainWindow.SystemCards.cs` | 卡片页宿主与合并的**系统状态与网络卡片**，始终出现在组件菜单里，服务关闭时也能进入并查看未开启提示。卡片显示上传/下载、会话流量和峰值、网卡与实时曲线，以及 CPU、内存、放电功率、飞行模式与麦克风/摄像头占用。 |
| `Controls/Sparkline.cs` | 轻量迷你折线（面积填充 + 描线，`SetValues`/`Push`/`Clear`，自适应上限 = 峰值 ×1.1）。**故意不复用 `WaveVisualizer`**：那个是频谱专用且每帧离屏，画历史曲线只需在数据到达时重绘一次。 |
| `Core/IslandLayerHost.cs` | 细条上的通知图层宿主：注册 `IslandLayerSpec`（键 / 时长 / 作用域 / 优先级）→ `Show*` 显示。**`Sync` 里按优先级做单占仲裁**（同一时刻只有一个层可见，平手保注册在前的），并对外回调 `SyncRequested`（`Show/Hide/HideAll` 末尾触发），从根上杜绝两层文字叠在一起。 |
| `Services/VivoPhoneService.cs` + `Services/TrafficMonitorService.cs` + `MainWindow.Link.cs` | **应用联动**（第二批②，全部只读外部程序写的文件，免提权免注入）。vivo：按文件 mtime 决定是否重新解析 `historyPhone.json`（用正则而不是 `JsonSerializer`——同目录的 `config.json` 根本不是合法 JSON），`"phone": []` 非空 = 此刻连着，`lastSuccessTime` 取最大值。TrafficMonitor：进程路径取不到（`Get-Process`/`Win32_Process` 都为空），所以按候选路径扫盘找 `history_traffic.dat`（首行 `lines:` + `yyyy/MM/dd 上传/下载`）。`MainWindow.Link.cs` 用两条 ticker 采样（vivo 5s / 流量 15s），vivo 首拍只做基线、之后「换设备 / 连接时间变 / 连接状态翻转」才弹提示（图层 `vivo`，优先级 74）。第 8 页「应用联动」在 `MainWindow.LinkCard.cs`：它自己**不读文件**，只把 `MainWindow.Link.cs` 采到的 `_vivoInfo` 摊成四行（设备 / 状态 / 连接 / 地址）——这样卡片、系统状态卡底部那行、细条提示三者必然同源；每次刷新还会写一行 `(Cards) 应用联动卡：…｜界面 …` 供脚本核对界面文案。TrafficMonitor 的每日流量暂时不占这一页（用户说后期单独搞），仍然只在网速卡的提示行里露一行。 |
| `Interop/WinSqlite.cs` + `Services/DownloadMonitorService.cs` + `Services/SteamDownloadLog.cs` + `MainWindow.Downloads.cs` | **下载状态**（全部只读）。浏览器读取系统 SQLite 下载记录与 `.crdownload` 文件，History 主库及 WAL 的指纹不变就复用缓存。Steam 清单发现任务，客户端日志确认具体阶段；日志首次最多读末尾 2 MiB，之后只读增量，处理半行与截断。暂停、排队、消失都不算完成；Steam 需要明确完成事件及本次运行之后的完成时间，浏览器需要完成记录。只有可信进度显示完整进度轨道，Steam 实时进度不可用时整条收起。图层 `download` 优先级 66，采样在后台每 2 秒一次；持续刷新不抢用户选中的主岛，暂停事件只提醒一次。 |
| `SettingsWindow.xaml(.cs)` | 8 个分类页（外观 / 岛屿与动效 / 音频 / 歌词 / 行为 / 系统状态 / AI / 关于）。设置项由 `Core/SettingsRegistry.cs` 的描述符表驱动：**加一条描述符 + 在同名控件上挂 `Setting_Changed` 就接完**（回填、写回、级联禁用、导航搜索索引都自动）。含搜索框（按 `Uid + Tag + 注册表 Label` 过滤导航）、「恢复默认」与运行期状态清理。 |
| `App.xaml.cs` | 全局异常兜底：写 `%LOCALAPPDATA%\alpha-island\error.log`，不弹 WinForms 模态框。 |

数据流：

```
SMTC   ──► SmtcService ──────┬─► MainWindow.ApplyNowPlaying ─► 卡片 UI
                             └─► LyricsService ─► LrcParser ─► 歌词区（60fps 卡拉OK 擦除）
WASAPI ──► AudioSpectrumService ─► SpectrumVisualizer.PushFrame ─► OnRender(60fps)
```

歌词匹配打分（`LyricsService.Score`）：曲名完全一致 +55 / 包含 +34 / 二元组相似度兜底；
艺术家重叠最多 +30；时长差 ≤2s +25、≤5s +15、>40s −25。低于 55 分不显示，避免张冠李戴。

单测台（独立编译这两个源文件，不起 UI）可复现全部边界：

```powershell
# %TEMP%\lyricprobe 下 <Compile Include> 这两个文件即可
```

---

## 三段式收回动画（用户早期设计定稿，勿简化）

需求来自早期的 Gemini 设计对话（已导出为
`X:\FixtureWorkspace\_shots\gemini_windows_plugin.txt`，需求清单见
`X:\FixtureWorkspace\_shots\windows_island_requirements_checklist.md`）。
用户原话：**「缩回动画太生硬了，改成先缩成一个圆再向上缩回，类似 Google play 游戏的弹窗动画」**
→ 再迭代：**「先向上缩回再逐渐变为长条状（待机状态），这一版的速度过快，造成较大的割裂感，
且在回缩成圆的时候可以将右边的图标代替这个球的窗口，例如这个 demo 所带的 siri 图标」**。

定稿参数（`MainWindow.CollapseToBar()`）：

| 阶段 | 时长 | 缓动 | 做什么 |
|---|---|---|---|
| 1 向心聚合成球 | **260→280ms** | `CubicEase/EaseInOut` | 内容层 110ms 淡出；W/H→**46**、Y→18、圆角→23；**Siri 炫彩球淡入并居中当球心** |
| 2 向上吸附 | **220→240ms** | `CubicEase/EaseIn` | **只做纯位移** Y→0，保持 46px 正圆；Siri 球同步渐隐 |
| 3 贴顶横向舒展 | **220→240ms** | `CubicEase/EaseOut` | W→130、H→10、圆角→(0,0,10,10)，Y 不再变 |

总时长 ≈700ms。**关键约束：位移与形变必须分离，禁止「一边上升一边变形」**（用户明确否定的版本）。
每段用动画 `Completed` 串联，回调开头都检查 `_state != IslandState.Bar` 就整体放弃，
中途被 hover 唤回不会和展开动画打架。展开方向仍是 360ms `BackEase/Amplitude≈0.25` 的一次性弹性动画。

---

## 托盘 & 任务栏（用户早期设计定稿）

用户原话：**「设置界面改为像程序一样在任务栏出现，在启动时可以在托盘处打开设置，
而不是右键打开设置，因为岛在后期需要用鼠标按键完成其他交互」**。

* 岛体窗口 `ShowInTaskbar="False"`；**设置窗口 `ShowInTaskbar="True"`**（普通软件形态，任务栏有独立图标）。
* 托盘 `WinForms.NotifyIcon`：**左键单击**打开设置（另外保留双击与右键菜单「⚙️ 打开设置 / 退出程序」）。
* 岛体本身**没有右键菜单** —— 右键留给后续交互。
* 单实例 Mutex，二次启动提示「灵动岛已经在运行中，请检查任务栏右下角托盘！」
* **图标**：内置 `Assets\alpha-island.ico`（16/24/32/48/64/256 六帧，全部 32bpp DIB，无 PNG 压缩帧），
  由 `tools\make-icon.ps1` 生成（改配色就改脚本里的渐变端点后重跑；脚本用 Windows PowerShell 5.1 跑，
  带中文，**必须保存为 UTF-8 with BOM**）。托盘图标按系统 DPI 取帧（本机 dpi=96 → 16px）。
* **换图标不用改代码**：把自己的 `.ico` 放到 `%LOCALAPPDATA%\alpha-island\config\icon.ico`，重启后托盘与各窗口都用它；
  找不到才回退内置资源 → exe 内嵌图标 → `SystemIcons.Application`。
* **设置页图标槽也能换图**：把 20x20 左右的 PNG 按槽名放进 `%LOCALAPPDATA%\alpha-island\config\icons\`
  （或程序目录 `Assets\icons\`），文件名 = 选项键 + `.png`（如 `AiEnabled.png`），打开设置页即生效。

---

## 踩过的坑（改代码前务必看）

### 1. 目标框架必须是 `net8.0-windows10.0.19041.0`

只写 `net8.0-windows` **拿不到 WinRT 投影**，`Windows.Media.Control` 根本不存在于编译期。
带上平台版本后 SDK 才会引入 `Microsoft.Windows.SDK.NET.Ref`。

CsWinRT 的 `IAsyncOperation<T>` **没有** `GetAwaiter()`，必须 `.AsTask()` 后再 await：

```csharp
var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask();
```

### 2. NAudio 必须用 2.2.1，不能用 3.x

NAudio 3.1.0 只支持 `net9.0`，在 net8.0 上会 `NU1202`。

### 3. FFT 归一化（曾经导致频谱恒为空）

NAudio 的 `FastFourierTransform.FFT` **内部已经按 1/N 归一化**：
幅度 1.0 的满幅正弦 + 汉宁窗，输出约 **0.25**。所以只需 `×4` 还原到 0 dBFS。

早期版本按教科书写法又除了一次 `FftSize`，结果每个频段都落到 −100 dB 以下、
被噪声门吃光，柱子永远为 0。**这是本项目最容易复发的 bug。**

### 4. `WasapiCapture.StopRecording()` 会 Join 采集线程 → 锁纪律

采集线程只在 `AudioSpectrumService._gate` 上短暂停留。
如果在**持有 `_gate`** 的情况下调用 `StopRecording()` / `Dispose()`，
采集线程可能正阻塞在这把锁上，Join 永不返回 → 退出程序时死锁。

约定：

* `_gate` 只保护音频数据缓冲，采集线程只用这一把；
* `_lifecycle` 串行化设备创建/销毁；
* 停止/释放 NAudio 对象时**不得持有 `_gate`**；
* 锁顺序固定 `_lifecycle → _gate`。

### 5. 窗口 `Background` 必须是 `{x:Null}`，不能是 `Transparent`

写成 `Transparent` 会让整块 620×330 的透明窗口都参与命中测试，
**挡住屏幕顶部整片区域的鼠标点击**（用户会发现浏览器标签点不动）。
设为 `null` 后只有真正渲染出来的岛体吃掉鼠标事件。

### 6. `ImplicitUsings` + `UseWindowsForms` 会让类型撞名

`Color` / `Point` / `Cursor` / `Timer` / `Application` 等同时存在于
`System.Drawing` / `System.Windows.Forms` / `System.Windows`。
在新文件里用别名或全限定名：

```csharp
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
```

### 7. 悬停判定绝对不要用 MouseEnter/MouseLeave，也不要缓存钩子里的光标坐标

细条在 `y=0..10`，展开卡片从 `y=18` 起，中间有 8px 缝。后果：

* 光标停在细条上 → 卡片展开后光标"落空" → 一抖鼠标就 `MouseLeave` → 收起 → 又触发
  `MouseEnter` → **展开/收起来回跳**（已实测复现）。
* 反过来，光标停在细条上移走时，WPF 认为它"从未进入岛体"，**根本不产生 `MouseLeave`**，
  岛体会永远展开。

最终方案：`HoverTimer_Tick` 每 120ms 现取光标位置判定，连续 3 次不在热区才收起。
热区 = 当前岛体屏幕矩形 ∪ 顶部细条固定区域（`IsCursorInHotZone`）。

**另一个更隐蔽的坑**：曾用全局 `WH_MOUSE_LL` 钩子的 `WM_MOUSEMOVE` 缓存光标坐标，
结果热区判定永远失败 —— 日志显示 `cursor=1205,346` 从头到尾没变过，即
**`SetCursorPos` / 程序化移动不一定走到低级鼠标钩子**。所以判定必须现取
`GetCursorPos()`；钩子只用于"点击岛外立即收起"。

### 8. 卡片高度动画不能被高频事件反复重启

`SetState(Expanded, force:true)` 会重新开始 90ms 延迟 + 200ms 的淡入动画。
如果换曲判定被 SMTC 的元数据抖动（偶尔给空标题）反复触发，`ApplyLyrics(null)` 会不停
重启动画，**卡片会一直停在"半透明空壳"状态**（截图表现为一个纯深色空卡片）。

对策三件套：换曲判定要求"曲名非空且真的变了"；`_lyricsKey` 保证同一首歌只处理一次；
`ApplyLyrics` 只在"有无歌词"真的翻转时才 `force` 重定向高度。另外"媒体丢失"用
1.2s 延时确认，避免换曲瞬间的 session 闪断把卡片打回收起态。

---

## 网易云歌词接口（实测 823 首，2026-09）

非官方接口，无需登录 / 签名 / Cookie，UA 和 Referer 其实都可省。**HTTP 状态码永远是 200，
真正的状态在响应体的 `code` 字段里**；返回 `Content-Type` 是 `text/plain` 而不是 JSON，
所以不要按 Content-Type 分支，直接 JSON 解析。

```
搜索  GET https://music.163.com/api/search/get?s=<urlencoded>&type=1&limit=10
歌词  GET https://music.163.com/api/song/lyric?id=<id>&lv=-1&kv=-1&tv=-1&rv=-1&yv=-1
```

必须知道的几点（都踩过）：

| 事实 | 说明 |
|---|---|
| **`nolyric` 字段不存在** | 823 首里 0 次。纯音乐用 **`pureMusic: true`** 标记，正文是 `[00:05.00]纯音乐，请欣赏`。只判 `nolyric` 等于没判。 |
| **`romalrc` 需要 `rv` 参数** | `lv`/`kv`/`tv`/`rv`/`yv` 都是"**只要出现就返回对应段落**"，取值本身被忽略。少写 `rv` 就永远拿不到罗马音。 |
| **`/api/song/lyric/v1` 不能用** | 它的 `lrc.lyric` 是每行一个 JSON 对象，不是 LRC 文本。要用 `/api/song/lyric`。 |
| **`tlyric` 可能全行皆空** | id=186016（晴天）的 `tlyric` 存在但每行文本都是空串。按"有 key = 有翻译"判断会显示一片空白；必须逐行判空。 |
| **翻译必须按时间戳对齐** | `tlyric` 与 `lrc` 行数不同（实测 52 vs 48、46 vs 42…），**绝不能按行号 join**。本项目按毫秒对齐，`.5` 与 `.50` 视为同一时刻。 |
| **时间戳小数是"秒的小数"** | `.5` = 500ms、`.90` = 900ms、`.000` = 0ms，不是整数毫秒。四种精度都真实存在。 |
| **一行可有多个时间戳且乱序** | 例：`[03:12.50][03:00.84]…[00:52.07]啊观世音菩萨`，第一行是最大的。必须全部展开再排序。 |
| **`result.songs` 找不到时是"不存在"** | 不是 `null` 也不是 `[]`，直接 `{"result":{"songCount":0}}`。索引前必须判存在。 |
| **搜索结果有两套 schema** | 垃圾查询会命中 fallback 后端，song 对象多出 `rUrl` 等字段。按属性名反序列化，不要依赖顺序。 |
| **服务器排序不可信** | 搜"夜曲 周杰伦"返回的第一条是他人的翻唱。必须客户端按 曲名+艺术家+时长 打分。 |
| **yrc 逐字歌词** | `yv` 参数开启，约 1/3 的歌有。格式 `[起始ms,持续ms](字起始,字持续,0)字…`，开头可能有 JSON 制作人行。有它就能做**逐字**卡拉OK，比按行线性插值准得多。 |
| **制作人行是普通歌词行** | `[00:00.000] 作词 : 周杰伦`、`[00:02.250] 词：周杰伦`，会在开头闪一下。而且角色常用 `/`、`、` 串起来（`作词/编曲/混音:4st`、`pv/美工:嘛佑`），只匹配单个角色会漏 —— 见下面第 9 条的判定方式。 |
| **罗马音只对日文有意义** | 中文/粤语歌的 `romalrc` 是拼音/粤拼，显示出来只是噪音。本项目只在原文含假名时才启用罗马音兜底。 |
| **反爬几乎没有** | ~800 次连续请求零限流、零封禁；延迟 搜索 250–450ms / 歌词 110–160ms。 |

### 9. 制作人行要按"角色词表"判，不要写死正则

`作词/编曲/混音:4st` 这类多角色行用 `^(作词|作曲|...)\s*[:：]` 匹配不到。
正确做法：先切出冒号前的角色串，按 `[/、,，&+·]` 拆开，**要求每个角色都命中词表**才算制作人行。
这样普通歌词里的冒号（「我说：你好」→ 角色串是「我说」）不会被误杀。
实现见 `LrcParser.IsCreditLine`。

### 10. XAML 里 `IsChecked="True"` 会在 `InitializeComponent()` 期间就触发事件

`SettingsWindow` 里 `LyricsToggle` 排在 `TranslationToggle` 前面，而
`LyricsToggle_Changed` 会去设 `TranslationToggle.IsEnabled` —— 此时后者**字段还是 null**，
`new SettingsWindow(...)` 直接 `NullReferenceException`，托盘点设置只弹一个
"Microsoft .NET 未处理的异常" 框，设置窗口永远打不开。

对策：`_suppress` 字段**初值设为 `true`**，构造函数末尾才置 `false`；
处理器里引用同级控件一律判空。这个 bug 只在"加完控件后没再打开过设置窗口"时才会漏掉 ——
**改完设置窗口一定要真的点开看一眼。**

### 11. Win11 托盘图标"消失"：可见性按 exe 完整路径记在注册表里

`HKCU\Control Panel\NotifyIconSettings\<hash>` 每条含 `ExecutablePath`、`InitialTooltip`、
`IconSnapshot`，以及 **`IsPromoted`（DWORD，1 = 固定在任务栏可见区，缺失/0 = 丢进溢出区）**。

**改 `TargetFramework` 会改变输出目录 → exe 路径变了 → 变成"新程序" → 默认进溢出区**，
用户就会觉得"托盘图标没了"。验证方法（UI Automation 枚举 `Shell_TrayWnd` 子孙节点）：

```powershell
# 名字应该出现为 "Alpha Island (灵动岛)"，class = SystemTray.NormalButton
```

修法：给对应 `ExecutablePath` 的键写入 `IsPromoted = 1`（DWord），再重启应用即可，
**无需重启 explorer**。

同理，**任何改变输出路径的操作**（改 `TargetFramework`、加 `RuntimeIdentifier`、改 `AssemblyName`）
都会让托盘图标重新掉进溢出区；只换图标文件（`Assets\alpha-island.ico` 或用 `%LOCALAPPDATA%\alpha-island\config\icon.ico`）
不影响可见性。

### 12. 全局底层钩子 (`WH_MOUSE_LL` / `WH_KEYBOARD_LL`) 不能装在 UI 线程上

低级钩子的回调由系统投递到**安装钩子的那个线程**的消息队列，所以只要钩子装在 WPF UI 线程上，
UI 线程任何一次长任务（实测：`downloads` 订阅在 UI 线程上同步读浏览器 `History`，每拍 812–843 ms）
都会被放大成**整机鼠标输入停顿**——鼠标光标不动、画面照常（DWM 继续显示上一帧），
这正是 alpha_74 事故里用户看到的「每过几秒卡一下，只有鼠标不动」。
现在 `Interop/HookThread.cs` 给钩子一个专属后台线程 + 自己的消息循环（起循环前先 `PeekMessage(PM_NOREMOVE)`
把队列建出来，否则 `WM_QUIT` 投不进来；`UnhookWindowsHookEx` 也在该线程的 `finally` 里做），
事件经 `MouseHook.Post` 用 `Dispatcher.BeginInvoke(…, DispatcherPriority.Input)` 投回 UI，
同步判据（`ShouldInterceptRightClick` / `IsInsideIsland`）仍必须**立刻返回**（跨线程读岛矩形走
`PublishIslandRect` 发布的装箱快照，避免 32 字节 `Rect` 被撕裂）。
另外 `PointToScreen` 返回的是**物理像素**、`ActualWidth` 是 **DIP**，
两个角都要换算，否则高 DPI 下判定框是错的。

### 13. 设备分块粒度取决于音频设备

实测蓝牙耳机（OPPO Enco Clip2）约 **62 ms/块（16.7 次/秒）**，
有线/USB 设备通常接近引擎周期（~10 ms）。
可视化控件自带 60 fps 指数插值（起 22 ms / 落 85 ms），
所以即使上游只有 16 Hz 刷新，观感依然是连续的。

### 14. 不要用 WinForms 模态框接异常

托盘/设置窗口跑在 WinForms 消息循环里，未处理异常默认弹
"Microsoft .NET 应用程序的组件中发生了未经处理的异常" 模态框，既难看又断掉调试现场。
`App.xaml.cs` 里统一挂 `DispatcherUnhandledException` + `AppDomain.UnhandledException` +
`WinForms.Application.ThreadException`，改为写 `%LOCALAPPDATA%\alpha-island\error.log`。

### 15. 常驻置顶小窗（屏幕右上角占用灯）的四条纪律

`Interop\PrivacyIndicatorWindow.cs` 是屏幕右上角的麦克风 / 摄像头占用灯，它踩到的坑对任何「常驻置顶小窗」都成立：

* **不要用 `SizeToContent`**：`SizeToContent.WidthAndHeight` 实测把窗口算成 `136×39`（内容被拉到整个窗口矩形、垂直居中，8px 的点跑到 y=16 而不是 6，右下留一大片透明区）。要么 `SizeToContent=Manual` + 自己算宽高再 `Left/Top`，要么在 `SizeChanged` 里重新定位（`Reposition()` 用的是 `Width/Height`，不能用 `ActualWidth/ActualHeight`）。
* **点击穿透 / 不抢焦点只能靠 ex-style，而且要在 `SourceInitialized` 之后设**：`WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`。`Topmost=true` 会带来 `WS_EX_TOPMOST`、`AllowsTransparency=true` 会带来 `WS_EX_LAYERED`，实测整值 = `0x080800A8`。WPF 的 `IsHitTestVisible=false` 只挡窗口内部的命中测试，挡不住别的进程点过来。
* **独立窗照样受 `ShutdownMode=OnLastWindowClose` 管辖**：退出路径（`MainWindow.xaml.cs` 的 `ExitApplication()`）必须显式 `Dispose()`/`Close()` 它，否则主窗关了进程还在。
* **缩放进出场要缩内容、不要缩窗口**：给胶囊 `Border` 挂 `ScaleTransform` + `RenderTransformOrigin=(0.5,0.5)`（就是「由中心点放大出现」，窗口 `Left/Top/Width/Height` 全程不动，点击穿透的 ex-style 与右上角位置都不用重算）。**别用回弹缓动**：胶囊正好等于窗口矩形，`BackEase{Amplitude=0.5}` 实测过冲到 `1.13`，两端圆角会被窗口边缘切平；用 `CubicEase`（出场 `EaseOut` 220ms、退场 `EaseIn` 170ms）。退场那一拍还要**保持最后一帧的样子**（`SetActive` 里先算 `willHide = !want && _shown`，别在 `HideCore()` 之前就把点 `Collapsed`、把宽度缩回去，否则动画缩的是一个空胶囊）。

### 16. 卡片 / 滚轮 / 手机连接 / 卡片置顶 的四条纪律

四条用户报的交互问题里，有三条的根因都不在用户以为的地方，记下来免得下次再踩：

* **「点开总是剪切板」的根因是一条永远成立的兜底**：`CardPageForNotifyLayer()` 本来就按「此刻弹着什么」把图层映射到卡片页，但它后面的兜底是 `else if (_clipboard?.History.Count > 0) → 剪切板页`，而剪贴板历史几乎永远非空 ⇒ 所有入口都被它抢走。改成「没有通知可挑就留在上一张卡」（`MainWindow.Geometry.cs`）。点击 / 悬停入口只调 `SetState`，选页发生在形变块里 ⇒ **改一处就够**，不用动输入路径。
* **卡片切换入口统一到岛外右侧轮廓线**：旧圆点及其布局、点击和数据接口已移除。线条处于岛体裁切之外，绑定岛体尺寸、圆角和形变变换；加宽的命中区域与悬停热区也包含它，并避免被置顶提示抢走点击。长按菜单从近处向外分层排列，悬停只预选并显示名称，松手才打开目标页；移出缓冲范围立即取消。正文区域仍保留剪切板 / AI 内层滚动、音量页调音量，以及通知正文阅读的行为。
* **`"phone": []` 的意思是「对方还没把手机写进数组」，不是「没连」**：vivo 套件刚连上时先写 `last_time_phone`（设备名 / IP / `lastSuccessTime`），`"phone"` 数组随后才有内容 ⇒ 只认数组的判据会在连接那一刻判「未连接」，弹出与套件相悖的「手机已断开」。判据要「**硬证据** = 数组非空 **或** **软证据** = 记录时间戳在 90s 窗口内」，而且**软证据每次采样都要按当前时间重算**（把整份结果缓存下来会把它冻在最后一次写入）。**别拿文件 mtime 当软证据**：对方进程掉线时也会重写这个文件。断开提示只在「丢掉硬证据」时报，软证据自然过期不报。
* **卡片右键要钉「这一页」，而不是整岛**：老代码 `PinTargetLayer()` 第一行是 `if (_state != IslandState.Pill) return null;` ⇒ 卡片态永远退化成整岛 pin，而整岛 pin 又挡不住 Card→Pill 降级，观感就是「只顶置音乐岛」。现在卡片态用 `_pinnedCardPage`（顺手把它对应的通知图层也钉住；音乐页没有对应图层就只钉卡片），并在**三个地方**释放：离开卡片态 / 自动收回 / 换页；保持展开的两道闸门（悬停降级、点岛外）都要先问 `IsCardPagePinned()`。

---

## 已验证

* SMTC 元数据 / 封面 / 进度 / 播放控制 —— 实测与系统当前媒体会话同步（曲名、艺术家、专辑、封面、进度全部正确）
* 频谱随真实音乐跳动（连续两帧截图字节不同）
* 歌词整链路（独立单测台 + 实机）：

  | 用例 | 结果 |
  |---|---|
  | 摇摆 / 墨老板·洛天依 | 36 行同步歌词 + 5 行逐字 |
  | TsunTsun / 23.exe·初音ミク | 58 行，中文翻译正确 |
  | Lemon / 米津玄師 | 42 行，中文翻译正确 |
  | 逆战 / 张杰 | 66 行，**24 行 yrc 逐字** |
  | 卡农 钢琴版 | `null`（`pureMusic` 正确识别） |
  | 晴天 / 周杰伦 | 不当成有翻译（`tlyric` 空行陷阱） |
  | 不存在的歌名 | `null`（不误匹配） |
  | 精度 / 多时间戳 / 过滤 / 翻译对齐 | 全部符合预期 |

* 悬停：细条上抖动 30 次保持展开；移开 ≤360ms 自动收起
* 三段式收回动画实测抓帧：圆球 + Siri 球 → 暗圆吸顶 → 延展成细条
* 托盘图标出现在任务栏可见区（`Alpha Island (灵动岛)` / `SystemTray.NormalButton`）；左键单击打开设置；
  设置窗口在任务栏有独立按钮（`任务栏按钮=True`）
* 展开态（六条频谱 + 歌词同时跑）CPU ≈ **2.7–5.5% 单核**；收起态几乎为 0
* 播放中优雅退出耗时 231 ms，无死锁
* 麦克风 / 摄像头占用：屏幕右上角黑底胶囊里一枚 iOS 橙点（麦克风，`#FF9F0A`）/ 绿点（摄像头，`#30D158`），
  占用结束自动消失；逐像素实测 `mic+cam` 时橙 44px / 绿 44px（位置与手算 `padding=8 / gap=5` 完全吻合）、
  假占用结束后橙 0px / 绿 0px，窗口 ex-style 含 `WS_EX_TRANSPARENT|NOACTIVATE|TOOLWINDOW|TOPMOST`（点不中它、不抢焦点）；
  出现/消失是 iOS 式由中心点缩放（缩放曲线实测单调无过冲：`0.00->0.90->0.98->0.99->1.00`，
  慢放逐像素 `0,0,0,30,44,44,52,52` 长大 / `52,52,52,42,24,0,0,0` 缩回，退场期间窗口宽保持 37 不闪）
* 最小化的岛（细条态）不显示任何内容：`BarIndicatorsEnabled=false` 让 `BarSpeedLayer` 整层不画，
  实测连续 20s 假占用下 `[bar-privacy show=False state=Bar reveal=1.00 mic=True cam=True opacity=0.00]`
  且细条区域截图逐像素橙 0 / 绿 0（占用指示只走屏幕右上角的胶囊）
* 四条交互修正实测（`%TEMP%\alpha_82_reqs.ps1`，真鼠标/滚轮注入，**31 通过 / 0 失败**）：
  ① 点开岛落到「此刻在弹的那张卡」——点击后 `(Cards) 卡片页 → 下载进度（id=download）`，不再被剪切板兜底抢走；
  ② 在线条上滚轮切换卡片——按日志里的 `[pager]` 屏幕矩形把滚轮送过去，向下到 `id=link`、向上回 `id=download`；
  ③ 手机连接不再误报断开——人造「真文件形状 + 新鲜时间戳 + 空 `phone` 数组」弹出的是「手机已连接」，陈旧记录与 90s 软证据过期都不弹任何提示（`[vivo=notify live=0]` 0 次）；
  ④ 右键卡片钉住这一页——`[pin=card-on]` 后光标离开仍保持卡片（`[pin=card-hold-tick]`）、再右键 `[pin=card-off]`；换页 / 收起 / 离开卡片态都会自动释放
* 异常日志 `%LOCALAPPDATA%\alpha-island\error.log` 干净

---

## 后续可做

* 逐字歌词（yrc）已经在解析并驱动擦除，可以进一步做"逐字发光/跳动"
* 紧凑胶囊里直接显示当前歌词（现在是显示曲名）
* 歌词离线缓存（现在只在内存里缓存，重启要重新拉）
* 网易云以外的歌词源（QQ 音乐 / LRCLIB）作为兜底
* 音量条（`AudioSessionManager` 可拿到每进程音量）；麦克风 / 摄像头占用指示已做成屏幕右上角胶囊（见 §15 与 `_shots\IMPL_NOTES.md` §35），还可以再做「点圆点直接跳到系统卡片」
* 设置落盘改成**原子写**（写临时文件 + 替换）并给读取加重试：实测「上一个实例正在退出保存 `options.json` 时启动下一个实例」会让新实例读到写了一半的文件、回落默认值，再在退出时把默认值写回（当场把 `PrivacyIndicatorEnabled` 从 `true` 静默改成 `false`，占用灯直接失效）
* 系统通知镜像、筛选、静默、优先队列与系统事件已经接入；安装与授权步骤见 [通知模块安装与验收](docs/通知安装与验收.md)。系统状态与网速已合并为两行 CPU/RAM 与上传/下载读数。
* 多显示器 & 任意贴边位置
