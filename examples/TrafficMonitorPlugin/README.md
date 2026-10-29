**简体中文 | [English](README.en.md)**

# TrafficMonitor 网络数据

把 TrafficMonitor 提供的网速和可用的当日流量接入 Alpha Island，适合已同时运行两者、希望复用网络读数的情况。

- 版本：**1.0.2**
- 插件 ID：`alpha.trafficmonitor`
- 宿主插件 API 要求：**6**

## 使用条件

需安装 Alpha Island 插件和 TrafficMonitor 原生桥接，并运行 TrafficMonitor。

## 主要功能

- 作为网络数据来源接入现有网络卡片、主岛和侧岛，不新增独立卡片。
- 读取原始上传、下载速率；桥接提供有效当日数据时，同时接入当日流量。
- 沿用 TrafficMonitor 选择的网卡，便于两边查看同一来源。

## 安装与使用

先按[通用安装步骤](../../README.md#install)放置完整插件目录，再进行以下操作：

1. 将原生桥接安装到 TrafficMonitor 的插件目录并重新启动 TrafficMonitor，详见[原生桥接说明](../../Plugins/TrafficMonitorBridge/README.md)。
2. 把本插件的完整目录安装到 Alpha Island 的插件目录，在设置中刷新并启用。
3. 打开 Alpha Island 的网络来源列表，选择 TrafficMonitor；两程序需使用同一 Windows 用户。

## 说明与限制

- 两个目录不同：原生桥接属于 TrafficMonitor，带 plugin.json 的本插件属于 Alpha Island。
- 仅启用本插件不能代替安装原生桥接；桥接缺失、失效或超过有效期时不会编造数据。
- 此扩展用于复用网络读数，实际资源占用会随配置变化；不提供其他硬件监控数据。

原项目: [TrafficMonitor](https://github.com/zhongyang219/TrafficMonitor)

## 从源码构建

在插件源码根目录执行：

```powershell
dotnet build examples/TrafficMonitorPlugin/TrafficMonitorPlugin.csproj -c Release
```

需要 Windows 和 .NET 8 SDK。普通构建只生成文件；安装时仍需手动放置完整插件目录。本机批量打包命令在仓库之外单独维护。

[返回插件列表](../../README.md#trafficmonitor)
