# TrafficMonitor 数据源

插件 ID：`alpha.trafficmonitor`。

把 TrafficMonitor 的上传和下载速率接入 Alpha Island 的网络来源列表，不新增独立卡片。

需要自行安装并运行 TrafficMonitor，并将单独提供的原生桥接安装到它的插件目录。然后启用本托管插件，在 Alpha Island 网络来源中选择 TrafficMonitor。原生桥接位于 `Plugins/TrafficMonitorBridge/`，来源和许可随该目录保留。

此插件只读取桥接提供的共享内存快照，不读取浏览器记录或数据包内容。未运行、未安装桥接或数据失效时，不会编造速率；可改回主程序的系统网络来源。插件设置可关闭此数据源，停用后停止读取。

安装此目录中的 DLL 不能代替安装 TrafficMonitor 或原生桥接。不要把原生桥接包当作普通 Alpha Island 插件目录安装。
