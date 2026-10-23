# TrafficMonitor 桥接与媒体播放器验收

音乐组件现显示为“媒体播放器”，内部 `music` Id 和既有设置键保留，以兼容原置顶与插件配置。播放器监听 Windows 全部媒体会话，支持 Chrome、Edge 和原有音乐软件发布的标题、封面、播放状态及控制。自动模式选择最近开始播放的会话，全部暂停时保留当前来源；点击播放器来源名称右侧的箭头可以手动选择或恢复自动选择。

网站必须向 Windows 发布媒体会话。未发布的页面无法提供可靠的标题、封面、进度或控制；不以系统音频频谱冒充网页媒体信息。上一项、下一项和拖动进度只在来源支持时可用。视频不搜索歌曲歌词，浏览器静音播放也不会被误判为暂停。原生能力参见 [Microsoft 的媒体会话管理器文档](https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionmanager)。

## 旁侧缩略岛

主卡片展开时，左侧显示其他活跃组件的缩略内容：网络包含上下行和 CPU/RAM，媒体包含封面或媒体图标与标题，剪切板包含复制摘要，音量包含百分比。当前主卡片对应的组件不会重复显示在旁侧。置顶组件优先，其余按通知优先级选择。点击缩略岛切换组件并保持展开；外侧位置跟随岛体形变，背景跟随主岛透明度。

## 安装已构建的桥接

桥接包括两个不同进程使用的插件：

| 文件 | 加载方 | 用途 |
| --- | --- | --- |
| `bin/TrafficMonitorBridge/x64/AlphaIsland.TrafficMonitorBridge.dll` | TrafficMonitor x64 | 读取原始监控数据，发布同用户共享内存 |
| `bin/Debug/net8.0-windows10.0.19041.0/plugins/alpha.trafficmonitor/` | Alpha Island | 注册网络数据源，接入内置网速卡片 |

1. 退出 TrafficMonitor，将**原生桥接 DLL**复制到它的 `plugins` 目录，再启动 TrafficMonitor。你这台电脑的目录是 `C:\ExampleTools\TrafficMonitor\plugins`。
2. 在 Visual Studio 普通 Debug 构建解决方案并 F5。接收插件自动部署到 Alpha Island 的输出目录，不需要 MSIX 或证书。
3. 在 Alpha Island **设置 → 插件**启用“TrafficMonitor 网络数据”，保持“使用 TrafficMonitor 数据”开启，并开启系统状态中的网速检测。
4. 进入“系统状态与网络”卡片，来源应显示“来源：TrafficMonitor”，速率和今日流量均在原网速卡片中显示。接收插件仅提供数据，组件菜单的页数保持不变。

原生 DLL 按 **TrafficMonitor V1.86 / ITMPlugin API 7 / x64** 开发。目前只提供 x64 构建；不能放进 32 位 TrafficMonitor。主程序接口在 V1.86 返回版本 **0**，与插件的 API **7** 是两个接口的版本号。接收插件默认禁用；尚未将 DLL 自动写入你的 TrafficMonitor 安装目录，也未重启正在运行的 TrafficMonitor。

再次分发原生桥接时同时保留输出目录中的 `TrafficMonitor-LICENSE.txt`（上游 Anti 996 许可证）和接口作者声明。

## 数据口径

- 连接时使用 TrafficMonitor 的原始 B/s 与今日字节计数；其所选网卡、合并网卡和采样周期会影响结果，岛不会再叠加本机网卡计数。
- 今日流量直接使用绝对字节值；“本次监测”从接入时建立基线，按计数差值累加。跨天、计数回退或生产者重启重新建立基线，不以舍入后的速度积分估算流量。
- 数据超过 5 秒未更新、插件关闭或来源无效时恢复本机采样，并重置来源统计基线、峰值和趋势，避免混用口径。主卡片标明当前来源。
- `history_traffic.dat` 历史字段是 KB，读取时乘 1024；实时接口已经是字节，不再乘 1024。0.1.118 起，今日值仅在桥接已连接且提供有效当日计数时使用；其他情况由本机采样并保存在 daily-traffic.json，不再依赖历史文件作为备用统计。
- 所有网络视图与接收插件的速率和流量均按 1024 换算（1 KB/s = 1024 B/s），保留小数。TrafficMonitor 若设为 bit/s，比较时还需将 bit/s 除以 8 转为 B/s。

