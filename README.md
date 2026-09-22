# Alpha Island

Windows 版「灵动岛」：屏幕顶部居中的液态玻璃浮岛，监听 **SMTC** 显示当前正在播放的媒体，
接上 **WASAPI Loopback 实时音频频谱**（真波形，不是假动画），并从**网易云**拉同步歌词做岛内卡拉OK 滚动。

---

## 快速开始

```powershell
cd X:\FixtureWorkspace\alpha-island
dotnet run
```

* 鼠标移到岛体上 → 展开成媒体卡片
* 点击岛外任意位置 → 收起
* 换曲瞬间 → 自动弹出紧凑胶囊，6 秒后自动收起
* 右下角托盘图标：双击 / 右键打开设置

---

## 结构

| 文件 | 职责 |
|---|---|
| `Services/SmtcService.cs` | 包装 `Windows.Media.Control` 的 WinRT 投影：媒体会话、封面、播放状态、进度、上一首/下一首/播放暂停/跳转。事件统一派发到 UI 线程。 |
| `Services/AudioSpectrumService.cs` | WASAPI Loopback 采集默认播放设备 → 单声道混音 → 汉宁窗 + FFT(2048) → 64 个对数频段 → dB 归一化 + 快起慢落平滑。 |
| `Services/LyricsService.cs` | 用曲名+艺术家+时长去网易云搜索歌曲 ID（模糊匹配打分），再拉 LRC / 翻译 / 罗马音 / 逐字歌词。换曲才请求一次，结果按 `曲名\|艺术家` 缓存。 |
| `Services/LrcParser.cs` | LRC / yrc 解析器：多时间戳、四种精度、元数据与制作人行过滤、翻译按毫秒对齐、逐字时间解析。 |
| `Controls/SpectrumVisualizer.cs` | 纯 `FrameworkElement`，`OnRender` 画镜像胶囊柱 + 横向色相渐变。`PushFrame` 线程安全；`IsActive=false` 时完全停止逐帧渲染。 |
| `MainWindow.xaml(.cs)` | 三态状态机（细条 / 胶囊 / 卡片）、三段式收回动画 + Siri 球、动画、托盘、悬停轮询、歌词卡拉OK。 |
| `SettingsWindow.xaml(.cs)` | 通透度、可视化开关、灵敏度、歌词开关、翻译开关、常驻胶囊。 |
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

### 12. 全局底层鼠标钩子 (`WH_MOUSE_LL`) 里不能同步等 UI 线程

钩子回调运行在**安装钩子的线程**上，同步阻塞会卡住整机鼠标。
本项目用 `Dispatcher.BeginInvoke` 异步投递，并且
`PointToScreen` 返回的是**物理像素**、`ActualWidth` 是 **DIP**，
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
* 异常日志 `%LOCALAPPDATA%\alpha-island\error.log` 干净

---

## 后续可做

* 逐字歌词（yrc）已经在解析并驱动擦除，可以进一步做"逐字发光/跳动"
* 紧凑胶囊里直接显示当前歌词（现在是显示曲名）
* 歌词离线缓存（现在只在内存里缓存，重启要重新拉）
* 网易云以外的歌词源（QQ 音乐 / LRCLIB）作为兜底
* 音量条 / 麦克风占用指示（`AudioSessionManager` 可拿到每进程音量）
* 通知镜像（`UserNotificationListener`，需要用户授权）
* 多显示器 & 任意贴边位置
