**简体中文 | [English](README.en.md)**

# TrafficMonitor 原生桥接

这是安装到 **TrafficMonitor** 中的 x64 原生组件，用于把其网速和可用的当日流量交给 Alpha Island。它不带 Alpha Island 的 `plugin.json`，也不能代替 Alpha Island 端的插件。

## 安装

1. 从单独的原生桥接包取得 `AlphaIsland.TrafficMonitorBridge.dll` 和随附许可。
2. 退出 TrafficMonitor，把桥接 DLL 放入其程序目录下的 `plugins` 文件夹，再重新启动 TrafficMonitor；应使用与桥接兼容的 x64 版本。
3. 在 Alpha Island 中另行安装并启用 [TrafficMonitor 网络数据插件](../../examples/TrafficMonitorPlugin/README.md)，然后在网络来源列表中选择 TrafficMonitor。
4. 两程序使用同一 Windows 用户运行。若等待桥接，先确认 TrafficMonitor 已加载 DLL，再检查 Alpha Island 的数据源选择。

## 从源码构建

使用 Visual Studio 的“使用 C++ 的桌面开发”工作负载打开 `TrafficMonitorBridge.vcxproj`，选择 x64 构建。项目使用 C++17 和 v143 工具集；本机维护者也可使用已适配的 x64 MinGW 工具链。普通 C# 插件构建不会自动构建或安装此桥接。

上游接口与许可保留在 [upstream/](upstream/)，请同时保留署名、许可和对应源码。Alpha Island 端插件与本组件属于两个不同的插件目录。
