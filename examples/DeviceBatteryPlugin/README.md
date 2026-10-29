**简体中文 | [English](README.en.md)**

# 2.4G 设备电量

读取受支持接收器、标准 HID 和手柄的电池信息，统一显示到 Alpha Island 的电池卡片。

- 版本：**1.1.4**
- 插件 ID：`alpha.device-battery`
- 宿主插件 API 要求：**6**

## 使用条件

设备或驱动提供受支持的查询协议或电量字段；无需 Python 或额外厂商管理程序。

## 主要功能

- 支持部分 MCHOSE、ATK/VXE、DARKRIM、罗技 HID++ 设备，以及标准 HID 或 Windows 手柄电池报告。
- 与 Windows 蓝牙和耳机插件的读数合并，保留电量来源和在线状态。
- 在电池卡片刷新设备，或在插件设置中调整查询间隔。

## 安装与使用

先按[通用安装步骤](../../README.md#install)放置完整插件目录，再进行以下操作：

1. 连接受支持的接收器、有线设备或手柄，安装并启用插件。
2. 打开主程序的“电池”卡片查看读数；这类数据源不需要另开一张插件卡片。
3. 设备状态变化后可点击卡片刷新，或等候自动刷新。

## 说明与限制

- 接收器已插入不代表外设已开机或在线；未上报时如实显示未知。
- 电压估算带估算标识；手柄电量档位不伪装成精确百分比。
- K5 Ultra、罗技和其他设备各有型号与协议限制；飞智专用 DInput 查询的硬件兼容性尚待验证。查询不改写 DPI、灯效、配对或固件。

协议与接口来源见 [SOURCES.md](SOURCES.md)。

## 从源码构建

在插件源码根目录执行：

```powershell
dotnet build examples/DeviceBatteryPlugin/DeviceBatteryPlugin.csproj -c Release
```

需要 Windows 和 .NET 8 SDK。普通构建只生成文件；安装时仍需手动放置完整插件目录。本机批量打包命令在仓库之外单独维护。

[返回插件列表](../../README.md#device-battery)
