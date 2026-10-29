**简体中文 | [English](README.en.md)**

# 耳机控制

为受支持耳机提供分路电量和控制卡片，按设备能力显示降噪、预设 EQ、游戏模式等选项。

- 版本：**0.1.6**
- 插件 ID：`oppo.pods`
- 宿主插件 API 要求：**6**

## 使用条件

通常需先在 Windows 中完成配对并连接音频；控制能力随品牌、型号、固件和后端变化。

## 主要功能

- 读取左耳、右耳和充电盒等设备实际提供的电量。
- 按能力提供降噪、内置 EQ、游戏模式、空间音效或双设备选项。
- 可设置自动连接、断线重连、首选耳机和型号覆盖。

## 安装与使用

先按[通用安装步骤](../../README.md#install)放置完整插件目录，再进行以下操作：

1. 先在 Windows 蓝牙设置中完成配对；退出可能占用私有控制通道的耳机管理程序。
2. 安装并启用插件，打开“耳机”卡片，点击刷新、选择耳机，再点击连接。
3. 仅使用设备实际显示为可用的控制项；普通连接和读取不会自动改写音效。

## 说明与限制

- 支持部分 OPPO／一加／realme、vivo／iQOO 和华为设备；小米／Redmi、漫步者和 AirPods 的支持范围依后端而定，部分仅能读状态。
- Windows 能播放音频，不等于插件的私有控制通道一定可用。AirPods 目前依赖可解码的明文 BLE 广播，不支持加密广播或 Windows 控制通道。
- 自定义 EQ 编辑、固件升级和手势配置尚未接入；型号资料存在不等于所有功能都已实机验证。

许可与上游修改说明：[LICENSE](LICENSE) · [第三方说明](THIRD-PARTY-NOTICES.md)。

参考项目: [OppoPodsManager](https://github.com/Zhaoyi-ya/OppoPodsManager)

## 从源码构建

在插件源码根目录执行：

```powershell
dotnet build examples/OppoPodsPlugin/OppoPodsPlugin.csproj -c Release
```

需要 Windows 和 .NET 8 SDK。普通构建只生成文件；安装时仍需手动放置完整插件目录。本机批量打包命令在仓库之外单独维护。

[返回插件列表](../../README.md#earbuds)