接口来源见 [官方插件开发指南](https://github.com/zhongyang219/TrafficMonitor/wiki/插件开发指南)、[V1.86 插件接口](https://github.com/zhongyang219/TrafficMonitor/blob/V1.86/include/PluginInterface.h)、[V1.86 原始值实现](https://github.com/zhongyang219/TrafficMonitor/blob/V1.86/TrafficMonitor/TrafficMonitor.cpp#L1290)、[历史文件实现](https://github.com/zhongyang219/TrafficMonitor/blob/V1.86/TrafficMonitor/HistoryTrafficFile.cpp)。上游接口和许可证保存在 `plugins/TrafficMonitorBridge/upstream`。

## 开发与重建

接收插件是解决方案中的 `TrafficMonitorPlugin`，引用已有插件 SDK。它通过 `RegisterNetworkSource` 接入统一网络读取点，现有内置网络卡、主岛和旁侧岛均从该读取点取数。刷新复用宿主的网络采样节拍，资源由插件服务生命周期管理。

原生 DLL 独立于 C# 日常解决方案：安装 Visual Studio 的“使用 C++ 的桌面开发”后，单独打开 `plugins/TrafficMonitorBridge/TrafficMonitorBridge.vcxproj`，选择 x64 编译。输出复用 `bin/TrafficMonitorBridge/x64`，不生成安装包。当前机器也可用已有的 x64 MinGW：

```powershell
.\tools\build-traffic-monitor-bridge.ps1
```

未将原生项目加入日常解决方案，避免普通 C# 构建要求额外安装 C++ 工具链。当前生成的 DLL 静态链接 MinGW 运行库，约 178 KB。

先构建原生 DLL，再运行离线回归：

```powershell
dotnet run --project tools/RegressionTests/RegressionTests.csproj -c Debug --no-restore
```

回归使用单独的测试共享内存、实际原生 DLL 虚函数与真实接收插件，不改 TrafficMonitor 配置或生产映射。回归不能替代真实浏览器网页和 TrafficMonitor 两程序联动的手工验收。

## 共享内存协议 v1

命名为 `Local\AlphaIsland.TrafficMonitor.v1_<当前用户 SID>`，仅在本机 Windows 会话中共享。接收端只读。生产者通过同名 `_writer` 互斥量串行写入，使用奇偶序号防止读取到半份快照。DLL 没有后台采集线程、网络连接或逐秒文件写入。

80 字节定长、小端，数值单位为字节：

| 偏移 | 类型 | 含义 |
| --- | --- | --- |
| 0 / 4 | uint32 / uint32 | magic `0x4149544d` / 版本 1 |
| 8 / 12 | int32 / uint32 | 写入中为奇数，完成为变化的偶数 / 大小 80 |
| 16 | int64 | UTC FILETIME |
| 24 / 28 | uint32 / uint32 | 生产者 PID / bit 0 表示今日计数可用 |
| 32 / 40 | uint64 / uint64 | 下行 / 上行 B/s |
| 48 / 56 | uint64 / uint64 | 今日下行 / 上行字节 |
| 64 | uint64 | 生产者启动标记 |
| 72 / 76 | uint32 / uint32 | 本地日期 yyyyMMdd / 保留 0 |

接收端检查大小、版本、稳定偶数序号、时间和数值范围。生产者日期不是今天时不将计数当作今日流量；本次累计的 epoch 包含 PID、启动标记和日期。

## 手工验收

1. 在 Chrome 或 Edge 的受支持网页播放媒体：播放器显示正确来源、标题和封面；暂停、继续和来源支持的进度操作正常。
2. 同时打开音乐软件和浏览器：自动模式跟随新开始播放的会话，手动选择可持续停留；视频不会残留上一首歌曲的歌词。
3. 网络和播放器同时置顶：分别展开两者，另一组件以相应缩略内容显示；点击缩略岛可往返切换且不立即收回。
4. 开启减弱动效，再反复展开双岛：旁侧岛仍在主岛外，不发生重叠或透明区域穿透。
5. TrafficMonitor 中选择实际联网网卡，对照原始速率及今日流量。关闭 TrafficMonitor 或禁用接收插件后，来源提示改为“来源：系统”；重开后恢复“来源：TrafficMonitor”，组件菜单始终只保留原有网络组件。
