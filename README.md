**简体中文 | [English](README.en.md)**

# Alpha Island 插件

这里介绍 Alpha Island 的可选插件及其安装方式。按需选择即可，主安装包不附带这些插件。

本页位于 `plugins` 分支，[主程序](https://github.com/l-egend/alpha-island/tree/master) 位于 `master` 分支。当前说明以宿主 **0.2.32** 为基线，适用于 Windows 10 2004（19041）及以上、Windows 11 x64。

<a id="get-plugins"></a>

## 获取插件

从 [插件 Release](https://github.com/l-egend/alpha-island/releases/tag/plugins-v0.2.32) 下载**安装用插件 ZIP**，可选择单个插件或全部插件合并包。源码包供开发使用，不能直接安装。

[下载全部可选插件](https://github.com/l-egend/alpha-island/releases/download/plugins-v0.2.32/AlphaIsland-optional-plugins-0.2.32.zip) · [下载全部插件源码](https://github.com/l-egend/alpha-island/archive/refs/heads/plugins.zip) · [浏览本分支](https://github.com/l-egend/alpha-island/tree/plugins)

<a id="install"></a>

## 安装与更新

1. 打开 Alpha Island 的“设置 → 插件”，点击“打开插件目录”。默认用户目录是 `%LOCALAPPDATA%\alpha-island\plugins`，以按钮实际打开的位置为准。
2. 解压**安装用插件 ZIP**，把包含 `plugin.json` 的整个插件文件夹放进去，保留 DLL、辅助文件及许可。不要只复制一个 DLL，也不要把源码 ZIP 直接当成插件安装。
3. 返回设置，点击“刷新”，找到插件并手动开启；有配置项的插件可点击“插件设置”。
4. 更新插件前先退出 Alpha Island，用新版本的完整目录替换同 ID 的旧目录，再启动程序。卸载可能清除插件专属设置，不必用卸载来完成更新。

以 API 对话为例，目录应类似：

```text
plugins/
└── ai.api-chat/
    ├── plugin.json
    ├── AlphaIsland.Plugins.ApiChat.dll
    ├── AlphaIsland.Plugins.ApiChat.deps.json
    ├── README.md
    └── README.en.md
```

TrafficMonitor 需要安装到**两个不同应用中的两部分**，请按下方条目完成原生桥接和 Alpha Island 插件的安装。

<a id="catalog"></a>

## 插件列表

<a id="api-chat"></a>

### API 对话

在 Alpha Island 中与兼容接口的模型对话，支持流式回答、TXT 人格、语音指令和可选工具调用。

- 版本：**0.1.41**
- 插件 ID：`ai.api-chat`
- 使用条件：准备可用的服务地址、模型名称和 API Key；语音输入还需配置语音识别服务。
- 获取方式：[下载插件 ZIP](https://github.com/l-egend/alpha-island/releases/download/plugins-v0.2.32/ai.api-chat-0.1.41.zip) · [插件源码](examples/ApiChatPlugin) · [使用说明](examples/ApiChatPlugin/README.md)

---

<a id="local-agents"></a>

### 本机 Agent

在 Alpha Island 内向本机可用的 Codex、DeepSeek Harness 或 Antigravity CLI 发送任务，并查看回答和审批。

- 版本：**0.1.30**
- 插件 ID：`ai.local-agents`
- 使用条件：对应工具已安装、登录并能正常使用；发现程序不代表账号或访问权限已经验证。
- 获取方式：[下载插件 ZIP](https://github.com/l-egend/alpha-island/releases/download/plugins-v0.2.32/ai.local-agents-0.1.30.zip) · [插件源码](examples/LocalAgentsPlugin) · [使用说明](examples/LocalAgentsPlugin/README.md)

---

<a id="earbuds"></a>

### 耳机控制

为受支持耳机提供分路电量和控制卡片，按设备能力显示降噪、预设 EQ、游戏模式等选项。

- 版本：**0.1.6**
- 插件 ID：`oppo.pods`
- 使用条件：通常需先在 Windows 中完成配对并连接音频；控制能力随品牌、型号、固件和后端变化。
- 获取方式：[下载插件 ZIP](https://github.com/l-egend/alpha-island/releases/download/plugins-v0.2.32/oppo.pods-0.1.6.zip) · [插件源码](examples/OppoPodsPlugin) · [使用说明](examples/OppoPodsPlugin/README.md)

参考项目: [OppoPodsManager](https://github.com/Zhaoyi-ya/OppoPodsManager)

---

<a id="vivo-phone"></a>

### vivo 手机连接

读取 vivo 办公套件已有的连接记录，显示设备名称、连接方式、时间和内网地址，并提醒新的连接。

- 版本：**0.1.3**
- 插件 ID：`vivo.office`
- 使用条件：本机已安装 vivo 办公套件并产生过连接记录；当前在线判断需要套件运行且提供对应设备信息。
- 获取方式：[下载插件 ZIP](https://github.com/l-egend/alpha-island/releases/download/plugins-v0.2.32/vivo.office-0.1.3.zip) · [插件源码](examples/VivoOfficePlugin) · [使用说明](examples/VivoOfficePlugin/README.md)

---

<a id="device-battery"></a>

### 2.4G 设备电量

读取受支持接收器、标准 HID 和手柄的电池信息，统一显示到 Alpha Island 的电池卡片。

- 版本：**1.1.4**
- 插件 ID：`alpha.device-battery`
- 使用条件：设备或驱动提供受支持的查询协议或电量字段；无需 Python 或额外厂商管理程序。
- 获取方式：[下载插件 ZIP](https://github.com/l-egend/alpha-island/releases/download/plugins-v0.2.32/alpha.device-battery-1.1.4.zip) · [插件源码](examples/DeviceBatteryPlugin) · [使用说明](examples/DeviceBatteryPlugin/README.md)

---

<a id="trafficmonitor"></a>

### TrafficMonitor 网络数据

把 TrafficMonitor 提供的网速和可用的当日流量接入 Alpha Island，适合已同时运行两者、希望复用网络读数的情况。

- 版本：**1.0.2**
- 插件 ID：`alpha.trafficmonitor`
- 使用条件：需安装 Alpha Island 插件和 TrafficMonitor 原生桥接，并运行 TrafficMonitor。
- 获取方式：[下载插件 ZIP](https://github.com/l-egend/alpha-island/releases/download/plugins-v0.2.32/alpha.trafficmonitor-1.0.2.zip) · [插件源码](examples/TrafficMonitorPlugin) · [使用说明](examples/TrafficMonitorPlugin/README.md)

原项目: [TrafficMonitor](https://github.com/zhongyang219/TrafficMonitor)

---

[下载可选 x64 原生桥接](https://github.com/l-egend/alpha-island/releases/download/plugins-v0.2.32/TrafficMonitor-integration-0.2.32.zip)。它与 Alpha Island 插件是两个部分，安装位置见 [桥接说明](Plugins/TrafficMonitorBridge/README.md)。

<a id="clock"></a>

### 时钟示例

显示时间和日期，可调整标题、秒数、12/24 小时制和字号，也可作为插件开发示例。

- 版本：**1.0.1**
- 插件 ID：`example.clock`
- 使用条件：无需账号、网络或额外应用，使用 Windows 当前本地时间。
- 获取方式：[下载插件 ZIP](https://github.com/l-egend/alpha-island/releases/download/plugins-v0.2.32/example.clock-1.0.1.zip) · [插件源码](examples/ClockPlugin) · [使用说明](examples/ClockPlugin/README.md)

---

<a id="development"></a>

## 开发插件

可从[时钟示例](examples/ClockPlugin/README.md#development)和 [SDK 接口](PluginSdk/PluginContracts.cs)开始。`PluginSdk/`、`ConversationCore/` 为独立构建所需的源码快照，普通构建不会自动部署插件。

分发时保留插件及依赖的许可与署名。构建缓存、本地配置、命令历史和本机发布工具不进入源码分支。

## 反馈与贡献

问题、兼容性反馈和新插件建议可通过 [Issues](https://github.com/l-egend/alpha-island/issues)提交。请提供 Alpha Island 版本、插件版本、Windows 版本、相关设备型号和复现步骤；日志与截图先去除密钥和个人内容。
