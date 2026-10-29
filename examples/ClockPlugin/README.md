**简体中文 | [English](README.en.md)**

# 时钟示例

显示时间和日期，可调整标题、秒数、12/24 小时制和字号，也可作为插件开发示例。

- 版本：**1.0.1**
- 插件 ID：`example.clock`
- 宿主插件 API 要求：**2**

## 使用条件

无需账号、网络或额外应用，使用 Windows 当前本地时间。

## 主要功能

- 自定义卡片标题，选择是否显示秒数。
- 切换 12/24 小时制，调整 28～44 DIP 的时间字号。
- 卡片未激活或不可见时暂停界面计时任务。

## 安装与使用

先按[通用安装步骤](../../README.md#install)放置完整插件目录，再进行以下操作：

1. 安装并启用时钟示例插件。
2. 从组件菜单打开时钟卡片。
3. 在插件设置中调整标题、时间格式和字号。

## 说明与限制

- 时间和日期取自系统，界面语言跟随主程序。
- 示例展示卡片、设置、后台服务和调度生命周期；普通构建不会自动安装到主程序。

<a id="development"></a>

## 开发示例

在 [ClockPlugin.cs](ClockPlugin.cs) 中查看卡片、设置、服务和调度任务的注册方式，接口见 [PluginContracts.cs](../../PluginSdk/PluginContracts.cs)。开发新插件时使用唯一 ID，并让清单中的程序集和入口类型与代码一致。

## 从源码构建

在插件源码根目录执行：

```powershell
dotnet build examples/ClockPlugin/ClockPlugin.csproj -c Release
```

需要 Windows 和 .NET 8 SDK。普通构建只生成文件；安装时仍需手动放置完整插件目录。本机批量打包命令在仓库之外单独维护。

[返回插件列表](../../README.md#clock)
