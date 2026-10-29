# Alpha Island plugins

本仓库的 `plugins` 分支维护 Alpha Island 的七个可选插件源码；[主程序位于 master 分支](https://github.com/l-egend/alpha-island/tree/master)。主程序不包含这些插件，也不会自动安装它们。

当前宿主兼容版本为 **0.2.32**，记录在 `version.json` 中。此分支只保存源码、使用说明及独立构建所需的依赖；二进制插件包另行分发。

## 插件一览

| 插件 | ID | 用途与使用条件 |
| --- | --- | --- |
| [API 对话](examples/ApiChatPlugin/README.md) | `ai.api-chat` | 在 Alpha Island 内连接兼容接口的模型，使用流式对话、人格、语音指令和可选工具；需要自行配置服务地址、模型和密钥。 |
| [本机 Agent](examples/LocalAgentsPlugin/README.md) | `ai.local-agents` | 使用本机已安装并登录的 Codex、DeepSeek Harness 或 Antigravity CLI 开启任务，支持从文件中转站附加文件；程序发现成功不代表账号或权限验证成功。 |
| [耳机控制](examples/OppoPodsPlugin/README.md) | `oppo.pods` | 读取受支持耳机的分路电量，并按型号能力提供降噪、预设 EQ 等控制；需先在 Windows 配对，品牌或型号列入资料不等于所有功能均已实测。 |
| [vivo 手机连接](examples/VivoOfficePlugin/README.md) | `vivo.office` | 只读办公套件已有连接记录，显示设备、方式、时间和内网地址，并提醒新连接；不是手机控制或文件传输工具。 |
| [2.4G 设备电量](examples/DeviceBatteryPlugin/README.md) | `alpha.device-battery` | 读取受支持接收器、标准 HID 和手柄电池，汇入主程序的电池卡片；不支持的设备或未上报数据会如实显示未知。 |
| [TrafficMonitor 数据源](examples/TrafficMonitorPlugin/README.md) | `alpha.trafficmonitor` | 通过可选原生桥接将 TrafficMonitor 网速数据接入网络卡片；需另行运行 TrafficMonitor 并安装对应桥接。 |
| [时钟示例](examples/ClockPlugin/README.md) | `example.clock` | 显示时间和日期，可设置标题、秒数、12/24 小时制和字号；也可作为学习插件接口的起点。 |

所有插件均需手动安装、启用。可只选需要的插件，不要求全部安装；停用会停止相应卡片或后台服务。插件自身版本及 SDK API 版本独立于宿主版本，具体要求见各目录中的 `plugin.json`。

## 使用说明

### API 对话

支持服务商预设、模型选择与连接测试、对话历史及 TXT 人格导入。可选的工具调用和按需状态读取分别受开关控制，不会默认把所有系统信息附加到每次请求。云端模型按对应服务的规则计费；发送的对话、附件及获准工具结果会交给所配置的接口。密钥使用当前 Windows 用户加密保存。

### 本机 Agent

选择提供方、任务目录并输入指令后，创建本插件自己的会话。可以附加文件或文件夹路径，取消时结束本插件拥有的任务进程。它与“显示外部助手正在思考/等待输入”的状态接入是两项独立功能，也不会接管其他应用已打开的对话。仍需具备相应账号额度和访问权限。

### 耳机与设备电量

耳机控制插件负责耳机连接及支持的控制项；2.4G 插件负责接收器、HID 和手柄电池数据。读数会与 Windows 的电池来源合并。接收器已插入不代表外设在线，未知值、档位和电压估算会分别标明，不作为精确百分比。耳机高级功能随型号、固件和后端支持情况变化。

### 手机连接与网络数据源

vivo 插件只读取办公套件写入的必要连接字段；办公套件关闭或断开后，历史信息会明确标为“最近设备”。TrafficMonitor 插件不创建单独卡片，安装原生桥接后在网络来源中选择 TrafficMonitor；不能仅安装托管插件就得到桥接数据。

## 源码构建

需要 Windows 10 2004 或以上、.NET 8 SDK。运行以下命令只生成文件，不会写入应用配置或安装目录：

```powershell
git clone --branch plugins --single-branch https://github.com/l-egend/alpha-island.git alpha-island-plugins
cd alpha-island-plugins
dotnet build examples/ClockPlugin/ClockPlugin.csproj -c Release
```

将命令中的项目路径替换为所需插件即可。普通构建只生成文件，不会自动部署到主程序。发布包在 `packages/<宿主版本>/`，该目录不进入源码版本管理。TrafficMonitor 原生桥接另行打包，需要 x64 MinGW g++。

维护者本机的批量打包、检阅安装和调试命令放在源码目录之外，不随仓库或对应源码包发布。

`PluginSdk/` 和 `ConversationCore/` 是构建依赖的源码快照，便于本目录独立构建。与宿主联合回归时可传入 `-p:AlphaIslandHostRoot=<宿主源码目录>` 使用宿主当前依赖；插件 SDK 的 API 版本与插件自身版本独立于宿主版本。

## 安装

在 Alpha Island 的设置 → 插件中打开插件目录，将所需 ZIP 内含 `plugin.json` 的完整目录解压进去，再刷新并手动启用。替换已加载的 DLL 前先关闭 Alpha Island。此源码目录不会改动已安装的用户插件。

## 目录

| 目录 | 用途 |
| --- | --- |
| `examples/` | 七个可选插件项目 |
| `PluginSdk/`、`ConversationCore/` | 独立构建所需的共享契约与对话组件 |
| `Plugins/TrafficMonitorBridge/` | 可选原生桥接及其来源和许可 |
| `tools/` | 可公开的包校验和隐私检查工具 |
| `packages/` | 发布产物，不进入源码版本管理 |

原始许可与第三方署名保留在对应目录。发布前检查源码和包内容，不要提交 `packages/`、`bin/`、`obj/`、`.vs/`、用户配置或本地工具。
